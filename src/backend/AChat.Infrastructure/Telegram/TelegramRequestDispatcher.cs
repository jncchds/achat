using System.Collections.Concurrent;
using System.Text.Json;
using AChat.Core.Entities;
using AChat.Core.Services;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AChat.Infrastructure.Telegram;

public sealed class TelegramRequestDispatcher : BackgroundService, ITelegramRequestDispatcher
{
    private readonly ILogger<TelegramRequestDispatcher> _logger;
    private readonly TelegramRateLimitingOptions _opts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEncryptionService _encryption;
    private readonly ConcurrentDictionary<string, TelegramBotClient> _clients = new();
    private readonly ConcurrentDictionary<Guid, TokenBucket> _perBotOutbound = new();
    private readonly TokenBucket _globalInbound;
    private readonly TokenBucket _globalOutbound;

    public TelegramRequestDispatcher(
        IOptions<TelegramRateLimitingOptions> options,
        IServiceScopeFactory scopeFactory,
        IEncryptionService encryption,
        ILogger<TelegramRequestDispatcher> logger)
    {
        _logger = logger;
        _opts = options.Value;
        _scopeFactory = scopeFactory;
        _encryption = encryption;

        _globalInbound = new TokenBucket(_opts.GlobalInboundPerSecond, _opts.GlobalInboundBurst);
        _globalOutbound = new TokenBucket(_opts.GlobalOutboundPerSecond, _opts.GlobalOutboundBurst);
    }

    public bool TryAcquireInboundToken()
    {
        if (!_opts.Enabled)
        {
            return true;
        }

        return _globalInbound.TryAcquire(1);
    }

    public Task EnqueueSendMessageAsync(
        Guid botId,
        long chatId,
        string text,
        ParseMode? parseMode = null,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken ct = default)
        => EnqueueAsync(new TelegramOutboundMessage
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            CommandType = TelegramOutboundCommandType.SendMessage,
            ChatId = chatId,
            Text = text,
            ParseMode = parseMode?.ToString(),
            ReplyMarkupJson = replyMarkup is null ? null : JsonSerializer.Serialize(replyMarkup),
            AttemptCount = 0,
            AvailableAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }, ct);

    public Task EnqueueSendChatActionAsync(
        Guid botId,
        long chatId,
        ChatAction action,
        CancellationToken ct = default)
        => EnqueueAsync(new TelegramOutboundMessage
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            CommandType = TelegramOutboundCommandType.SendChatAction,
            ChatId = chatId,
            ChatAction = action.ToString(),
            AttemptCount = 0,
            AvailableAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }, ct);

    public Task EnqueueAnswerCallbackQueryAsync(
        Guid botId,
        string callbackQueryId,
        string? text = null,
        CancellationToken ct = default)
        => EnqueueAsync(new TelegramOutboundMessage
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            CommandType = TelegramOutboundCommandType.AnswerCallbackQuery,
            CallbackQueryId = callbackQueryId,
            Text = text,
            AttemptCount = 0,
            AvailableAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }, ct);

    public Task EnqueueEditMessageTextAsync(
        Guid botId,
        long chatId,
        int messageId,
        string text,
        CancellationToken ct = default)
        => EnqueueAsync(new TelegramOutboundMessage
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            CommandType = TelegramOutboundCommandType.EditMessageText,
            ChatId = chatId,
            MessageId = messageId,
            Text = text,
            AttemptCount = 0,
            AvailableAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var message = await DequeueDueMessageAsync(stoppingToken);
            if (message is null)
            {
                await Task.Delay(Math.Max(5, _opts.DispatcherIdleDelayMs), stoppingToken);
                continue;
            }

            await WaitForOutboundPermitAsync(message.BotId, stoppingToken);

            try
            {
                await ExecuteMessageAsync(message, stoppingToken);
                await DeleteQueuedMessageAsync(message.Id, stoppingToken);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                var nextAttempt = message.AttemptCount + 1;
                if (nextAttempt > _opts.MaxRetryAttempts)
                {
                    await DeleteQueuedMessageAsync(message.Id, stoppingToken);
                    _logger.LogError(
                        ex,
                        "Dropping Telegram queued message {MessageId} for bot {BotId} after repeated 429 responses.",
                        message.Id,
                        message.BotId);
                    continue;
                }

                var retryAfterSeconds = ex.Parameters?.RetryAfter ?? _opts.DefaultRetryAfterSeconds;
                await RescheduleAsync(
                    message.Id,
                    nextAttempt,
                    DateTime.UtcNow.AddSeconds(Math.Max(1, retryAfterSeconds)),
                    $"429: {ex.Message}",
                    stoppingToken);

                _logger.LogWarning(
                    "Telegram 429 for bot {BotId}. Requeued message {MessageId} (attempt {Attempt}/{MaxAttempts}) after {DelaySeconds}s.",
                    message.BotId,
                    message.Id,
                    nextAttempt,
                    _opts.MaxRetryAttempts,
                    retryAfterSeconds);
            }
            catch (Exception ex)
            {
                var nextAttempt = message.AttemptCount + 1;
                if (nextAttempt > _opts.MaxRetryAttempts)
                {
                    await DeleteQueuedMessageAsync(message.Id, stoppingToken);
                    _logger.LogError(ex,
                        "Dropping Telegram queued message {MessageId} for bot {BotId} after {Attempts} attempts.",
                        message.Id,
                        message.BotId,
                        nextAttempt - 1);
                    continue;
                }

                await RescheduleAsync(
                    message.Id,
                    nextAttempt,
                    DateTime.UtcNow.AddSeconds(_opts.DefaultRetryAfterSeconds),
                    ex.Message,
                    stoppingToken);

                _logger.LogError(ex,
                    "Failed dispatching Telegram queued message {MessageId} for bot {BotId}. Retrying attempt {Attempt}/{MaxAttempts}.",
                    message.Id,
                    message.BotId,
                    nextAttempt,
                    _opts.MaxRetryAttempts);
            }
        }
    }

    private async Task EnqueueAsync(TelegramOutboundMessage message, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var queuedCount = await db.TelegramOutboundMessages.CountAsync(ct);
        if (_opts.QueueCapacity > 0 && queuedCount >= _opts.QueueCapacity)
        {
            _logger.LogWarning(
                "Telegram outbound queue is above configured capacity ({Count}/{Capacity}). Enqueuing continues.",
                queuedCount,
                _opts.QueueCapacity);
        }

        db.TelegramOutboundMessages.Add(message);
        await db.SaveChangesAsync(ct);
    }

    private async Task<TelegramOutboundMessage?> DequeueDueMessageAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        return await db.TelegramOutboundMessages
            .AsNoTracking()
            .Where(m => m.AvailableAt <= now)
            .OrderBy(m => m.AvailableAt)
            .ThenBy(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private async Task ExecuteMessageAsync(TelegramOutboundMessage message, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var bot = await db.Bots
            .Where(b => b.Id == message.BotId)
            .Select(b => new { b.EncryptedTelegramBotToken })
            .FirstOrDefaultAsync(ct);

        if (bot?.EncryptedTelegramBotToken is null)
        {
            throw new InvalidOperationException($"Bot {message.BotId} has no Telegram token configured.");
        }

        var token = _encryption.Decrypt(bot.EncryptedTelegramBotToken);
        var client = _clients.GetOrAdd(token, static raw => new TelegramBotClient(raw));

        switch (message.CommandType)
        {
            case TelegramOutboundCommandType.SendMessage:
            {
                InlineKeyboardMarkup? replyMarkup = null;
                if (!string.IsNullOrWhiteSpace(message.ReplyMarkupJson))
                {
                    replyMarkup = JsonSerializer.Deserialize<InlineKeyboardMarkup>(message.ReplyMarkupJson);
                }

                var parseMode = ParseMode.None;
                if (!string.IsNullOrWhiteSpace(message.ParseMode)
                    && Enum.TryParse<ParseMode>(message.ParseMode, out var parsedMode))
                {
                    parseMode = parsedMode;
                }

                if (parseMode != ParseMode.None || replyMarkup is not null)
                {
                    await client.SendMessage(
                        message.ChatId!.Value,
                        message.Text!,
                        parseMode: parseMode,
                        replyMarkup: replyMarkup,
                        cancellationToken: ct);
                }
                else
                {
                    await client.SendMessage(message.ChatId!.Value, message.Text!, cancellationToken: ct);
                }

                break;
            }

            case TelegramOutboundCommandType.SendChatAction:
            {
                if (!Enum.TryParse<ChatAction>(message.ChatAction, out var chatAction))
                {
                    chatAction = ChatAction.Typing;
                }

                await client.SendChatAction(message.ChatId!.Value, chatAction, cancellationToken: ct);
                break;
            }

            case TelegramOutboundCommandType.AnswerCallbackQuery:
                await client.AnswerCallbackQuery(message.CallbackQueryId!, text: message.Text, cancellationToken: ct);
                break;

            case TelegramOutboundCommandType.EditMessageText:
                await client.EditMessageText(message.ChatId!.Value, message.MessageId!.Value, message.Text!, cancellationToken: ct);
                break;

            default:
                throw new InvalidOperationException($"Unsupported Telegram command type '{message.CommandType}'.");
        }
    }

    private async Task DeleteQueuedMessageAsync(Guid messageId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entity = await db.TelegramOutboundMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (entity is null)
        {
            return;
        }

        db.TelegramOutboundMessages.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    private async Task RescheduleAsync(
        Guid messageId,
        int attemptCount,
        DateTime availableAt,
        string? lastError,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entity = await db.TelegramOutboundMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (entity is null)
        {
            return;
        }

        entity.AttemptCount = attemptCount;
        entity.AvailableAt = availableAt;
        entity.LastError = lastError;
        entity.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task WaitForOutboundPermitAsync(Guid botId, CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            return;
        }

        var perBotBucket = _perBotOutbound.GetOrAdd(
            botId,
            _ => new TokenBucket(_opts.PerBotOutboundPerSecond, _opts.PerBotOutboundBurst));

        while (!ct.IsCancellationRequested)
        {
            if (_globalOutbound.HasTokens(1) && perBotBucket.HasTokens(1))
            {
                _globalOutbound.TryAcquire(1);
                perBotBucket.TryAcquire(1);
                return;
            }

            var waitMs = Math.Max(1, Math.Min(_globalOutbound.GetWaitTimeMsFor(1), perBotBucket.GetWaitTimeMsFor(1)));
            await Task.Delay(waitMs, ct);
        }
    }

    private sealed class TokenBucket
    {
        private readonly object _sync = new();
        private readonly double _ratePerSecond;
        private readonly double _capacity;
        private double _tokens;
        private DateTime _lastRefillUtc;

        public TokenBucket(int ratePerSecond, int burstCapacity)
        {
            _ratePerSecond = Math.Max(1, ratePerSecond);
            _capacity = Math.Max(1, burstCapacity);
            _tokens = _capacity;
            _lastRefillUtc = DateTime.UtcNow;
        }

        public bool TryAcquire(int tokens)
        {
            lock (_sync)
            {
                RefillLocked();
                if (_tokens < tokens)
                {
                    return false;
                }

                _tokens -= tokens;
                return true;
            }
        }

        public bool HasTokens(int tokens)
        {
            lock (_sync)
            {
                RefillLocked();
                return _tokens >= tokens;
            }
        }

        public int GetWaitTimeMsFor(int tokens)
        {
            lock (_sync)
            {
                RefillLocked();
                if (_tokens >= tokens)
                {
                    return 1;
                }

                var missing = tokens - _tokens;
                var seconds = missing / _ratePerSecond;
                return Math.Max(1, (int)Math.Ceiling(seconds * 1000));
            }
        }

        private void RefillLocked()
        {
            var now = DateTime.UtcNow;
            var elapsedSeconds = (now - _lastRefillUtc).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                return;
            }

            _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _ratePerSecond);
            _lastRefillUtc = now;
        }
    }
}
