using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// Arms evolution work (§12b): re-evaluates the trigger — enough <b>creator</b>
/// messages accumulated since the last pass (<c>Evolution:MinNewMessages</c>) AND
/// the cooldown has elapsed (<c>Evolution:CooldownHours</c>) — and upserts a
/// <see cref="WorkItem"/> for the bot's creator conversation (unique per bot,
/// §2.3). No LLM work happens here.
///
/// Deliberately slow: personality that shifts hourly is noise, not character.
/// </summary>
public sealed class EvolutionProducer : IWorkProducer
{
    private readonly IConfiguration _config;
    public WorkKind Kind => WorkKind.Evolution;
    public int MaxAttempts { get; }

    public EvolutionProducer(IConfiguration config)
    {
        _config = config;
        MaxAttempts = config.GetValue("Work:MaxAttempts", 5);
    }

    public async Task ProduceAsync(AppDbContext db, CancellationToken ct)
    {
        var minNewMessages = _config.GetValue("Evolution:MinNewMessages", 40);
        var cooldownHours = _config.GetValue("Evolution:CooldownHours", 12);

        var now = DateTime.UtcNow;
        var cooldownCutoff = now.AddHours(-cooldownHours);

        var bots = await db.Bots
            .AsNoTracking()
            .Where(b => b.EvolutionEnabled)
            .Select(b => new { b.Id, b.OwnerId, b.EvolutionUpToMessageId })
            .ToListAsync(ct);

        foreach (var bot in bots)
        {
            // The creator's conversation is unique per bot (§2.3) — it is the
            // work item's target.
            var convId = await db.Conversations
                .Where(c => c.BotId == bot.Id && c.UserId == bot.OwnerId)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);
            if (convId == 0) continue; // no relationship yet — nothing to evolve from

            // A preset that cannot generate is a configuration state, not a failure.
            var canGenerate = await db.Bots
                .Where(b => b.Id == bot.Id)
                .Select(b => b.Preset.GenerationModel != "" && b.Preset.GenerationModel != null)
                .FirstOrDefaultAsync(ct);
            if (!canGenerate) continue;

            // How many creator messages since the last completed pass?
            var newCount = await db.Messages
                .CountAsync(m => m.ConversationId == convId
                    && m.Id > (bot.EvolutionUpToMessageId ?? 0), ct);

            var lastVersionAt = await db.PersonaVersions
                .Where(v => v.BotId == bot.Id)
                .MaxAsync(v => (DateTime?)v.CreatedAt, ct);
            if (!EvolutionHandler.ShouldEvolve(newCount, lastVersionAt, minNewMessages, cooldownCutoff))
                continue;

            await WorkQueue.UpsertAsync(db, Kind, convId, MaxAttempts, ct);
        }
    }
}

/// <summary>
/// Personality-evolution pass (§2.4, executed by the durable queue per §12b).
/// Maintains the evolved-traits layer — a short machine-written document
/// describing how the friendship has shaped the bot. Reads only the creator's
/// conversation — other users' interactions never influence it.
///
/// Bounded: the rewrite is prompted with core + directives as fixed constraints
/// and produces only the evolved layer. Every version is stored (versioning is
/// required, not optional — the admin can read the history and revert); a human
/// edit or revert appends a new version and becomes the next pass's baseline.
///
/// Runs on the utility model and is logged to §4.4 with purpose
/// <see cref="InteractionPurpose.PersonaEvolution"/>. The watermark advances only
/// on success; a failed pass leaves it untouched so the same tail retries (now
/// with backoff and a cap, §12b).
/// </summary>
public sealed class EvolutionHandler : IWorkHandler
{
    private static readonly int MaxWords = 160;

    private readonly IApiKeyProtector _keys;
    private readonly ILlmProviderFactory _factory;
    private readonly IConfiguration _config;
    private readonly ILogger<EvolutionHandler> _log;

    public WorkKind Kind => WorkKind.Evolution;

    public EvolutionHandler(
        IApiKeyProtector keys, ILlmProviderFactory factory,
        IConfiguration config, ILogger<EvolutionHandler> log)
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
        if (bot is null || bot.Preset is null || !bot.EvolutionEnabled) return WorkOutcome.Ok; // switched off — stale claim
        var preset = bot.Preset;
        if (string.IsNullOrWhiteSpace(preset.GenerationModel)) return WorkOutcome.Ok; // configuration state

        // Re-validate the trigger: a claim may be stale (the watermark advanced,
        // or a human edit/reset re-armed the cooldown) — then no-op succeeds.
        var newCount = await db.Messages
            .CountAsync(m => m.ConversationId == item.ConversationId
                && m.Id > (bot.EvolutionUpToMessageId ?? 0), ct);
        var lastVersionAt = await db.PersonaVersions
            .Where(v => v.BotId == bot.Id)
            .MaxAsync(v => (DateTime?)v.CreatedAt, ct);
        var cooldownCutoff = DateTime.UtcNow.AddHours(-_config.GetValue("Evolution:CooldownHours", 12));
        if (!ShouldEvolve(newCount, lastVersionAt, _config.GetValue("Evolution:MinNewMessages", 40), cooldownCutoff))
            return WorkOutcome.Ok;

        return await EvolveAsync(db, bot.Id, ct);
    }

    private async Task<WorkOutcome> EvolveAsync(AppDbContext db, int botId, CancellationToken ct)
    {
        var bot = await db.Bots.Include(b => b.Preset)
            .FirstAsync(b => b.Id == botId, ct);
        var preset = bot.Preset!;
        var maxTailMessages = _config.GetValue("Evolution:MaxTailMessages", 60);

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
            .Take(maxTailMessages)
            .ToListAsync(ct);
        tail.Reverse();
        if (tail.Count == 0) return WorkOutcome.Ok; // the tail was already consumed — stale claim

        var transcript = string.Join("\n", tail.Select(m =>
            $"{(m.Role == MessageRole.User ? "creator" : bot.Name)} ({m.CreatedAt.ToUniversalTime():ddd d MMM} {m.CreatedAt.Hour:00}:{m.CreatedAt.Minute:00}): {m.Content}"));

        // ── Provider (utility model when set, §3) ─────────────────────────────
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
            MaxTokens = _config.GetValue("Evolution:MaxTokens", 512),
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

        if (failed)
            return WorkOutcome.Fail(failure);

        _log.LogInformation("Evolution for bot {BotId}: {Result}", botId,
            version is null ? "no change (baseline kept)" : "new version written");
        return WorkOutcome.Ok;
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
