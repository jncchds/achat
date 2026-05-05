using AChat.Core.Options;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AChat.Infrastructure.BackgroundJobs;

public partial class PersonalityEvolutionJob(
    IServiceScopeFactory scopeFactory,
    IOptions<BotEvolutionOptions> options,
    ILogger<PersonalityEvolutionJob> logger) : BackgroundService
{
    private readonly BotEvolutionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogEvolutionJobStarted(logger);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunEvolutionCycleAsync(stoppingToken);
        }
    }

    private async Task RunEvolutionCycleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var botService = scope.ServiceProvider.GetRequiredService<BotService>();

        var intervalHours = _options.IntervalHours;
        var cutoff = DateTime.UtcNow.AddHours(-intervalHours);

        var botsToEvolve = await db.Bots
            .Include(b => b.Preset)
            .Where(b => b.LastEvolvedAt == null || b.LastEvolvedAt < cutoff ||
                        (b.EvolutionIntervalHours != null &&
                         b.LastEvolvedAt < DateTime.UtcNow.AddHours(-b.EvolutionIntervalHours.Value)))
            .ToListAsync(ct);

        LogEvolutionCycleStarted(logger, botsToEvolve.Count);

        foreach (var bot in botsToEvolve)
        {
            if (ct.IsCancellationRequested) break;

            var since = bot.LastEvolvedAt ?? DateTime.MinValue;
            var messageCount = await db.Messages
                .Include(m => m.Conversation)
                .CountAsync(m => m.Conversation.BotId == bot.Id
                              && m.Conversation.UserId == bot.OwnerId
                              && m.CreatedAt > since
                              && m.Role == Core.Enums.MessageRole.User, ct);

            if (messageCount < _options.MinOwnerMessagesRequired)
            {
                LogEvolutionSkipped(logger, bot.Id, bot.Name, messageCount, _options.MinOwnerMessagesRequired);
                continue;
            }

            await botService.RunEvolutionAsync(bot, null, ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Personality evolution job started")]
    private static partial void LogEvolutionJobStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evolution cycle started, {Count} bots to evaluate")]
    private static partial void LogEvolutionCycleStarted(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping evolution for bot {BotId} ({BotName}): {MessageCount}/{Required} owner messages")]
    private static partial void LogEvolutionSkipped(ILogger logger, Guid botId, string botName, int messageCount, int required);
}
