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
/// as <see cref="InteractionPurpose.PersonaEvolution"/> (every call, including a shrunk
/// retry), stored as a <see cref="PersonaVersionSource.Consolidation"/> version. The
/// result must be a non-empty JSON array of strings, otherwise the pass fails and
/// retries (§12b).
///
/// <para>
/// The prompt is bounded: <c>Evolution:MaxInputTokens</c> covers the fixed instructions,
/// core persona, directives and the traits; a list that does not fit is consolidated in
/// chunks, oldest first. A context-length rejection or a <c>finish_reason=length</c>
/// truncation is deterministic, so the chunk is split in two and retried once within the
/// attempt; if that fails too the work item is dead-lettered at once
/// (<see cref="WorkOutcome.FailPermanent"/>) with an actionable reason. Nothing is
/// written to the trait list until every chunk succeeded.
/// </para>
/// </summary>
public sealed class EvolutionHandler : IWorkHandler
{
    private const string SystemPrompt =
        // Qwen3's chat template requires a user message in the list, so the full
        // brief goes in the user turn and the system message stays a short role.
        "You maintain the list of traits a virtual friend has developed through its friendship. " +
        "Output only a JSON array of strings — no commentary, no code fence.";

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

    /// <summary>The outcome of one provider call: the merged items, or a failure (shrinkable = deterministic).</summary>
    private sealed record CallResult(List<string>? Merged, string? Failure, bool Shrinkable, int? InteractionId);

    private sealed class Run
    {
        public required AppDbContext Db { get; init; }
        public required Bot Bot { get; init; }
        public required ILlmProvider Provider { get; init; }
        public required string Model { get; init; }
        public required List<string> Directives { get; init; }
        public required int MaxChars { get; init; }
        public required int MaxOutputTokens { get; init; }
        public required int ClipChars { get; init; }
        public int? LastInteractionId { get; set; }
        public int Calls { get; set; }
    }

    private async Task<WorkOutcome> ConsolidateAsync(AppDbContext db, Bot bot, List<BotTrait> active, CancellationToken ct)
    {
        var preset = bot.Preset!;
        var botId = bot.Id;

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

        // ── Input budget ───────────────────────────────────────────────────────
        // The fixed part (instructions + core + directives) counts against the budget;
        // the traits get what is left, in chunks (oldest first) when they do not fit.
        var maxChars = _config.GetValue("Turn:MaxTraitChars", 200);
        var maxInput = _config.GetValue("Evolution:MaxInputTokens", 4000);
        var fixedTokens = PromptAssembly.EstimateTokens(SystemPrompt)
            + PromptAssembly.EstimateTokens(BuildBrief(bot.CorePersona, directives, [], maxChars));
        var available = maxInput - fixedTokens;
        if (available < 64)
        {
            var why = $"Consolidation skipped: the fixed prompt (instructions, core persona, directives) takes about " +
                      $"{fixedTokens} tokens of the {maxInput}-token input budget — raise Evolution:MaxInputTokens " +
                      "or shorten the core persona / directives.";
            await LogFailureAsync(db, bot, model, why, ct);
            return WorkOutcome.FailPermanent(why);
        }

        var run = new Run
        {
            Db = db, Bot = bot, Provider = provider, Model = model, Directives = directives,
            MaxChars = maxChars,
            MaxOutputTokens = _config.GetValue("Evolution:MaxTokens", 4096),
            ClipChars = Math.Max(maxChars, available * 3), // one legacy prose item can never exceed the budget on its own
        };

        var chunks = TokenLimits.Chunk(active, t => PromptAssembly.EstimateTokens(Clipped(t.Text, run.ClipChars)), available);

        // ── Run the chunks; all must succeed before anything is written ────────
        var merged = new List<string>();
        foreach (var chunk in chunks)
        {
            var res = await RunChunkAsync(run, chunk, shrinkAllowed: true, ct);
            if (res.Merged is null)
                return res.Shrinkable ? WorkOutcome.FailPermanent(res.Failure) : WorkOutcome.Fail(res.Failure);
            foreach (var m in res.Merged)
                if (!merged.Contains(m, StringComparer.OrdinalIgnoreCase)) merged.Add(m);
        }
        if (merged.Count == 0) return WorkOutcome.Fail("Consolidation produced no items.");

        // ── Replace the active set and store a Consolidation version (§2.4) ────
        var now = DateTime.UtcNow;
        foreach (var t in active) t.RetiredAt = now;
        foreach (var text in merged) db.BotTraits.Add(new BotTrait { BotId = botId, Text = text, CreatedAt = now });
        await db.SaveChangesAsync(ct);
        var version = await PersonaTraits.RecordVersionAsync(db, botId, PersonaVersionSource.Consolidation, ct);
        version.InteractionId = run.LastInteractionId; // the audit trail from §2.4
        await db.SaveChangesAsync(ct);

        _log.LogInformation("Consolidation for bot {BotId}: {From} → {To} traits in {Calls} call(s)",
            botId, active.Count, merged.Count, run.Calls);
        return WorkOutcome.Ok;
    }

    /// <summary>
    /// One chunk. A deterministic failure (context rejection / truncation) splits a multi-item
    /// chunk in two and retries each half once (no further splitting); anything else returns as is.
    /// </summary>
    private async Task<CallResult> RunChunkAsync(Run run, List<BotTrait> chunk, bool shrinkAllowed, CancellationToken ct)
    {
        var res = await CallAsync(run, chunk, ct);
        if (res.Merged is not null || !res.Shrinkable) return res;

        if (!shrinkAllowed || chunk.Count < 2)
            return res with { Failure = res.Failure + (chunk.Count < 2 ? " (a single item cannot be shrunk further)" : " (already retried on a smaller input)") };

        _log.LogWarning("Consolidation for bot {BotId}: {Failure} — retrying {Count} traits as two halves",
            run.Bot.Id, Truncate(res.Failure, 200), chunk.Count);
        var half = chunk.Count / 2;
        var merged = new List<string>();
        foreach (var part in new[] { chunk.Take(half).ToList(), chunk.Skip(half).ToList() })
        {
            var r = await RunChunkAsync(run, part, shrinkAllowed: false, ct);
            if (r.Merged is null) return r;
            merged.AddRange(r.Merged);
        }
        return new CallResult(merged, null, false, run.LastInteractionId);
    }

    private async Task<CallResult> CallAsync(Run run, List<BotTrait> chunk, CancellationToken ct)
    {
        var bot = run.Bot;
        var preset = bot.Preset!;
        var brief = BuildBrief(bot.CorePersona, run.Directives, chunk.Select(t => Clipped(t.Text, run.ClipChars)).ToList(), run.MaxChars);
        var requestMessages = new List<LlmChatMessage>
        {
            new(MessageRole.System, SystemPrompt),
            new(MessageRole.User, brief),
        };
        var request = new LlmChatRequest
        {
            Messages = requestMessages,
            Model = run.Model,
            Temperature = 0.3f,
            MaxTokens = run.MaxOutputTokens,
        };

        string? rawResponse = null;
        string? failure = null;
        bool failed = false, shrinkable = false;
        int? httpStatus = null;
        List<string>? merged = null;
        LlmCompletion? completion = null;
        run.Calls++;

        try
        {
            completion = await run.Provider.ChatAsync(request, ct);
            rawResponse = completion.Content?.Trim() ?? "";
            if (completion.FinishReason == "length")
            {
                // Cut off at the output cap (a reasoning model spends it thinking). The content, if
                // any, is incomplete — never parse it.
                failed = shrinkable = true;
                failure = $"Consolidation truncated at the output cap (finish_reason=length, Evolution:MaxTokens={run.MaxOutputTokens}) — " +
                          "raise Evolution:MaxTokens or use a non-reasoning utility model.";
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
                failure += " — the prompt exceeds the model's context window; lower Evolution:MaxInputTokens.";
        }
        catch (Exception ex)
        {
            failed = true;
            failure = ex.Message;
        }

        if (!failed)
        {
            // Strip inline reasoning before parsing (§4.4 keeps the raw response).
            merged = PersonaTraits.ParseConsolidation(TokenLimits.StripThinking(rawResponse), run.MaxChars);
            if (merged is null)
            {
                failed = true;
                failure = "Consolidation result was not a non-empty JSON array of strings.";
            }
        }
        if (failed) _log.LogWarning("Consolidation for bot {BotId} failed: {Failure}", bot.Id, Truncate(failure, 300));

        // ── Log the call, success or failure (§4.4) ────────────────────────────
        var interaction = new LlmInteraction
        {
            Purpose = InteractionPurpose.PersonaEvolution,
            BotId = bot.Id,
            UserId = bot.OwnerId,
            PresetId = preset.Id,
            Provider = preset.Provider,
            Model = run.Model,
            Endpoint = string.IsNullOrWhiteSpace(preset.BaseEndpoint) ? "(default)" : preset.BaseEndpoint,
            RequestBody = SerializeRequest(requestMessages),
            ResponseContent = rawResponse ?? failure,
            PromptTokens = completion?.PromptTokens,
            CompletionTokens = completion?.CompletionTokens,
            TotalTokens = completion?.TotalTokens,
            FinishReason = completion?.FinishReason,
            Failed = failed,
            HttpStatus = httpStatus,
            FailureReason = failure,
            Notes = merged is null ? null : $"Consolidated {chunk.Count} → {merged.Count} traits",
            CreatedAt = DateTime.UtcNow,
        };
        run.Db.LlmInteractions.Add(interaction);
        await run.Db.SaveChangesAsync(ct); // materialises interaction.Id
        run.LastInteractionId = interaction.Id;

        return failed ? new CallResult(null, failure, shrinkable, interaction.Id) : new CallResult(merged, null, false, interaction.Id);
    }

    /// <summary>A pass that never reached the provider still leaves a §4.4 row so the reason is visible.</summary>
    private static async Task LogFailureAsync(AppDbContext db, Bot bot, string model, string reason, CancellationToken ct)
    {
        var preset = bot.Preset!;
        db.LlmInteractions.Add(new LlmInteraction
        {
            Purpose = InteractionPurpose.PersonaEvolution,
            BotId = bot.Id, UserId = bot.OwnerId, PresetId = preset.Id, Provider = preset.Provider, Model = model,
            Endpoint = string.IsNullOrWhiteSpace(preset.BaseEndpoint) ? "(default)" : preset.BaseEndpoint,
            RequestBody = "[]", ResponseContent = reason, Failed = true, FailureReason = reason,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The user-turn brief for a list of trait texts (also used to size the fixed part with an empty list).</summary>
    internal static string BuildBrief(string corePersona, IReadOnlyList<string> directives, IReadOnlyList<string> traits, int maxChars) =>
        "Consolidate the trait list below into FEWER items: merge overlapping ones, drop " +
        "duplicates and things that no longer hold, keep what is distinctive. Each item is one " +
        $"short phrase (at most {maxChars} characters) about the friendship itself: a habit, a " +
        "shared joke, a tone, how the friend has grown.\n" +
        "The core identity and the creator's directives are FIXED — do not restate, contradict " +
        "or override them.\n\n" +
        $"Core identity (fixed):\n{corePersona}\n\n" +
        $"Directives (fixed):\n{(directives.Count == 0 ? "(none)" : string.Join("\n", directives.Select(d => "- " + d)))}\n\n" +
        "Current traits:\n" + string.Join("\n", traits.Select(t => "- " + t)) +
        "\n\nReturn the consolidated list as a JSON array of strings.";

    private static string Clipped(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Truncate(string? s, int max) =>
        s is null ? "" : (s.Length <= max ? s : s[..max] + "…");

    private static string SerializeRequest(IReadOnlyList<LlmChatMessage> messages) =>
        System.Text.Json.JsonSerializer.Serialize(messages
            .Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content })
            .ToList());
}
