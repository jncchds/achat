using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// Arms summarisation work (§12b): a conversation's un-summarised tail (messages
/// after <see cref="ConversationSummary.CoversUpToMessageId"/>) exceeds
/// <c>Chat:SummaryTailTokens</c> — the same threshold the handler re-validates,
/// so the queue never arms a pass that is not yet due (which would no-op and
/// re-arm in a loop). No LLM work happens here.
/// </summary>
public sealed class SummaryProducer : IWorkProducer
{
    private readonly IConfiguration _config;
    public WorkKind Kind => WorkKind.Summary;
    public int MaxAttempts { get; }

    public SummaryProducer(IConfiguration config)
    {
        _config = config;
        MaxAttempts = config.GetValue("Work:MaxAttempts", 5);
    }

    public async Task ProduceAsync(AppDbContext db, CancellationToken ct)
    {
        var threshold = _config.GetValue("Chat:SummaryTailTokens", 800);

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
            var tail = await db.Messages
                .AsNoTracking()
                .Where(m => m.ConversationId == conv.Id && m.Id > watermark)
                .Select(m => m.Content)
                .ToListAsync(ct);
            if (tail.Count < 2) continue;
            if (tail.Sum(PromptAssembly.EstimateTokens) <= threshold) continue;

            // A preset that cannot generate is a configuration state, not a failure.
            var canGenerate = await db.Bots
                .Where(b => b.Id == conv.BotId)
                .Select(b => b.Preset.GenerationModel != "" && b.Preset.GenerationModel != null)
                .FirstOrDefaultAsync(ct);
            if (!canGenerate) continue;

            await WorkQueue.UpsertAsync(db, Kind, conv.Id, MaxAttempts, ct);
        }
    }
}

/// <summary>
/// The rolling-summary pass (§5.3, §5.5 — executed by the durable queue per §12b).
/// Rewrites the conversation's single <see cref="ConversationSummary"/> row in
/// place: previous summary + newly aged-out messages → rewritten summary.
///
/// Uses the preset's utility model when set (else the generation model, §3), and
/// logs every call — success or failure — to the interaction log (§4.4, purpose
/// <see cref="InteractionPurpose.Summarisation"/>).
///
/// <para>
/// The prompt is bounded: <c>Chat:SummaryMaxInputTokens</c> covers the instructions, the
/// previous summary and the new messages. A tail that does not fit is summarised in
/// steps, oldest first; each step folds its messages into the summary and advances the
/// watermark (<see cref="ConversationSummary.CoversUpToMessageId"/>) together, so the
/// summary always matches what it covers and a later failure loses nothing. At most
/// <c>Chat:SummaryMaxSteps</c> steps run per pass; the producer re-arms the rest. A
/// context-length rejection or a <c>finish_reason=length</c> truncation is deterministic,
/// so the step is halved once (for the rest of the pass); if that does not cure it the
/// item is dead-lettered at once (<see cref="WorkOutcome.FailPermanent"/>) with an
/// actionable reason. Any other failure retries with backoff (§12b).
/// </para>
/// </summary>
public sealed class SummaryHandler : IWorkHandler
{
    private const string NoSummary = "(none — this is the start of the relationship)";

    private readonly IApiKeyProtector _keys;
    private readonly ILlmProviderFactory _factory;
    private readonly IConfiguration _config;
    private readonly ILogger<SummaryHandler> _log;

    public WorkKind Kind => WorkKind.Summary;

    public SummaryHandler(
        IApiKeyProtector keys, ILlmProviderFactory factory,
        IConfiguration config, ILogger<SummaryHandler> log)
    {
        _keys = keys;
        _factory = factory;
        _config = config;
        _log = log;
    }

    public async Task<WorkOutcome> ExecuteAsync(AppDbContext db, WorkItem item, CancellationToken ct)
    {
        var conversation = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == item.ConversationId, ct);
        if (conversation is null) return WorkOutcome.Ok; // gone — stale claim

        var bot = await db.Bots.Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == conversation.BotId, ct);
        if (bot?.Preset is null) return WorkOutcome.Ok; // the bot or its preset is gone — stale claim
        if (string.IsNullOrWhiteSpace(bot.Preset.GenerationModel)) return WorkOutcome.Ok; // configuration state

        // Re-validate the trigger: the tail may have shrunk (a stale claim) — then
        // no-op succeeds instead of burning another attempt.
        var summary = await db.ConversationSummaries
            .FirstOrDefaultAsync(s => s.BotId == conversation.BotId && s.UserId == conversation.UserId, ct);
        var watermark = summary?.CoversUpToMessageId ?? 0;
        var tail = await db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Id > watermark)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
        var threshold = _config.GetValue("Chat:SummaryTailTokens", 800);
        if (tail.Count < 2 || tail.Sum(m => PromptAssembly.EstimateTokens(m.Content)) <= threshold)
            return WorkOutcome.Ok;

        return await SummariseAsync(db, conversation, bot, summary, tail, ct);
    }

    /// <summary>The outcome of one provider call: the new summary, or a failure (shrinkable = deterministic).</summary>
    private sealed record CallResult(string? Summary, string? Failure, bool Shrinkable);

    private async Task<WorkOutcome> SummariseAsync(
        AppDbContext db, Conversation conversation, Bot bot, ConversationSummary? summary,
        List<Message> tail, CancellationToken ct)
    {
        var preset = bot.Preset!;

        string? apiKey = null;
        if (preset.ApiKeyEncrypted is not null)
        {
            try { apiKey = _keys.Unprotect(Convert.ToBase64String(preset.ApiKeyEncrypted)); }
            catch { apiKey = null; }
        }

        var model = string.IsNullOrWhiteSpace(preset.UtilityModel) ? preset.GenerationModel : preset.UtilityModel;
        var provider = _factory.Create(new LlmPresetConfig
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

        var system = BuildSystemPrompt(bot.Name);
        var maxInput = _config.GetValue("Chat:SummaryMaxInputTokens", 4000);
        var target = _config.GetValue("Chat:SummaryTargetTokens", 800);
        // The output cap must cover thinking + the summary: reasoning models (Qwen3, Nemotron)
        // spend tokens on reasoning before content, so it is well above the target length.
        var maxOutput = _config.GetValue("Chat:SummaryMaxTokens", 4096);
        var maxSteps = Math.Max(1, _config.GetValue("Chat:SummaryMaxSteps", 8));

        var steps = 0;
        int? stepCap = null; // set once a deterministic failure halved the step
        var remaining = tail;
        while (remaining.Count > 0 && steps < maxSteps)
        {
            var previous = summary?.Content is { Length: > 0 } txt ? txt : NoSummary;
            var fixedTokens = PromptAssembly.EstimateTokens(system)
                + PromptAssembly.EstimateTokens(BuildUserPrompt(previous, "", target));
            var available = maxInput - fixedTokens;
            if (available < 64)
            {
                var why = $"Summary skipped: the instructions and the previous summary take about {fixedTokens} tokens " +
                          $"of the {maxInput}-token input budget — raise Chat:SummaryMaxInputTokens or lower " +
                          "Chat:SummaryTargetTokens.";
                await LogAsync(db, bot, conversation, model, "[]", why, null, failed: true, ct);
                return WorkOutcome.FailPermanent(why);
            }

            // One message larger than the whole budget is clipped (its start is kept) rather than
            // blocking the summary forever.
            var clipChars = Math.Max(400, available * 3);
            var chunk = TokenLimits.Chunk(remaining, m => PromptAssembly.EstimateTokens(Line(m, clipChars)), available)[0];
            if (stepCap is { } cap && chunk.Count > cap) chunk = chunk.Take(cap).ToList();

            var transcript = string.Join("\n", chunk.Select(m => Line(m, clipChars)));
            var res = await CallAsync(db, provider, bot, conversation, model, system,
                BuildUserPrompt(previous, transcript, target), maxOutput, ct);

            if (res.Summary is null)
            {
                if (res.Shrinkable && stepCap is null && chunk.Count >= 2)
                {
                    stepCap = chunk.Count / 2;
                    _log.LogWarning("Summary for conversation {ConversationId}: {Failure} — retrying with {Cap} messages per step",
                        conversation.Id, Truncate(res.Failure, 200), stepCap);
                    continue;
                }
                if (!res.Shrinkable) return WorkOutcome.Fail(res.Failure);
                return WorkOutcome.FailPermanent(res.Failure + (chunk.Count < 2
                    ? " (a single message cannot be shrunk further)"
                    : " (already retried on a smaller input)"));
            }

            // ── Commit the step: summary and watermark move together ──────────
            if (summary is null)
            {
                summary = new ConversationSummary { BotId = conversation.BotId, UserId = conversation.UserId };
                db.ConversationSummaries.Add(summary);
            }
            summary.Content = res.Summary;
            summary.CoversUpToMessageId = chunk.Max(m => m.Id);
            summary.TokenCount = PromptAssembly.EstimateTokens(res.Summary);
            summary.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            steps++;
            remaining = remaining.Skip(chunk.Count).ToList();
        }

        _log.LogInformation("Rewrote summary for conversation {ConversationId} in {Steps} step(s) — now covers up to message {MaxId}{More}",
            conversation.Id, steps, summary?.CoversUpToMessageId, remaining.Count > 0 ? $" ({remaining.Count} messages left for the next pass)" : "");
        return WorkOutcome.Ok;
    }

    private async Task<CallResult> CallAsync(
        AppDbContext db, ILlmProvider provider, Bot bot, Conversation conversation, string model,
        string system, string userPrompt, int maxOutput, CancellationToken ct)
    {
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
            MaxTokens = maxOutput,
        };

        string? raw = null, newSummary = null, failure = null;
        bool failed = false, shrinkable = false;
        int? httpStatus = null;
        LlmCompletion? completion = null;

        try
        {
            completion = await provider.ChatAsync(request, ct);
            raw = completion.Content ?? "";
            if (completion.FinishReason == "length")
            {
                // Cut off at the output cap (a reasoning model spends it thinking). Never let a
                // cut-off summary replace a good one.
                failed = shrinkable = true;
                failure = $"Summary truncated at the output cap (finish_reason=length, Chat:SummaryMaxTokens={maxOutput}) — " +
                          "raise Chat:SummaryMaxTokens or use a non-reasoning utility model.";
            }
            else
            {
                newSummary = TokenLimits.StripThinking(raw);
                if (newSummary.Length == 0)
                {
                    failed = true;
                    failure = "Summarisation returned an empty summary.";
                    newSummary = null;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LlmProviderException ex)
        {
            failed = true;
            httpStatus = ex.HttpStatus;
            shrinkable = TokenLimits.IsContextLengthError(ex);
            failure = ex.HttpStatus is { } s ? $"Provider returned HTTP {s}: {Truncate(ex.ErrorPayload, 2000)}" : ex.Message;
            if (shrinkable)
                failure += " — the prompt exceeds the model's context window; lower Chat:SummaryMaxInputTokens.";
        }
        catch (Exception ex)
        {
            failed = true;
            failure = ex.Message;
        }
        if (failed) _log.LogWarning("Summary for conversation {ConversationId} failed: {Failure}", conversation.Id, Truncate(failure, 300));

        // Every call is logged, success or failure (§4.4); the raw response is kept as returned.
        await LogAsync(db, bot, conversation, model, SerializeRequest(requestMessages),
            failed ? (string.IsNullOrEmpty(raw) ? failure : raw) : raw, completion, failed, ct, failure, httpStatus);

        return failed ? new CallResult(null, failure, shrinkable) : new CallResult(newSummary, null, false);
    }

    private static async Task LogAsync(
        AppDbContext db, Bot bot, Conversation conversation, string model, string requestBody, string? response,
        LlmCompletion? completion, bool failed, CancellationToken ct, string? failure = null, int? httpStatus = null)
    {
        var preset = bot.Preset!;
        db.LlmInteractions.Add(new LlmInteraction
        {
            Purpose = InteractionPurpose.Summarisation,
            BotId = bot.Id,
            UserId = conversation.UserId,
            PresetId = preset.Id,
            Provider = preset.Provider,
            Model = model,
            Endpoint = string.IsNullOrWhiteSpace(preset.BaseEndpoint) ? "(default)" : preset.BaseEndpoint,
            RequestBody = requestBody,
            ResponseContent = response,
            PromptTokens = completion?.PromptTokens,
            CompletionTokens = completion?.CompletionTokens,
            TotalTokens = completion?.TotalTokens,
            FinishReason = completion?.FinishReason,
            Failed = failed,
            HttpStatus = httpStatus,
            FailureReason = failed ? (failure ?? response) : null,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    internal static string BuildSystemPrompt(string botName) =>
        "You maintain a rolling narrative summary of an ongoing conversation between a virtual " +
        $"friend named {botName} and their user. Rewrite the summary to cover the full history. " +
        "Write in the third person, from a neutral observer's view of the relationship. " +
        "Record every personal detail the user has shared — names (of people, pets, places), " +
        "dates, plans, preferences, and what has been decided or promised — even details that seem " +
        "minor; the friend may be asked about any of them later. Also preserve ongoing threads and " +
        "the emotional tone. Do not invent details. End with what is currently unresolved or expected. " +
        "Respond with the summary text only.";

    internal static string BuildUserPrompt(string previous, string transcript, int targetTokens) =>
        "Previous summary:\n" + previous +
        "\n\nNew messages since then (oldest first):\n" + transcript +
        $"\n\nWrite the updated summary (under {targetTokens} tokens).";

    private static string Line(Message m, int clipChars)
    {
        var text = m.Content.Length <= clipChars ? m.Content : m.Content[..clipChars] + " …[cut]";
        return (m.Role == MessageRole.User ? "user" : "friend") + ": " + text;
    }

    private static string SerializeRequest(IReadOnlyList<LlmChatMessage> messages) =>
        System.Text.Json.JsonSerializer.Serialize(messages
            .Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content })
            .ToList());

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
