using System.Collections.Concurrent;
using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// Personality-evolution worker (§2.4). Maintains the evolved-traits layer — a short
/// machine-written document describing how the friendship has shaped the bot.
///
/// Deliberately slow: it triggers only when enough <b>creator</b> messages have
/// accumulated since the last pass (<c>Evolution:MinNewMessages</c>) AND the cooldown
/// has elapsed (<c>Evolution:CooldownHours</c>). Personality that shifts hourly is
/// noise, not character. It reads only the creator's conversations — other users'
/// interactions never influence it, even though they see the result.
///
/// Bounded: the rewrite is prompted with core + directives as fixed constraints and
/// produces only the evolved layer. Every version is stored (versioning is required,
/// not optional — the admin can read the history and revert); a human edit or revert
/// appends a new version and becomes the next pass's baseline.
///
/// Runs on the utility model and is logged to §4.4 with purpose
/// <see cref="InteractionPurpose.PersonaEvolution"/>. The watermark advances only on
/// success; a failed pass leaves it untouched so the same tail retries.
/// </summary>
public sealed class PersonaEvolutionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonaEvolutionService> _log;
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    private static readonly int MaxWords = 160;

    public int MinNewMessages { get; }
    public int CooldownHours { get; }
    public int MaxTailMessages { get; }
    public int MaxTokens { get; }

    public PersonaEvolutionService(
        IServiceScopeFactory scopes, IConfiguration config, ILogger<PersonaEvolutionService> log)
    {
        _scopes = scopes;
        _config = config;
        _log = log;
        MinNewMessages = config.GetValue("Evolution:MinNewMessages", 40);
        CooldownHours = config.GetValue("Evolution:CooldownHours", 12);
        MaxTailMessages = config.GetValue("Evolution:MaxTailMessages", 60);
        MaxTokens = config.GetValue("Evolution:MaxTokens", 512);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "Persona evolution started (trigger ≥ {M} new creator messages + {H}h cooldown)",
            MinNewMessages, CooldownHours);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await SweepAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Evolution sweep failed");
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

        var now = DateTime.UtcNow;
        var cooldownCutoff = now.AddHours(-CooldownHours);

        // Bots with evolution enabled — the watermark tells how far we got last time.
        var bots = await db.Bots
            .AsNoTracking()
            .Where(b => b.EvolutionEnabled)
            .Select(b => new { b.Id, b.OwnerId, b.EvolutionUpToMessageId })
            .ToListAsync(ct);

        foreach (var bot in bots)
        {
            if (!_inFlight.TryAdd(bot.Id, 0)) continue;
            try
            {
                // How many creator messages since the last completed pass?
                var creatorConvIds = await db.Conversations
                    .Where(c => c.BotId == bot.Id && c.UserId == bot.OwnerId)
                    .Select(c => c.Id)
                    .ToListAsync(ct);
                if (creatorConvIds.Count == 0) continue;

                var newCount = await db.Messages
                    .CountAsync(m => creatorConvIds.Contains(m.ConversationId)
                        && m.Id > (bot.EvolutionUpToMessageId ?? 0), ct);

                var lastVersionAt = await db.PersonaVersions
                    .Where(v => v.BotId == bot.Id)
                    .MaxAsync(v => (DateTime?)v.CreatedAt, ct);
                if (!ShouldEvolve(newCount, lastVersionAt, MinNewMessages, cooldownCutoff)) continue;

                await EvolveAsync(db, keys, factory, bot.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Evolution failed for bot {BotId}", bot.Id);
            }
            finally
            {
                _inFlight.TryRemove(bot.Id, out _);
            }
        }
    }

    private async Task EvolveAsync(
        AppDbContext db, IApiKeyProtector keys, ILlmProviderFactory factory,
        int botId, CancellationToken ct)
    {
        var bot = await db.Bots.Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot is null || bot.Preset is null || !bot.EvolutionEnabled) return;
        var preset = bot.Preset;
        if (string.IsNullOrWhiteSpace(preset.GenerationModel)) return;

        // ── Baseline: the current (latest) version + fixed constraints (§2.4) ───
        var currentVersion = await db.PersonaVersions
            .Where(v => v.BotId == botId)
            .OrderByDescending(v => v.Id)
            .FirstOrDefaultAsync(ct);
        var baseline = currentVersion?.Content ?? "";

        var directives = await db.BotDirectives
            .Where(d => d.BotId == botId && d.IsActive)
            .OrderBy(d => d.Id)
            .Select(d => d.Text)
            .ToListAsync(ct);

        // ── The new material: the tail of the creator's messages since the pass ──
        var watermark = bot.EvolutionUpToMessageId ?? 0;
        var tail = await db.Messages
            .AsNoTracking()
            .Where(m => m.Id > watermark
                && m.Conversation.UserId == bot.OwnerId
                && m.Conversation.BotId == botId)
            .OrderByDescending(m => m.Id)
            .Take(MaxTailMessages)
            .ToListAsync(ct);
        tail.Reverse();
        if (tail.Count == 0) return;

        var transcript = string.Join("\n", tail.Select(m =>
            $"{(m.Role == MessageRole.User ? "creator" : bot.Name)} ({m.CreatedAt.ToUniversalTime():ddd d MMM} {m.CreatedAt.Hour:00}:{m.CreatedAt.Minute:00}): {m.Content}"));

        // ── Provider (utility model when set, §3) ─────────────────────────────
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

        // ── The bounded rewrite (§2.4): core + directives are fixed constraints ─
        // Qwen3's chat template requires a user message in the list, so the full
        // brief goes in the user turn and the system message stays a short role.
        var system = "You maintain the evolved-traits layer of a virtual friend's personality. " +
                     "Output only the document text — no headings, no meta-commentary.";

        var brief =
            "Rewrite the evolved-traits document below the constraints.\n" +
            "The core identity and the creator's directives are FIXED — do not restate, " +
            "contradict, or override them in your output. The document describes only what " +
            "the relationship itself has added: shared shorthand and inside references, the " +
            "tone the friendship has settled into, habits of the two of you, how the friend " +
            "has grown because of this friendship.\n\n" +
            $"Core identity (fixed):\n{bot.CorePersona}\n\n" +
            $"Directives (fixed):\n{(directives.Count == 0 ? "(none)" : string.Join("\n", directives.Select(d => "- " + d)))}\n\n" +
            "Current evolved-traits document (your baseline — refine it, do not start from scratch):\n" +
            (string.IsNullOrWhiteSpace(baseline) ? "(none yet — this is the first pass)" : baseline) +
            $"\n\nRecent conversation between {bot.Name} and their creator (excerpt, oldest first):\n{transcript}\n\n" +
            "Rewrite the evolved-traits document as a short prose document (at most about " +
            $"{MaxWords} words). Keep what is still true, add what the recent conversation " +
            "shows has settled, drop what no longer holds. If nothing has changed, return " +
            "the baseline unchanged.";

        var requestMessages = new List<LlmChatMessage>
        {
            new(MessageRole.System, system),
            new(MessageRole.User, brief),
        };

        var request = new LlmChatRequest
        {
            Messages = requestMessages,
            Model = model,
            Temperature = 0.4f,
            MaxTokens = MaxTokens,
        };

        var now = DateTime.UtcNow;
        string? rawResponse = null;
        string? failure = null;
        bool failed = false;
        LlmCompletion? completion = null;

        try
        {
            completion = await provider.ChatAsync(request, ct);
            rawResponse = completion.Content?.Trim() ?? "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failed = true;
            failure = ex is LlmProviderException lpe
                ? (lpe.HttpStatus is { } s ? $"Provider returned HTTP {s}: {Truncate(lpe.ErrorPayload, 2000)}" : lpe.Message)
                : ex.Message;
            _log.LogWarning("Evolution for bot {BotId} failed: {Failure}", botId, Truncate(failure, 300));
        }

        // ── Store a new version + advance the watermark only on success (§2.4) ──
        PersonaVersion? version = null;
        if (!failed && !string.IsNullOrWhiteSpace(rawResponse) && rawResponse != baseline)
        {
            version = new PersonaVersion
            {
                BotId = botId,
                Content = rawResponse,
                Source = PersonaVersionSource.Evolution,
                CreatedAt = now,
            };
            db.PersonaVersions.Add(version);
            var tracked = await db.Bots.FirstAsync(b => b.Id == botId, ct);
            tracked.EvolutionUpToMessageId = tail.Max(m => m.Id);
            await db.SaveChangesAsync(ct); // materialises version.Id
        }

        // ── Log the call, success or failure (§4.4) ────────────────────────────
        var interaction = new LlmInteraction
        {
            Purpose = InteractionPurpose.PersonaEvolution,
            BotId = botId,
            UserId = bot.OwnerId,
            PresetId = preset.Id,
            Provider = preset.Provider,
            Model = model,
            Endpoint = string.IsNullOrWhiteSpace(preset.BaseEndpoint) ? "(default)" : preset.BaseEndpoint,
            RequestBody = SerializeRequest(requestMessages),
            ResponseContent = rawResponse ?? failure,
            PromptTokens = completion?.PromptTokens,
            CompletionTokens = completion?.CompletionTokens,
            TotalTokens = completion?.TotalTokens,
            FinishReason = completion?.FinishReason,
            Failed = failed,
            FailureReason = failure,
            CreatedAt = now,
        };
        db.LlmInteractions.Add(interaction);
        await db.SaveChangesAsync(ct); // materialises interaction.Id

        if (version is not null)
        {
            version.InteractionId = interaction.Id; // the audit trail from §2.4
            await db.SaveChangesAsync(ct);
        }

        if (!failed)
            _log.LogInformation("Evolution for bot {BotId}: {Result}", botId,
                version is null ? "no change (baseline kept)" : "new version written");
    }

    private static string Truncate(string? s, int max) =>
        s is null ? "" : (s.Length <= max ? s : s[..max] + "…");

    private static string SerializeRequest(IReadOnlyList<LlmChatMessage> messages) =>
        System.Text.Json.JsonSerializer.Serialize(messages
            .Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content })
            .ToList());

    /// <summary>
    /// The evolution trigger (§2.4): enough new creator messages AND the cooldown
    /// has elapsed. The cooldown is measured from the newest version of <b>any</b>
    /// source — a human edit or revert also resets it, protecting corrections from
    /// an immediate overwrite.
    /// </summary>
    public static bool ShouldEvolve(
        int newCreatorMessages,
        DateTime? lastVersionAt,
        int minNewMessages,
        DateTime cooldownCutoff)
    {
        if (newCreatorMessages < minNewMessages) return false;
        return lastVersionAt is null || lastVersionAt <= cooldownCutoff;
    }
}
