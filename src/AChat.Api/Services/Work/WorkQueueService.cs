using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// The durable work queue's worker (§12b). Each tick it (1) runs every producer,
/// which arms <c>WorkItems</c> for the passes that are due, then (2) claims and
/// executes due work items one at a time until none are left, recording each
/// outcome (delete on success; back-off or dead-letter on failure).
///
/// A container restart loses nothing: pending items and their attempt counts
/// live in the database, so the next tick after boot picks up exactly where the
/// previous process stopped (at-least-once — the handlers are idempotent).
/// </summary>
public sealed class WorkQueueService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<WorkQueueService> _log;
    private readonly TimeSpan _tick;
    private readonly int _maxAttempts;
    private readonly int _baseDelaySeconds;
    private readonly int _maxDelaySeconds;

    public WorkQueueService(IServiceScopeFactory scopes, IConfiguration config, ILogger<WorkQueueService> log)
    {
        _scopes = scopes;
        _config = config;
        _log = log;
        _tick = TimeSpan.FromSeconds(config.GetValue("Work:TickSeconds", 5));
        _maxAttempts = config.GetValue("Work:MaxAttempts", 5);
        _baseDelaySeconds = config.GetValue("Work:BaseDelaySeconds", 60);
        _maxDelaySeconds = config.GetValue("Work:MaxDelaySeconds", 3600);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "Work queue started (tick {Tick}s; max {Max} attempts; backoff {Base}s → {MaxDelay}s)",
            _tick.TotalSeconds, _maxAttempts, _baseDelaySeconds, _maxDelaySeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<AppDbContext>();
                var producers = sp.GetServices<IWorkProducer>().ToList();
                var handlers = sp.GetServices<IWorkHandler>().ToDictionary(h => h.Kind);

                // ── 1. Arm the queue: each producer checks its trigger and upserts ──
                foreach (var producer in producers)
                {
                    try
                    {
                        await producer.ProduceAsync(db, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "{Kind} producer failed — work will arm on the next tick", producer.Kind);
                    }
                }

                // ── 2. Execute due work until the queue is drained ────────────────
                while (true)
                {
                    var item = await WorkQueue.ClaimAsync(db, stoppingToken);
                    if (item is null) break;

                    if (!handlers.TryGetValue(item.Kind, out var handler))
                    {
                        // A kind with no registered handler is a configuration error —
                        // drop the row rather than dead-lettering it forever.
                        _log.LogError("No handler registered for work kind {Kind} — discarding item {Id}", item.Kind, item.Id);
                        db.WorkItems.Remove(item);
                        await db.SaveChangesAsync(stoppingToken);
                        continue;
                    }

                    WorkOutcome outcome;
                    try
                    {
                        outcome = await handler.ExecuteAsync(db, item, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // An unexpected throw (database error, etc.) is a failed attempt.
                        _log.LogWarning(ex, "Work item {Id} ({Kind}) threw", item.Id, item.Kind);
                        outcome = WorkOutcome.Fail(ex.Message);
                    }

                    await WorkQueue.RecordOutcomeAsync(
                        db, item, outcome, _baseDelaySeconds, _maxDelaySeconds, stoppingToken);

                    if (outcome.Success)
                        _log.LogInformation("Work item {Id} ({Kind}) completed", item.Id, item.Kind);
                    else
                        _log.LogWarning(
                            "Work item {Id} ({Kind}) attempt {Attempts}/{Max} failed: {Failure}",
                            item.Id, item.Kind, item.Attempts, item.MaxAttempts,
                            item.LastFailure is { Length: > 200 } f ? f[..200] + "…" : item.LastFailure);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Work queue tick failed");
            }

            try
            {
                await Task.Delay(_tick, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // shutting down
            }
        }
    }
}
