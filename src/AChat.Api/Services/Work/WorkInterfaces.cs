using AChat.Core.Models;
using AChat.Infrastructure.Data;

namespace AChat.Api.Services;

/// <summary>
/// The result of one work-item execution (§12b). A failed outcome carries the
/// shaped failure text (provider HTTP status, parse error) for
/// <see cref="WorkItem.LastFailure"/>.
/// </summary>
public sealed record WorkOutcome(bool Success, string? Failure = null)
{
    public static WorkOutcome Ok { get; } = new(true);
    public static WorkOutcome Fail(string? failure) => new(false, failure);
}

/// <summary>
/// A producer re-evaluates its kind's trigger on each tick and upserts a
/// <see cref="WorkItem"/> when work is due (§12b). Producers never execute LLM
/// work — they only arm the queue.
/// </summary>
public interface IWorkProducer
{
    WorkKind Kind { get; }
    Task ProduceAsync(AppDbContext db, CancellationToken ct);
}

/// <summary>
/// A handler executes one claimed work item: read the state, do the LLM pass,
/// commit the result (watermark / version / summary) and return an outcome.
/// Handlers are idempotent — a double execution after a crash is harmless
/// (§12b, at-least-once).
/// </summary>
public interface IWorkHandler
{
    WorkKind Kind { get; }
    Task<WorkOutcome> ExecuteAsync(AppDbContext db, WorkItem item, CancellationToken ct);
}
