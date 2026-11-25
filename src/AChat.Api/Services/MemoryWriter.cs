using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>A memory the bot asked to keep, as carried by the turn envelope (§4.7).</summary>
public sealed record MemoryCandidate(string Content, MemoryKind Kind, int Importance);

/// <summary>
/// Writes bot-recorded memories (§4.7, §5.3): embed, compare with the nearest existing
/// memory in scope, then drop a duplicate, supersede a near-match, or insert. A memory a
/// human edited is never overwritten (§5.6).
/// </summary>
public static class MemoryWriter
{
    public enum CandidateAction { Insert, Drop, Supersede }

    public sealed record Result(IReadOnlyList<int> Stored, IReadOnlyList<int> Superseded);

    /// <summary>Candidate disposition against the nearest existing memory (§5.3, §5.6). Pure.</summary>
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

    public static async Task<Result> StoreAsync(
        AppDbContext db, ILlmProvider provider, string embeddingModel,
        int botId, int userId, IReadOnlyList<MemoryCandidate> candidates,
        int? sourceMessageId, float dedupThreshold, float supersedeThreshold,
        CancellationToken ct)
    {
        var stored = new List<int>();
        var superseded = new List<int>();
        if (candidates.Count == 0) return new Result(stored, superseded);

        var vectors = await provider.EmbedAsync(embeddingModel,
            candidates.Select(c => c.Content).ToList(), ct);
        var now = DateTime.UtcNow;

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var vector = new Pgvector.Vector(vectors[i]);

            // Nearest existing memory in scope (current embedding model only, §5.7).
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
                vector, botId, userId, embeddingModel)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            var action = Decide(nearest?.Sim ?? 0f, nearest is null ? null : (MemorySource)nearest.Source,
                dedupThreshold, supersedeThreshold);
            if (action == CandidateAction.Drop) continue;

            var memory = new Memory
            {
                BotId = botId,
                UserId = userId,
                Kind = c.Kind,
                Content = c.Content,
                Embedding = vector,
                EmbeddingModel = embeddingModel,
                EmbeddingDimension = vector.Memory.Length,
                Importance = c.Importance,
                Source = MemorySource.BotRemember,
                SourceMessageId = sourceMessageId,
                CreatedAt = now,
            };
            db.Memories.Add(memory);
            await db.SaveChangesAsync(ct); // materialises memory.Id

            if (action == CandidateAction.Supersede && nearest is not null)
            {
                var existing = await db.Memories.FirstAsync(m => m.Id == nearest.Id, ct);
                existing.SupersededByMemoryId = memory.Id; // keep the old row inspectable (§5.6)
                await db.SaveChangesAsync(ct);
                superseded.Add(existing.Id);
            }
            stored.Add(memory.Id);
        }
        return new Result(stored, superseded);
    }
}

/// <summary>Nearest-memory projection row for the raw-SQL dedup query (Source is an int — EF's default enum mapping).</summary>
public sealed record NearestMemoryRow(int Id, int Source, float Sim);
