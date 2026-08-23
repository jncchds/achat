using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// Interaction-log retention (§4.4, Phase 8). The audit log is the table that
/// dominates database size — it stores full request/response text. This worker
/// prunes interactions older than the configured retention window, in batches, so
/// the log stays bounded without a manual database intervention (which the project
/// rules forbid — data changes go through the application).
///
/// <c>Retention:InteractionDays</c> (default 90; 0 = keep forever) ·
/// <c>Retention:IntervalHours</c> (default 6).
/// </summary>
public sealed class InteractionRetentionService(
    IDbContextFactory<AppDbContext> dbFactory,
    IConfiguration config,
    ILogger<InteractionRetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retentionDays = config.GetValue("Retention:InteractionDays", 90);
        if (retentionDays <= 0)
        {
            log.LogInformation("Interaction retention disabled (Retention:InteractionDays = 0) — keeping the full log.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, config.GetValue("Retention:IntervalHours", 6)));

        try
        {
            // Let the app finish starting (migrations, bootstrap) before the first prune.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await PruneAsync(retentionDays, stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task PruneAsync(int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        using var db = dbFactory.CreateDbContext();

        const int batchSize = 5000;
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            var toDelete = await db.LlmInteractions
                .Where(i => i.CreatedAt < cutoff)
                .OrderBy(i => i.Id)
                .Take(batchSize)
                .ToListAsync(ct);
            if (toDelete.Count == 0) break;

            db.LlmInteractions.RemoveRange(toDelete);
            await db.SaveChangesAsync(ct);
            total += toDelete.Count;
        }

        if (total > 0)
            log.LogInformation("Pruned {Count} LlmInteractions older than {Days} days", total, retentionDays);
    }
}
