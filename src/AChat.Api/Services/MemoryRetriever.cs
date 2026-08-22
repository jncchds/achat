using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace AChat.Api.Services;

/// <summary>A memory with its retrieval signals (§5.4).</summary>
public sealed record ScoredMemory
{
    public required Memory Memory { get; init; }
    public required float Similarity { get; init; }
    public float Score { get; init; }
    public bool Pinned { get; init; }
}

/// <summary>
/// Memory retrieval (§5.4). On each turn:
/// 1. embed the incoming user message with the preset's embedding model;
/// 2. cosine-search <see cref="Memory"/> rows scoped to (BotId, UserId) and to rows whose
///    EmbeddingModel matches the preset's current one (§5.7);
/// 3. rank by similarity + importance + recency (weights configurable, §5.10) and take
///    the top K above a score floor;
/// 4. always include pinned memories regardless of score.
/// </summary>
public sealed class MemoryRetriever
{
    public int TopK { get; }
    public float ScoreFloor { get; }
    public float WeightSimilarity { get; }
    public float WeightImportance { get; }
    public float WeightRecency { get; }
    public float RecencyHalflifeDays { get; }

    public MemoryRetriever(int topK, float scoreFloor, float weightSimilarity,
        float weightImportance, float weightRecency, float recencyHalflifeDays)
    {
        TopK = topK;
        ScoreFloor = scoreFloor;
        WeightSimilarity = weightSimilarity;
        WeightImportance = weightImportance;
        WeightRecency = weightRecency;
        RecencyHalflifeDays = recencyHalflifeDays;
    }

    public static MemoryRetriever FromConfiguration(IConfiguration config) => new(
        config.GetValue("Memory:TopK", 5),
        config.GetValue("Memory:ScoreFloor", 0.35f),
        config.GetValue("Memory:WeightSimilarity", 0.6f),
        config.GetValue("Memory:WeightImportance", 0.2f),
        config.GetValue("Memory:WeightRecency", 0.2f),
        config.GetValue("Memory:RecencyHalflifeDays", 30f));

    /// <summary>
    /// The ranking function (§5.4): score = w_sim·similarity + w_imp·(importance/5)
    /// + w_rec·exp(−age_days/halflife). Pure — unit-testable.
    /// </summary>
    public static float Rank(float similarity, int importance, double ageDays,
        float wSim, float wImp, float wRec, float halflifeDays)
    {
        if (halflifeDays <= 0) halflifeDays = 30f;
        var recency = MathF.Exp(-(float)(ageDays / halflifeDays));
        return wSim * similarity + wImp * (importance / 5f) + wRec * recency;
    }

    /// <summary>Cosine search over in-scope, active memories of the current model (§5.4.2, §5.7).</summary>
    public async Task<List<(Memory Memory, float Similarity)>> SearchAsync(
        AppDbContext db, string embeddingModel, int botId, int userId,
        float[] queryVector, int limit, CancellationToken ct)
    {
        if (queryVector.Length == 0) return [];

        var v = new Vector(queryVector);
        var rows = await db.Database.SqlQueryRaw<MemorySearchRow>(
            """
            SELECT m."Id", m."Kind", m."Content", m."Importance", m."IsPinned", m."Source",
                   m."SourceMessageId", m."CreatedAt", m."LastRecalledAt", m."RecallCount",
                   CAST(1 - (m."Embedding" <=> {0}::vector) AS real) AS "Similarity"
            FROM "Memories" m
            WHERE m."BotId" = {1}
              AND m."UserId" = {2}
              AND m."EmbeddingModel" = {3}
              AND m."IsDeleted" = FALSE
              AND m."SupersededByMemoryId" IS NULL
            ORDER BY m."Embedding" <=> {0}::vector
            LIMIT {4}
            """,
            v, botId, userId, embeddingModel, limit)
            .ToListAsync(ct);

        return rows.Select(r => (Map(r), r.Similarity)).ToList();
    }

    /// <summary>
    /// Selection (§5.4.3–4): rank, keep top-K above the floor, and always include
    /// pinned memories. Pure over its input — unit-testable.
    /// </summary>
    public static List<ScoredMemory> Select(
        IEnumerable<(Memory Memory, float Similarity)> candidates,
        DateTime now, MemoryRetriever cfg)
    {
        var scored = candidates
            .Select(c =>
            {
                var ageDays = Math.Max(0, (now - c.Memory.CreatedAt).TotalDays);
                return new ScoredMemory
                {
                    Memory = c.Memory,
                    Similarity = c.Similarity,
                    Pinned = c.Memory.IsPinned,
                    Score = Rank(c.Similarity, c.Memory.Importance, ageDays,
                        cfg.WeightSimilarity, cfg.WeightImportance, cfg.WeightRecency,
                        cfg.RecencyHalflifeDays),
                };
            })
            .ToList();

        // Pinned memories ride along on top of the K slots (§5.4.4).
        var pinned = scored.Where(s => s.Pinned).ToList();
        var rest = scored.Where(s => !s.Pinned && s.Score >= cfg.ScoreFloor)
            .OrderByDescending(s => s.Score)
            .Take(cfg.TopK)
            .ToList();

        var result = new List<ScoredMemory>(pinned.Count + rest.Count);
        var seen = new HashSet<int>();
        foreach (var s in pinned.Where(s => seen.Add(s.Memory.Id))) result.Add(s);
        foreach (var s in rest.Where(s => seen.Add(s.Memory.Id))) result.Add(s);
        return result;
    }

    /// <summary>Render recalled memories into the prompt block, with relative ages (§5.5.3, §5.9.3).</summary>
    public static string BuildMemoryBlock(List<ScoredMemory> memories, DateTime now)
    {
        var lines = memories
            .OrderByDescending(m => m.Pinned)
            .ThenByDescending(m => m.Score)
            .Select(m => $"- ({RelativeAge(m.Memory.CreatedAt, now)}) {m.Memory.Content}");
        return "Things you remember about this person, with how long ago they came up:\n"
            + string.Join("\n", lines);
    }

    /// <summary>
    /// Relative age as a presentation concern (§5.9.3): the same memory renders as
    /// "yesterday" or "last year" depending on when it is recalled.
    /// </summary>
    public static string RelativeAge(DateTime thenUtc, DateTime nowUtc)
    {
        var age = nowUtc - thenUtc;
        if (age.TotalDays >= 365)
        {
            var years = (int)(age.TotalDays / 365);
            return years == 1 ? "about a year ago" : $"about {years} years ago";
        }
        if (age.TotalDays >= 30)
        {
            var months = (int)(age.TotalDays / 30);
            return months == 1 ? "about a month ago" : $"about {months} months ago";
        }
        if (age.TotalDays >= 2) return $"{(int)age.TotalDays} days ago";
        if (age.TotalDays >= 1) return "yesterday";
        if (age.TotalHours >= 1) return $"{(int)age.TotalHours} hours ago";
        return "earlier today";
    }

    private static Memory Map(MemorySearchRow r) => new()
    {
        Id = r.Id,
        Kind = (MemoryKind)r.Kind,
        Content = r.Content,
        Importance = r.Importance,
        IsPinned = r.IsPinned,
        Source = (MemorySource)r.Source,
        SourceMessageId = r.SourceMessageId,
        CreatedAt = r.CreatedAt,
        LastRecalledAt = r.LastRecalledAt,
        RecallCount = r.RecallCount,
    };
}

/// <summary>
/// Projection row for the raw-SQL vector search (abook's pattern). Kind/Source are
/// plain integers — EF's default enum mapping — so they must be read as int here.
/// </summary>
public sealed record MemorySearchRow(
    int Id,
    int Kind,
    string Content,
    int Importance,
    bool IsPinned,
    int Source,
    int? SourceMessageId,
    DateTime CreatedAt,
    DateTime? LastRecalledAt,
    int RecallCount,
    float Similarity);
