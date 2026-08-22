using System.Collections.Concurrent;
using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace AChat.Api.Services;

/// <summary>
/// Memory-extraction worker (§5.3). A background service — not in the request path,
/// a turn never waits on it. It reads the conversation's un-extracted tail (messages
/// after <see cref="Conversation.ExtractionUpToMessageId"/>) once either enough
/// messages have accumulated (<c>Extraction:MinNewMessages</c>) or the conversation
/// has gone quiet (<c>Extraction:IdleMinutes</c>), so a fast back-and-forth produces
/// one extraction pass rather than twenty.
///
/// Method: one LLM call (utility model when set) over the new messages, prompted for
/// structured JSON — durable facts and meaningful events, not pleasantries. Each
/// accepted candidate is embedded, deduplicated against the scope (drop ≥
/// <c>Memory:DedupThreshold</c>; supersede ≥ <c>Memory:SupersedeThreshold</c> unless
/// a human edited the existing row, §5.6), and stored. The watermark advances only
/// after a successful pass; a failed pass leaves it untouched so the tail retries.
/// Every call is logged to the interaction log (§4.4, purpose <see cref="InteractionPurpose.MemoryExtraction"/>).
/// </summary>
public sealed class MemoryExtractionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<MemoryExtractionService> _log;
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    public int MinNewMessages { get; }
    public int IdleMinutes { get; }
    public int MaxCandidates { get; }
    public int MaxTailMessages { get; }
    public float DedupThreshold { get; }
    public float SupersedeThreshold { get; }

    public MemoryExtractionService(IServiceScopeFactory scopes, IConfiguration config, ILogger<MemoryExtractionService> log)
    {
        _scopes = scopes;
        _config = config;
        _log = log;
        MinNewMessages = config.GetValue("Extraction:MinNewMessages", 8);
        IdleMinutes = config.GetValue("Extraction:IdleMinutes", 10);
        MaxCandidates = config.GetValue("Extraction:MaxCandidates", 10);
        MaxTailMessages = config.GetValue("Extraction:MaxTailMessages", 40);
        DedupThreshold = config.GetValue("Memory:DedupThreshold", 0.92f);
        SupersedeThreshold = config.GetValue("Memory:SupersedeThreshold", 0.85f);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "Memory extraction started (sweep every {Interval}s; trigger ≥ {M} new messages or idle ≥ {T} min)",
            SweepInterval.TotalSeconds, MinNewMessages, IdleMinutes);
        try
        {
            using var timer = new PeriodicTimer(SweepInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await SweepAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Extraction sweep failed");
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
        var idleCutoff = now.AddMinutes(-IdleMinutes);

        // Conversations with an un-extracted tail.
        var conversations = await db.Conversations
            .AsNoTracking()
            .Where(c => c.LastMessageAt != null)
            .Select(c => new { c.Id, c.BotId, c.UserId, c.LastMessageAt, c.ExtractionUpToMessageId })
            .ToListAsync(ct);

        var candidates = new List<(int ConversationId, int BotId, int UserId)>();
        foreach (var c in conversations)
        {
            if (c.LastMessageAt is not { } last) continue;
            var newCount = await db.Messages
                .CountAsync(m => m.ConversationId == c.Id && m.Id > c.ExtractionUpToMessageId, ct);
            if (newCount < 2) continue;

            var triggered = newCount >= MinNewMessages
                || (last <= idleCutoff && newCount >= 2);
            if (!triggered) continue;
            candidates.Add((c.Id, c.BotId, c.UserId));
        }

        foreach (var (conversationId, botId, _) in candidates)
        {
            if (!_inFlight.TryAdd(conversationId, 0)) continue;
            try
            {
                await ExtractAsync(db, keys, factory, conversationId, botId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Extraction failed for conversation {ConversationId}", conversationId);
            }
            finally
            {
                _inFlight.TryRemove(conversationId, out _);
            }
        }
    }

    private async Task ExtractAsync(
        AppDbContext db, IApiKeyProtector keys, ILlmProviderFactory factory,
        int conversationId, int botId, CancellationToken ct)
    {
        var conversation = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null) return;

        var bot = await db.Bots.Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot?.Preset is null) return;
        var preset = bot.Preset;
        if (string.IsNullOrWhiteSpace(preset.EmbeddingModel))
            return; // no embedding model on the preset — nothing to store against (§5.4)
        if (string.IsNullOrWhiteSpace(preset.GenerationModel)) return;

        var watermark = conversation.ExtractionUpToMessageId;
        var tail = await db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.Id > watermark)
            .OrderBy(m => m.Id)
            .Take(MaxTailMessages)
            .ToListAsync(ct);
        if (tail.Count < 2) return;

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

        var firstAt = tail[0].CreatedAt;
        var transcript = string.Join("\n", tail.Select(m =>
            $"{(m.Role == MessageRole.User ? "user" : "friend")} ({m.CreatedAt.ToUniversalTime():ddd d MMM} {m.CreatedAt.Hour:00}:{m.CreatedAt.Minute:00}): {m.Content}"));

        var system = "You extract durable memories from a conversation between a virtual friend named " +
            $"{bot.Name} and their user. Record durable facts and meaningful events about the USER — " +
            "who they are, people and pets in their life, what they like, what happened, what they plan — " +
            "each as a standalone, self-contained sentence (no pronouns that need context). " +
            "Skip pleasantries, small talk, and things only the friend said. " +
            "Respond with a JSON array and nothing else; each element: " +
            "{\"content\": string, \"kind\": \"fact\"|\"preference\"|\"event\"|\"relationship\", \"importance\": 1-5}. " +
            "Return an empty array if nothing durable.";

        var requestMessages = new List<LlmChatMessage>
        {
            new(MessageRole.System, system),
            new(MessageRole.User, "New messages (oldest first):\n" + transcript + "\n\nReturn the JSON array."),
        };

        var request = new LlmChatRequest
        {
            Messages = requestMessages,
            Model = model,
            Temperature = 0.3f,
            MaxTokens = _config.GetValue("Extraction:MaxTokens", 1024),
        };

        var now = DateTime.UtcNow;
        string? rawResponse = null;
        List<(string Content, MemoryKind Kind, int Importance)> candidates = [];
        string? failure = null;
        bool failed = false;
        LlmCompletion? completion = null;

        try
        {
            completion = await provider.ChatAsync(request, ct);
            rawResponse = completion.Content ?? "";
            candidates = ParseCandidates(rawResponse, MaxCandidates);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            failed = true;
            failure = ex is LlmProviderException lpe
                ? (lpe.HttpStatus is { } s ? $"Provider returned HTTP {s}: {Truncate(lpe.ErrorPayload, 2000)}" : lpe.Message)
                : ex.Message;
        }

        var storedIds = new List<int>();
        var supersededIds = new List<int>();

        if (!failed && candidates.Count > 0)
        {
            // ── Embed all candidates in one call (§5.3) ─────────────────────────
            float[][] vectors;
            try
            {
                var embeddings = await provider.EmbedAsync(preset.EmbeddingModel,
                    candidates.Select(c => c.Content).ToList(), ct);
                vectors = embeddings.Select(e => e).ToArray();
            }
            catch (Exception ex)
            {
                failed = true;
                failure = "Embedding failed: " + ex.Message;
                vectors = null;
            }

            if (vectors is not null)
            {
                for (var i = 0; i < candidates.Count; i++)
                {
                    var (content, kind, importance) = candidates[i];
                    var vector = new Vector(vectors[i]);

                    // Nearest existing memory in scope (current model only, §5.7).
                    var nearest = await db.Database.SqlQueryRaw<NearestMemoryRow>(
                        """
                        SELECT m."Id", m."Source",
                               CAST(1 - (m."Embedding" <=> {0}::vector) AS real) AS "Sim"
                        FROM "Memories" m
                        WHERE m."BotId" = {1}
                          AND m."UserId" = {2}
                          AND m."EmbeddingModel" = {3}
                          AND m."IsDeleted" = FALSE
                          AND m."SupersededByMemoryId" IS NULL
                        ORDER BY m."Embedding" <=> {0}::vector
                        LIMIT 1
                        """,
                        vector, conversation.BotId, conversation.UserId, preset.EmbeddingModel)
                        .AsNoTracking()
                        .FirstOrDefaultAsync(ct);

                    var action = Decide(nearest?.Sim ?? 0f, nearest is null ? null : (MemorySource)nearest.Source);
                    if (action == CandidateAction.Drop) continue;

                    var memory = new Memory
                    {
                        BotId = conversation.BotId,
                        UserId = conversation.UserId,
                        Kind = kind,
                        Content = content,
                        Embedding = vector,
                        EmbeddingModel = preset.EmbeddingModel,
                        EmbeddingDimension = vector.Memory.Length,
                        Importance = importance,
                        Source = MemorySource.Extraction,
                        SourceMessageId = tail.Max(m => m.Id),
                        CreatedAt = now,
                    };
                    db.Memories.Add(memory);
                    await db.SaveChangesAsync(ct); // materialises memory.Id

                    if (action == CandidateAction.Supersede && nearest is not null)
                    {
                        var existing = await db.Memories.FirstAsync(m => m.Id == nearest!.Id, ct);
                        existing.SupersededByMemoryId = memory.Id; // keep the old row inspectable (§5.6)
                        await db.SaveChangesAsync(ct);
                        supersededIds.Add(existing.Id);
                    }
                    storedIds.Add(memory.Id);
                }
            }
        }

        // ── Advance the watermark only on success (§5.3) ───────────────────────
        if (!failed)
        {
            var conv = await db.Conversations.FirstAsync(c => c.Id == conversationId, ct);
            conv.ExtractionUpToMessageId = tail.Max(m => m.Id);
            if (storedIds.Count > 0 || supersededIds.Count > 0)
                await db.SaveChangesAsync(ct);
        }

        // ── Log the call, success or failure (§4.4) ────────────────────────────
        db.LlmInteractions.Add(new LlmInteraction
        {
            Purpose = InteractionPurpose.MemoryExtraction,
            BotId = bot.Id,
            UserId = conversation.UserId,
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
        });
        await db.SaveChangesAsync(ct);

        if (!failed)
            _log.LogInformation("Extraction for conversation {ConversationId}: {Stored} stored, {Superseded} superseded (from {Count} candidates)",
                conversationId, storedIds.Count, supersededIds.Count, candidates.Count);
    }

    /// <summary>
    /// Candidate disposition against the nearest existing memory (§5.3, §5.6). Pure.
    /// </summary>
    public enum CandidateAction { Insert, Drop, Supersede }

    public static CandidateAction Decide(float similarity, MemorySource? nearestSource,
        float dedupThreshold = 0.92f, float supersedeThreshold = 0.85f)
    {
        if (similarity >= dedupThreshold) return CandidateAction.Drop; // a duplicate — never store twice
        if (similarity >= supersedeThreshold)
        {
            // A human correction is never silently overwritten (§5.6).
            return nearestSource == MemorySource.UserEdited
                ? CandidateAction.Drop
                : CandidateAction.Supersede;
        }
        return CandidateAction.Insert;
    }

    /// <summary>
    /// Lenient parse of the extraction response: tolerates code fences, prose around
    /// the array, and skips malformed elements. Returns at most <paramref name="max"/>.
    /// </summary>
    public static List<(string Content, MemoryKind Kind, int Importance)> ParseCandidates(string raw, int max)
    {
        var result = new List<(string, MemoryKind, int)>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start) return result;

        var array = raw[start..(end + 1)];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(array);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!el.TryGetProperty("content", out var c) || c.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var content = c.GetString()?.Trim();
                if (string.IsNullOrEmpty(content)) continue;

                var kind = MemoryKind.Fact;
                if (el.TryGetProperty("kind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String)
                    Enum.TryParse(k.GetString(), ignoreCase: true, out kind);

                var importance = 3;
                if (el.TryGetProperty("importance", out var imp) &&
                    imp.TryGetInt32(out var impValue))
                    importance = Math.Clamp(impValue, 1, 5);

                result.Add((content, kind, importance));
                if (result.Count >= max) break;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Malformed JSON — treat as no candidates; the caller logs the raw response.
        }
        return result;
    }

    private static string SerializeRequest(IReadOnlyList<LlmChatMessage> messages) =>
        System.Text.Json.JsonSerializer.Serialize(messages
            .Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content })
            .ToList());

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}

/// <summary>Nearest-memory projection row for the raw-SQL dedup query (Source is an int — EF's default enum mapping).</summary>
public sealed record NearestMemoryRow(int Id, int Source, float Sim);


