using System.Collections.Concurrent;
using AChat.Core.Enums;
using AChat.Core.Interfaces.Services;
using AChat.Core.Options;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AChat.Infrastructure.Telegram;

public partial class TelegramHostedService(
    IServiceScopeFactory scopeFactory,
    TelegramRateLimiter rateLimiter,
    IOptions<TelegramOptions> telegramOptions,
    ILogger<TelegramHostedService> logger) : BackgroundService
{
    private readonly TelegramOptions _options = telegramOptions.Value;
    private readonly ConcurrentDictionary<Guid, (ITelegramBotClient Client, CancellationTokenSource Cts)> _activeBots = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogTelegramServiceStarted(logger);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncBotsAsync(stoppingToken);
        }
    }

    private async Task SyncBotsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var botsWithToken = await db.Bots
            .AsNoTracking()
            .Where(b => b.TelegramToken != null)
            .Select(b => new { b.Id, b.TelegramToken })
            .ToListAsync(ct);

        var activeIds = botsWithToken.Select(b => b.Id).ToHashSet();

        // Stop bots that no longer have a token
        foreach (var (botId, (_, cts)) in _activeBots.ToArray())
        {
            if (!activeIds.Contains(botId))
            {
                cts.Cancel();
                cts.Dispose();
                _activeBots.TryRemove(botId, out _);
                LogBotPollerStopped(logger, botId);
            }
        }

        // Start new bots
        foreach (var bot in botsWithToken)
        {
            if (_activeBots.ContainsKey(bot.Id)) continue;

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var client = new TelegramBotClient(bot.TelegramToken!);

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message]
            };

            client.StartReceiving(
                updateHandler: (c, update, token) => HandleUpdateAsync(bot.Id, c, update, token),
                errorHandler: (c, ex, source, token) => HandleErrorAsync(bot.Id, ex, token),
                receiverOptions: receiverOptions,
                cancellationToken: cts.Token);

            _activeBots[bot.Id] = (client, cts);
            LogBotPollerStarted(logger, bot.Id);
        }
    }

    private async Task HandleUpdateAsync(Guid botId, ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message?.Text is null) return;

        var telegramUserId = update.Message.From?.Id;
        if (telegramUserId is null) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text;

        // Rate limiting
        if (!rateLimiter.AllowGlobal() || !rateLimiter.AllowBot(botId))
        {
            LogRateLimited(logger, botId, telegramUserId.Value);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();
        var botService = scope.ServiceProvider.GetRequiredService<IBotService>();

        var bot = await db.Bots.FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot is null) return;

        // Find or create user by telegram id
        var user = await db.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramUserId, ct);
        if (user is null)
        {
            // Unknown user — create stub and reject
            var stubUsername = $"telegram_{telegramUserId}";
            if (!await db.Users.AnyAsync(u => u.Username == stubUsername, ct))
            {
                var stub = new Core.Entities.User
                {
                    Username = stubUsername,
                    Email = string.Empty,
                    PasswordHash = string.Empty,
                    TelegramId = telegramUserId,
                    IsActive = false,
                    Role = UserRole.User,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.Users.Add(stub);
                await db.SaveChangesAsync(ct);

                // Create bot access request for stub user
                if (!await db.BotAccessRequests.AnyAsync(r => r.BotId == botId && r.RequesterId == stub.Id, ct))
                {
                    db.BotAccessRequests.Add(new Core.Entities.BotAccessRequest
                    {
                        BotId = botId,
                        RequesterId = stub.Id,
                        Status = AccessRequestStatus.Pending,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync(ct);
                }
            }

            var reply = string.IsNullOrWhiteSpace(bot.UnknownUserReply)
                ? _options.DefaultUnknownUserReply
                : bot.UnknownUserReply;
            await client.SendMessage(chatId, reply, cancellationToken: ct);
            LogUnknownTelegramUser(logger, telegramUserId.Value, botId);
            return;
        }

        // Check access
        if (!await botService.HasAccessAsync(botId, user.Id, ct))
        {
            var reply = string.IsNullOrWhiteSpace(bot.UnknownUserReply)
                ? _options.DefaultUnknownUserReply
                : bot.UnknownUserReply;
            await client.SendMessage(chatId, reply, cancellationToken: ct);
            return;
        }

        // Find or create conversation (latest one for this user+bot)
        var conversation = await db.Conversations
            .Where(c => c.BotId == botId && c.UserId == user.Id)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (conversation is null)
        {
            conversation = new Core.Entities.Conversation
            {
                BotId = botId,
                UserId = user.Id,
                Title = "Telegram",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(ct);
        }

        // Show typing indicator
        await client.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

        try
        {
            var responseBuilder = new System.Text.StringBuilder();
            await foreach (var chunk in chatService.StreamAsync(conversation.Id, user.Id, text, ct))
            {
                responseBuilder.Append(chunk);
            }

            if (responseBuilder.Length > 0)
                await client.SendMessage(chatId, responseBuilder.ToString(), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            LogTelegramChatError(logger, botId, user.Id, ex);
            await client.SendMessage(chatId, "Sorry, something went wrong.", cancellationToken: ct);
        }
    }

    private Task HandleErrorAsync(Guid botId, Exception ex, CancellationToken ct)
    {
        LogBotPollerError(logger, botId, ex);
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        foreach (var (_, (_, cts)) in _activeBots)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _activeBots.Clear();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Telegram hosted service started")]
    private static partial void LogTelegramServiceStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bot poller started for bot {BotId}")]
    private static partial void LogBotPollerStarted(ILogger logger, Guid botId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bot poller stopped for bot {BotId}")]
    private static partial void LogBotPollerStopped(ILogger logger, Guid botId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limited telegram message for bot {BotId} from user {TelegramUserId}")]
    private static partial void LogRateLimited(ILogger logger, Guid botId, long telegramUserId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown telegram user {TelegramUserId} messaged bot {BotId}")]
    private static partial void LogUnknownTelegramUser(ILogger logger, long telegramUserId, Guid botId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telegram chat error for bot {BotId}, user {UserId}")]
    private static partial void LogTelegramChatError(ILogger logger, Guid botId, Guid userId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telegram poller error for bot {BotId}")]
    private static partial void LogBotPollerError(ILogger logger, Guid botId, Exception ex);
}
