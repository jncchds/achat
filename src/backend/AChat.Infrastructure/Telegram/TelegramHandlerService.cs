using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Core.Services;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using DomainUser = AChat.Core.Entities.User;
using DomainMessage = AChat.Core.Entities.Message;

namespace AChat.Infrastructure.Telegram;

public class TelegramHandlerService
{
    private readonly AppDbContext _db;
    private readonly ILLMProviderFactory _providerFactory;
    private readonly IEncryptionService _encryption;
    private readonly int _ragTopK;
    private readonly int _recentWindowSize;

    public TelegramHandlerService(
        AppDbContext db,
        ILLMProviderFactory providerFactory,
        IEncryptionService encryption,
        int ragTopK = 5,
        int recentWindowSize = 20)
    {
        _db = db;
        _providerFactory = providerFactory;
        _encryption = encryption;
        _ragTopK = ragTopK;
        _recentWindowSize = recentWindowSize;
    }

    public async Task HandleUpdateAsync(Guid botId, Update update, CancellationToken ct = default)
    {
        if (update.CallbackQuery is { } callbackQuery)
        {
            await HandleCallbackQueryAsync(botId, callbackQuery, ct);
            return;
        }

        if (update.Message is not { } message) return;
        if (message.Text is null) return;

        var telegramUserId = message.From?.Id;
        if (telegramUserId is null) return;

        var bot = await _db.Bots
            .Include(b => b.LLMProviderPreset)
            .Include(b => b.EmbeddingPreset)
            .FirstOrDefaultAsync(b => b.Id == botId, ct);

        if (bot?.EncryptedTelegramBotToken is null) return;

        var rawToken = _encryption.Decrypt(bot.EncryptedTelegramBotToken);
        var telegramClient = new TelegramBotClient(rawToken);

        // Look up access status
        var accessEntry = await _db.BotAccessLists.FirstOrDefaultAsync(
            a => a.BotId == botId
                 && a.SubjectType == AccessSubjectType.TelegramUser
                 && a.SubjectId == telegramUserId.Value.ToString(), ct);

        if (accessEntry?.Status == AccessStatus.Denied)
        {
            // Silently drop
            return;
        }

        if (accessEntry?.Status != AccessStatus.Allowed)
        {
            // Unknown sender
            await telegramClient.SendMessage(
                message.Chat.Id,
                "I don't know you, go away",
                cancellationToken: ct);

            // Create access request if not already pending
            var existing = await _db.BotAccessRequests.AnyAsync(
                r => r.BotId == botId
                     && r.SubjectType == AccessSubjectType.TelegramUser
                     && r.SubjectId == telegramUserId.Value.ToString()
                     && r.Status == AccessRequestStatus.Pending, ct);

            if (!existing)
            {
                _db.BotAccessRequests.Add(new BotAccessRequest
                {
                    Id = Guid.NewGuid(),
                    BotId = botId,
                    SubjectType = AccessSubjectType.TelegramUser,
                    SubjectId = telegramUserId.Value.ToString(),
                    DisplayName = message.From?.Username ?? message.From?.FirstName,
                    RequestedAt = DateTime.UtcNow,
                    Status = AccessRequestStatus.Pending
                });
                await _db.SaveChangesAsync(ct);

                // Notify owner with inline Approve/Deny keyboard
                await NotifyOwnerAsync(telegramClient, bot, telegramUserId.Value,
                    message.From?.Username ?? message.From?.FirstName, ct);
            }
            return;
        }

        // Allowed — find or create stub user
        var user = await _db.Users.FirstOrDefaultAsync(u => u.TelegramId == telegramUserId, ct);
        if (user is null)
        {
            user = new DomainUser
            {
                Id = Guid.NewGuid(),
                TelegramId = telegramUserId,
                IsStubAccount = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);
        }

        // Send typing action
        await telegramClient.SendChatAction(message.Chat.Id, ChatAction.Typing, cancellationToken: ct);

        // Persist user message
        var userMsg = new DomainMessage
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            UserId = user.Id,
            Role = MessageRole.User,
            Content = message.Text,
            Source = MessageSource.Telegram,
            CreatedAt = DateTime.UtcNow
        };
        _db.Messages.Add(userMsg);
        await _db.SaveChangesAsync(ct);

        // Generate embedding
        float[]? queryEmbedding = null;
        if (bot.EmbeddingPreset is not null)
        {
            try
            {
                var embProvider = _providerFactory.GetEmbeddingProvider(bot.EmbeddingPreset);
                queryEmbedding = await embProvider.GenerateEmbeddingAsync(message.Text, ct);
                userMsg.Embedding = new Vector(queryEmbedding);
                await _db.SaveChangesAsync(ct);
            }
            catch { /* non-fatal */ }
        }

        if (bot.LLMProviderPreset is null) return;

        // Build context + generate full response
        var contextBuilder = new ChatContextBuilder(_db, _ragTopK, _recentWindowSize);
        var chatRequest = await contextBuilder.BuildAsync(bot, user.Id, message.Text, queryEmbedding, ct);

        var chatProvider = _providerFactory.GetChatProvider(bot.LLMProviderPreset);
        var responseText = await chatProvider.GenerateChatAsync(chatRequest, ct);

        // Persist assistant message
        var assistantMsg = new DomainMessage
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            UserId = user.Id,
            Role = MessageRole.Assistant,
            Content = responseText,
            Source = MessageSource.Telegram,
            CreatedAt = DateTime.UtcNow
        };
        _db.Messages.Add(assistantMsg);
        await _db.SaveChangesAsync(ct);

        // Embed assistant message
        if (bot.EmbeddingPreset is not null)
        {
            try
            {
                var embProvider = _providerFactory.GetEmbeddingProvider(bot.EmbeddingPreset);
                var emb = await embProvider.GenerateEmbeddingAsync(responseText, ct);
                assistantMsg.Embedding = new Vector(emb);
                await _db.SaveChangesAsync(ct);
            }
            catch { /* non-fatal */ }
        }

        await telegramClient.SendMessage(message.Chat.Id, responseText, cancellationToken: ct);
    }

    private async Task HandleCallbackQueryAsync(Guid botId, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var data = callbackQuery.Data;
        if (data is null) return;

        // Format: "approve:{botId}:{telegramUserId}" or "deny:{botId}:{telegramUserId}"
        var parts = data.Split(':');
        if (parts.Length != 3) return;
        if (!Guid.TryParse(parts[1], out var callbackBotId)) return;
        if (!long.TryParse(parts[2], out var requesterTelegramId)) return;

        // Only handle callbacks intended for this bot's webhook
        if (callbackBotId != botId) return;

        var action = parts[0]; // "approve" or "deny"

        var bot = await _db.Bots.FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot?.EncryptedTelegramBotToken is null) return;

        var rawToken = _encryption.Decrypt(bot.EncryptedTelegramBotToken);
        var telegramClient = new TelegramBotClient(rawToken);

        // Verify the callback sender is the bot owner
        var owner = await _db.Users.FirstOrDefaultAsync(u => u.Id == bot.OwnerId, ct);
        if (owner?.TelegramId != callbackQuery.From.Id) return;

        var subjectId = requesterTelegramId.ToString();

        // Find the pending request
        var request = await _db.BotAccessRequests.FirstOrDefaultAsync(
            r => r.BotId == botId
                 && r.SubjectType == AccessSubjectType.TelegramUser
                 && r.SubjectId == subjectId
                 && r.Status == AccessRequestStatus.Pending, ct);

        if (request is null)
        {
            await telegramClient.AnswerCallbackQuery(callbackQuery.Id,
                "Request no longer pending.", cancellationToken: ct);
            return;
        }

        // Upsert access list entry
        var accessEntry = await _db.BotAccessLists.FirstOrDefaultAsync(
            a => a.BotId == botId
                 && a.SubjectType == AccessSubjectType.TelegramUser
                 && a.SubjectId == subjectId, ct);

        string resultText;

        if (action == "approve")
        {
            request.Status = AccessRequestStatus.Approved;
            request.ResolvedAt = DateTime.UtcNow;
            request.ResolvedByUserId = owner.Id;

            if (accessEntry is null)
            {
                _db.BotAccessLists.Add(new BotAccessList
                {
                    Id = Guid.NewGuid(),
                    BotId = botId,
                    SubjectType = AccessSubjectType.TelegramUser,
                    SubjectId = subjectId,
                    Status = AccessStatus.Allowed
                });
            }
            else
            {
                accessEntry.Status = AccessStatus.Allowed;
            }

            // Create stub user if none exists
            if (!await _db.Users.AnyAsync(u => u.TelegramId == requesterTelegramId, ct))
            {
                _db.Users.Add(new DomainUser
                {
                    Id = Guid.NewGuid(),
                    TelegramId = requesterTelegramId,
                    IsStubAccount = true,
                    CreatedAt = DateTime.UtcNow
                });
            }

            resultText = $"✅ Approved access for {request.DisplayName ?? subjectId}";
        }
        else // deny
        {
            request.Status = AccessRequestStatus.Denied;
            request.ResolvedAt = DateTime.UtcNow;
            request.ResolvedByUserId = owner.Id;

            if (accessEntry is null)
            {
                _db.BotAccessLists.Add(new BotAccessList
                {
                    Id = Guid.NewGuid(),
                    BotId = botId,
                    SubjectType = AccessSubjectType.TelegramUser,
                    SubjectId = subjectId,
                    Status = AccessStatus.Denied
                });
            }
            else
            {
                accessEntry.Status = AccessStatus.Denied;
            }

            resultText = $"❌ Denied access for {request.DisplayName ?? subjectId}";
        }

        await _db.SaveChangesAsync(ct);

        // Acknowledge the button press and update the message
        await telegramClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);

        if (callbackQuery.Message is not null)
        {
            await telegramClient.EditMessageText(
                callbackQuery.Message.Chat.Id,
                callbackQuery.Message.MessageId,
                resultText,
                cancellationToken: ct);
        }
    }

    private async Task NotifyOwnerAsync(
        TelegramBotClient client,
        Bot bot,
        long requesterTelegramId,
        string? displayName,
        CancellationToken ct)
    {
        var owner = await _db.Users.FindAsync([bot.OwnerId], ct);
        if (owner?.TelegramId is null) return;

        var text = $"New access request for bot *{bot.Name}*\nFrom: {displayName ?? requesterTelegramId.ToString()}";
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Approve",
                    $"approve:{bot.Id}:{requesterTelegramId}"),
                InlineKeyboardButton.WithCallbackData("❌ Deny",
                    $"deny:{bot.Id}:{requesterTelegramId}")
            }
        });

        await client.SendMessage(
            owner.TelegramId.Value,
            text,
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: ct);
    }
}
