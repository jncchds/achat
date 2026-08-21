using System.Collections.Concurrent;
using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// The rolling-summary worker (§5.3, §5.5). A background service — not in the request
/// path, a turn never waits on it. It rewrites the conversation's single
/// <see cref="ConversationSummary"/> row in place when the un-summarised tail exceeds
/// the configured token threshold: previous summary + newly aged-out messages →
/// rewritten summary.
///
/// Uses the preset's utility model when set (else the generation model, §3), and logs
/// every call — success or failure — to the interaction log (§4.4, purpose
/// <see cref="InteractionPurpose.Summarisation"/>). A failed pass leaves the watermark
/// untouched, so the same tail is retried on a later sweep.
/// </summary>
public sealed class SummaryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<SummaryService> _log;
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    public SummaryService(IServiceScopeFactory scopes, IConfiguration config, ILogger<SummaryService> log)
    {
        _scopes = scopes;
        _config = config;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Summary service started (sweep every {Interval}s, tail threshold {Threshold} tokens)",
            SweepInterval.TotalSeconds, _config.GetValue("Chat:SummaryTailTokens", 800));
        try
        {
            using var timer = new PeriodicTimer(SweepInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await SweepAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // The sweep is best-effort; a failed sweep retries on the next tick.
                    _log.LogWarning(ex, "Summary sweep failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<IApiKeyProtector>();
        var factory = scope.ServiceProvider.GetRequiredService<ILlmProviderFactory>();

        // Conversations with enough un-summarised tail to be worth a pass.
        var conversations = await db.Conversations
            .AsNoTracking()
            .Where(c => db.Messages.Count(m => m.ConversationId == c.Id) >= 2)
            .Select(c => new { c.Id, c.BotId, c.UserId })
            .ToListAsync(ct);

        var summaries = await db.ConversationSummaries
            .AsNoTracking()
            .ToDictionaryAsync(s => (s.BotId, s.UserId), s => s.CoversUpToMessageId, ct);

        foreach (var conv in conversations)
        {
            var watermark = summaries.TryGetValue((conv.BotId, conv.UserId), out var upTo) ? upTo : 0;
            var tailCount = await db.Messages.CountAsync(m => m.ConversationId == conv.Id && m.Id > watermark, ct);
            if (tailCount < 2) continue;

            if (!_inFlight.TryAdd(conv.Id, 0)) continue;
            try
            {
                await SummariseConversationAsync(db, keys, factory, conv.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Summarisation failed for conversation {ConversationId}", conv.Id);
            }
            finally
            {
                _inFlight.TryRemove(conv.Id, out _);
            }
        }
    }

    private async Task SummariseConversationAsync(
        AppDbContext db, IApiKeyProtector keys, ILlmProviderFactory factory,
        int conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return;

        var summary = await db.ConversationSummaries
            .FirstOrDefaultAsync(s => s.BotId == conversation.BotId && s.UserId == conversation.UserId, ct);
        var watermark = summary?.CoversUpToMessageId ?? 0;
        var tail = await db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Id > watermark)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        if (tail.Count < 2) return;

        var tailTokens = tail.Sum(m => PromptAssembly.EstimateTokens(m.Content));
        var threshold = _config.GetValue("Chat:SummaryTailTokens", 800);
        if (tailTokens <= threshold) return;

        var bot = await db.Bots.Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == conversation.BotId, ct);
        if (bot?.Preset is null) return;
        var preset = bot.Preset;
        if (string.IsNullOrWhiteSpace(preset.GenerationModel)) return;

        string? apiKey = null;
        if (preset.ApiKeyEncrypted is not null)
        {
            try { apiKey = keys.Unprotect(Convert.ToBase64String(preset.ApiKeyEncrypted)); }
            catch { apiKey = null; }
        }

        var model = string.IsNullOrWhiteSpace(preset.UtilityModel) ? preset.GenerationModel : preset.UtilityModel;
        var provider = factory.Create(new LlmPresetConfig
        {
            Provider = preset.Provider,
            Endpoint = preset.BaseEndpoint,
            ApiKey = apiKey,
            GenerationModel = preset.GenerationModel,
            UtilityModel = preset.UtilityModel,
            EmbeddingModel = preset.EmbeddingModel,
            Temperature = preset.Temperature,
            MaxTokens = preset.MaxTokens,
            TimeoutMs = preset.TimeoutMs ?? 120_000,
        });

        var previous = summary?.Content is { Length: > 0 } txt ? txt : "(none — this is the start of the relationship)";
        var transcript = string.Join("\n", tail.Select(m =>
            (m.Role == MessageRole.User ? "user" : "friend") + ": " + m.Content));

        var system = "You maintain a rolling narrative summary of an ongoing conversation between a virtual " +
            $"friend named {bot.Name} and their user. Rewrite the summary to cover the full history. " +
            "Write in the third person, from a neutral observer's view of the relationship. " +
            "Record every personal detail the user has shared — names (of people, pets, places), " +
            "dates, plans, preferences, and what has been decided or promised — even details that seem " +
            "minor; the friend may be asked about any of them later. Also preserve ongoing threads and " +
            "the emotional tone. Do not invent details. End with what is currently unresolved or expected. " +
            "Respond with the summary text only.";

        // 1024: reasoning models (Qwen3 et al.) spend tokens on thinking before
        // content — the cap must cover both, or the summary truncates mid-sentence.
        var maxTokens = _config.GetValue("Chat:SummaryMaxTokens", 1024);
        var userPrompt = "Previous summary:\n" + previous +
            "\n\nNew messages since then (oldest first):\n" + transcript +
            $"\n\nWrite the updated summary (under {maxTokens} tokens).";

        var requestMessages = new List<LlmChatMessage>
        {
            new(MessageRole.System, system),
            new(MessageRole.User, userPrompt),
        };

        var request = new LlmChatRequest
        {
            Messages = requestMessages,
            Model = model,
            Temperature = _config.GetValue("Chat:SummaryTemperature", 0.3f),
            MaxTokens = maxTokens,
        };

        var now = DateTime.UtcNow;
        string? newSummary = null;
        string? failure = null;
        bool failed = false;
        LlmCompletion? completion = null;

        try
        {
            completion = await provider.ChatAsync(request, ct);
            newSummary = (completion.Content ?? "").Trim();
            if (newSummary.Length == 0)
                throw new LlmProviderException("Summarisation returned an empty summary.", null, null);
            // A model that spent its whole budget (thinking models burn tokens on
            // reasoning before content) truncates mid-sentence. Never let a cut-off
            // summary replace a good one.
            if (completion.FinishReason == "length")
                throw new LlmProviderException(
                    "Summary truncated at the token cap (finish_reason=length) — raise Chat:SummaryMaxTokens " +
                    "or lower Chat:SummaryTailTokens.", null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LlmProviderException ex)
        {
            failed = true;
            failure = ex.HttpStatus is { } s
                ? $"Provider returned HTTP {s}: {Truncate(ex.ErrorPayload, 2000)}"
                : ex.Message;
        }
        catch (Exception ex)
        {
            failed = true;
            failure = ex.Message;
        }

        if (!failed && newSummary is not null)
        {
            if (summary is null)
            {
                summary = new ConversationSummary
                {
                    BotId = conversation.BotId,
                    UserId = conversation.UserId,
                };
                db.ConversationSummaries.Add(summary);
            }
            summary.Content = newSummary;
            summary.CoversUpToMessageId = tail.Max(m => m.Id);
            summary.TokenCount = PromptAssembly.EstimateTokens(newSummary);
            summary.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            _log.LogInformation("Rewrote summary for conversation {ConversationId} — now covers up to message {MaxId}",
                conversationId, summary.CoversUpToMessageId);
        }

        // Every call is logged, success or failure (§4.4).
        db.LlmInteractions.Add(new LlmInteraction
        {
            Purpose = InteractionPurpose.Summarisation,
            BotId = bot.Id,
            UserId = conversation.UserId,
            PresetId = preset.Id,
            Provider = preset.Provider,
            Model = model,
            Endpoint = string.IsNullOrWhiteSpace(preset.BaseEndpoint) ? "(default)" : preset.BaseEndpoint,
            RequestBody = SerializeRequest(requestMessages),
            ResponseContent = newSummary ?? failure,
            PromptTokens = completion?.PromptTokens,
            CompletionTokens = completion?.CompletionTokens,
            TotalTokens = completion?.TotalTokens,
            FinishReason = completion?.FinishReason,
            Failed = failed,
            FailureReason = failure,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    private static string SerializeRequest(IReadOnlyList<LlmChatMessage> messages) =>
        System.Text.Json.JsonSerializer.Serialize(messages
            .Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content })
            .ToList());

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
