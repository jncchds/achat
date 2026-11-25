using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// Arms consolidation work (§2.4, §12b): a bot whose active trait list has grown past
/// <c>Turn:ConsolidateAbove</c> items AND whose newest persona version (any source) is
/// older than <c>Evolution:CooldownHours</c> gets a <see cref="WorkItem"/> on its
/// creator conversation (unique per bot, §2.3). No message watermarks. No LLM work here.
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
        var above = _config.GetValue("Turn:ConsolidateAbove", 12);
        var cooldownCutoff = DateTime.UtcNow.AddHours(-_config.GetValue("Evolution:CooldownHours", 12));

        var bots = await db.Bots
            .AsNoTracking()
            .Where(b => b.EvolutionEnabled)
            .Select(b => new { b.Id, b.OwnerId })
            .ToListAsync(ct);

        foreach (var bot in bots)
        {
            var activeCount = await db.BotTraits.CountAsync(t => t.BotId == bot.Id && t.RetiredAt == null, ct);
            if (activeCount <= above) continue; // cheap check first (legacy prose is one item at most)

            var convId = await db.Conversations
                .Where(c => c.BotId == bot.Id && c.UserId == bot.OwnerId)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);
            if (convId == 0) continue;

            // A preset that cannot generate is a configuration state, not a failure.
            var canGenerate = await db.Bots
                .Where(b => b.Id == bot.Id)
                .Select(b => b.Preset.GenerationModel != "" && b.Preset.GenerationModel != null)
                .FirstOrDefaultAsync(ct);
            if (!canGenerate) continue;

            var lastVersionAt = await db.PersonaVersions
                .Where(v => v.BotId == bot.Id)
                .MaxAsync(v => (DateTime?)v.CreatedAt, ct);
            if (!PersonaTraits.ShouldConsolidate(activeCount, above, lastVersionAt, cooldownCutoff)) continue;

            await WorkQueue.UpsertAsync(db, Kind, convId, MaxAttempts, ct);
        }
    }
}

/// <summary>
/// Trait consolidation (§2.4, executed by the durable queue per §12b). The bot's own
/// turns add and retire trait items (§4.7); this slow pass rewrites a long list into
/// fewer, merged, de-duplicated items. It never touches core or directives (passed as
/// constraints) and reads no conversation. Runs on the utility model, logged to §4.4
/// as <see cref="InteractionPurpose.PersonaEvolution"/>, stored as a
/// <see cref="PersonaVersionSource.Consolidation"/> version. The result must be a
/// non-empty JSON array of strings, otherwise the pass fails and retries (§12b).
/// </summary>
public sealed class EvolutionHandler : IWorkHandler
{
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
        if (string.IsNullOrWhiteSpace(bot.Preset.GenerationModel)) return WorkOutcome.Ok; // configuration state

        // Re-validate the trigger: a claim may be stale (already consolidated, or a
        // human edit re-armed the cooldown) — then a no-op succeeds.
        var active = await PersonaTraits.ActiveRowsAsync(db, bot.Id, ct);
        var lastVersionAt = await db.PersonaVersions
            .Where(v => v.BotId == bot.Id)
            .MaxAsync(v => (DateTime?)v.CreatedAt, ct);
        var cooldownCutoff = DateTime.UtcNow.AddHours(-_config.GetValue("Evolution:CooldownHours", 12));
        if (!PersonaTraits.ShouldConsolidate(active.Count, _config.GetValue("Turn:ConsolidateAbove", 12), lastVersionAt, cooldownCutoff))
            return WorkOutcome.Ok;

        return await ConsolidateAsync(db, bot, active, ct);
    }

    private async Task<WorkOutcome> ConsolidateAsync(AppDbContext db, Bot bot, List<BotTrait> active, CancellationToken ct)
    {
        var preset = bot.Preset!;
        var botId = bot.Id;
        var maxChars = _config.GetValue("Turn:MaxTraitChars", 200);

        var directives = await db.BotDirectives
            .Where(d => d.BotId == botId && d.IsActive)
            .OrderBy(d => d.Id)
            .Select(d => d.Text)
            .ToListAsync(ct);

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

        // Qwen3's chat template requires a user message in the list, so the full
        // brief goes in the user turn and the system message stays a short role.
        var system = "You maintain the list of traits a virtual friend has developed through its friendship. " +
                     "Output only a JSON array of strings — no commentary, no code fence.";

        var brief =
            "Consolidate the trait list below into FEWER items: merge overlapping ones, drop " +
            "duplicates and things that no longer hold, keep what is distinctive. Each item is one " +
            $"short phrase (at most {maxChars} characters) about the friendship itself: a habit, a " +
            "shared joke, a tone, how the friend has grown.\n" +
            "The core identity and the creator's directives are FIXED — do not restate, contradict " +
            "or override them.\n\n" +
            $"Core identity (fixed):\n{bot.CorePersona}\n\n" +
            $"Directives (fixed):\n{(directives.Count == 0 ? "(none)" : string.Join("\n", directives.Select(d => "- " + d)))}\n\n" +
            "Current traits:\n" + string.Join("\n", active.Select(t => "- " + t.Text)) +
            "\n\nReturn the consolidated list as a JSON array of strings.";

        var requestMessages = new List<LlmChatMessage>
        {
            new(MessageRole.System, system),
            new(MessageRole.User, brief),
        };

        var request = new LlmChatRequest
        {
            Messages = requestMessages,
            Model = model,
            Temperature = 0.3f,
            MaxTokens = _config.GetValue("Evolution:MaxTokens", 1024),
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
        }

        // ── Validate: non-empty JSON of strings, else the pass fails (§12b retry) ──
        List<string>? merged = null;
        if (!failed)
        {
            merged = PersonaTraits.ParseConsolidation(rawResponse, maxChars);
            if (merged is null)
            {
                failed = true;
                failure = "Consolidation result was not a non-empty JSON array of strings.";
            }
        }
        if (failed) _log.LogWarning("Consolidation for bot {BotId} failed: {Failure}", botId, Truncate(failure, 300));

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
            Notes = merged is null ? null : $"Consolidated {active.Count} → {merged.Count} traits",
            CreatedAt = now,
        };
        db.LlmInteractions.Add(interaction);
        await db.SaveChangesAsync(ct); // materialises interaction.Id

        if (failed || merged is null) return WorkOutcome.Fail(failure);

        // ── Replace the active set and store a Consolidation version (§2.4) ────
        foreach (var t in active) t.RetiredAt = now;
        foreach (var text in merged) db.BotTraits.Add(new BotTrait { BotId = botId, Text = text, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        var version = await PersonaTraits.RecordVersionAsync(db, botId, PersonaVersionSource.Consolidation, ct);
        version.InteractionId = interaction.Id; // the audit trail from §2.4
        await db.SaveChangesAsync(ct);

        _log.LogInformation("Consolidation for bot {BotId}: {From} → {To} traits", botId, active.Count, merged.Count);
        return WorkOutcome.Ok;
    }

    private static string Truncate(string? s, int max) =>
        s is null ? "" : (s.Length <= max ? s : s[..max] + "…");

    private static string SerializeRequest(IReadOnlyList<LlmChatMessage> messages) =>
        System.Text.Json.JsonSerializer.Serialize(messages
            .Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content })
            .ToList());
}
