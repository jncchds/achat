using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// The durable work queue's core mechanics (§12b) — claim, back-off, dead-letter,
/// and the producer's upsert. Static and database-scoped so the semantics are
/// unit-testable against an in-memory database; the hosted
/// <see cref="WorkQueueService"/> drives it.
///
/// <para>
/// Claiming is optimistic (read-then-mark), which is correct for the
/// one-app-container deployment — the same assumption §6 makes for migration
/// application. A multi-replica deployment would need
/// <c>FOR UPDATE SKIP LOCKED</c>; the worst case today is duplicate work, not
/// corruption, because the handlers are idempotent.
/// </para>
/// </summary>
public static class WorkQueue
{
    /// <summary>
    /// Claim the oldest due <see cref="WorkStatus.Pending"/> item: mark it
    /// <see cref="WorkStatus.Running"/> with <c>Attempts += 1</c>, or return null
    /// when nothing is due.
    /// </summary>
    public static async Task<WorkItem?> ClaimAsync(AppDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var item = await db.WorkItems
            .Where(w => w.Status == WorkStatus.Pending && w.NextTryAt <= now)
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (item is null) return null;

        item.Status = WorkStatus.Running;
        item.Attempts += 1;
        item.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>
    /// Record the outcome of a claimed item. Success deletes the row (the handler
    /// has already committed its result). Failure stores <see cref="WorkItem.LastFailure"/>
    /// and either dead-letters (attempts exhausted) or backs the item off:
    /// <c>NextTryAt = now + min(MaxDelay, BaseDelay · 2^attempts) ± 10% jitter</c>.
    /// </summary>
    public static async Task RecordOutcomeAsync(
        AppDbContext db, WorkItem item, WorkOutcome outcome,
        int baseDelaySeconds = 60, int maxDelaySeconds = 3600,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (outcome.Success)
        {
            db.WorkItems.Remove(item);
        }
        else
        {
            item.LastFailure = Truncate(outcome.Failure ?? "unknown failure", 2000);
            if (item.Attempts >= item.MaxAttempts)
            {
                item.Status = WorkStatus.Dead;
            }
            else
            {
                item.Status = WorkStatus.Pending;
                var delay = DelaySeconds(item.Attempts, baseDelaySeconds, maxDelaySeconds);
                // ±10% jitter so a fleet of items doesn't retry in lockstep.
                var jitter = delay * 0.1 * (2.0 * Random.Shared.NextDouble() - 1.0);
                item.NextTryAt = now.AddSeconds(delay + jitter);
            }
        }
        item.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The producer's upsert (§12b): arm a <see cref="WorkStatus.Pending"/> item for
    /// (kind, conversation) unless one already exists — in <b>any</b> status, so a
    /// <see cref="WorkStatus.Dead"/> item stays frozen until an admin retries or
    /// discards it.
    /// </summary>
    public static async Task UpsertAsync(
        AppDbContext db, WorkKind kind, int conversationId, int maxAttempts,
        CancellationToken ct = default)
    {
        var exists = await db.WorkItems
            .AnyAsync(w => w.Kind == kind && w.ConversationId == conversationId, ct);
        if (exists) return;

        var now = DateTime.UtcNow;
        db.WorkItems.Add(new WorkItem
        {
            Kind = kind,
            ConversationId = conversationId,
            Status = WorkStatus.Pending,
            MaxAttempts = maxAttempts,
            NextTryAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Deterministic backoff for a failed attempt (attempt 1 → base, doubling per
    /// attempt, capped). The jitter is applied in <see cref="RecordOutcomeAsync"/>.
    /// </summary>
    public static double DelaySeconds(int attempts, int baseDelaySeconds = 60, int maxDelaySeconds = 3600)
    {
        if (attempts < 1) attempts = 1;
        return Math.Min(maxDelaySeconds, baseDelaySeconds * Math.Pow(2, attempts - 1));
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
