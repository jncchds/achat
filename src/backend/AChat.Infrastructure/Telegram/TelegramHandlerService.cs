using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using DomainUser = AChat.Core.Entities.User;
using DomainMessage = AChat.Core.Entities.Message;
using TelegramMessage = Telegram.Bot.Types.Message;

namespace AChat.Infrastructure.Telegram;

public class TelegramHandlerService
{
    private readonly AppDbContext _db;
    private readonly ILLMProviderFactory _providerFactory;
    private readonly ITelegramRequestDispatcher _dispatcher;
    private readonly EvolutionOptions _evolutionOptions;
    private readonly int _ragTopK;
    private readonly int _recentWindowSize;

    public TelegramHandlerService(
        AppDbContext db,
        ILLMProviderFactory providerFactory,
        ITelegramRequestDispatcher dispatcher,
        IOptions<EvolutionOptions> evolutionOptions,
        int ragTopK = 5,
        int recentWindowSize = 20)
    {
        _db = db;
        _providerFactory = providerFactory;
        _dispatcher = dispatcher;
        _evolutionOptions = evolutionOptions.Value;
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
            await _dispatcher.EnqueueSendMessageAsync(
                botId,
                message.Chat.Id,
                "I don't know you, go away",
                ct: ct);

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
                await NotifyOwnerAsync(bot, telegramUserId.Value,
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

        if (message.Text.StartsWith('/'))
        {
            var handled = await HandleTelegramCommandAsync(bot, user, message, ct);
            if (handled) return;
        }

        var conversation = await ResolveConversationForTelegramAsync(bot.Id, user.Id, ct);

        // Send typing action
        await _dispatcher.EnqueueSendChatActionAsync(botId, message.Chat.Id, ChatAction.Typing, ct);

        // Persist user message
        var userMsg = new DomainMessage
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            UserId = user.Id,
            ConversationId = conversation.Id,
            Role = MessageRole.User,
            Content = message.Text,
            Source = MessageSource.Telegram,
            CreatedAt = DateTime.UtcNow
        };
        _db.Messages.Add(userMsg);

        if (conversation.Title == "New conversation")
            conversation.Title = BuildConversationTitle(message.Text);
        conversation.UpdatedAt = DateTime.UtcNow;
        conversation.LastMessageAt = DateTime.UtcNow;

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
        var chatRequest = await contextBuilder.BuildAsync(
            bot,
            user.Id,
            conversation.Id,
            message.Text,
            queryEmbedding,
            ct);

        var chatProvider = _providerFactory.GetChatProvider(bot.LLMProviderPreset);
        var responseText = await chatProvider.GenerateChatAsync(chatRequest, ct);

        // Persist assistant message
        var assistantMsg = new DomainMessage
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            UserId = user.Id,
            ConversationId = conversation.Id,
            Role = MessageRole.Assistant,
            Content = responseText,
            Source = MessageSource.Telegram,
            CreatedAt = DateTime.UtcNow
        };
        _db.Messages.Add(assistantMsg);

        conversation.UpdatedAt = DateTime.UtcNow;
        conversation.LastMessageAt = DateTime.UtcNow;
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

        await _dispatcher.EnqueueSendMessageAsync(botId, message.Chat.Id, responseText, ct: ct);
    }

    private async Task HandleCallbackQueryAsync(Guid botId, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var data = callbackQuery.Data;
        if (data is null) return;

        if (data.StartsWith("conv:", StringComparison.Ordinal))
        {
            await HandleConversationSelectionCallbackAsync(botId, callbackQuery, ct);
            return;
        }

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
            await _dispatcher.EnqueueAnswerCallbackQueryAsync(
                botId,
                callbackQuery.Id,
                "Request no longer pending.",
                ct);
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
        await _dispatcher.EnqueueAnswerCallbackQueryAsync(botId, callbackQuery.Id, ct: ct);

        if (callbackQuery.Message is not null)
        {
            await _dispatcher.EnqueueEditMessageTextAsync(
                botId,
                callbackQuery.Message.Chat.Id,
                callbackQuery.Message.MessageId,
                resultText,
                ct);
        }
    }

    private async Task<bool> HandleTelegramCommandAsync(
        AChat.Core.Entities.Bot bot,
        DomainUser user,
        TelegramMessage message,
        CancellationToken ct)
    {
        var command = message.Text!.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

        if (command is "/new" or "/newconversation" or "/new_conversation")
        {
            var conversation = await CreateNewConversationAsync(bot.Id, user.Id, "New conversation", ct);
            await _dispatcher.EnqueueSendMessageAsync(
                bot.Id,
                message.Chat.Id,
                $"🆕 Started a new conversation: {conversation.Title}",
                ct: ct);
            return true;
        }

        if (command is "/conversations" or "/continue")
        {
            var state = await _db.BotConversationStates
                .FirstOrDefaultAsync(s => s.BotId == bot.Id && s.UserId == user.Id, ct);

            var conversations = await _db.BotConversations
                .Where(c => c.BotId == bot.Id && c.UserId == user.Id)
                .OrderByDescending(c => c.UpdatedAt)
                .Take(10)
                .ToListAsync(ct);

            if (conversations.Count == 0)
            {
                await _dispatcher.EnqueueSendMessageAsync(
                    bot.Id,
                    message.Chat.Id,
                    "You don't have any conversations yet. Use /new to start one.",
                    ct: ct);
                return true;
            }

            var keyboardRows = conversations
                .Select(c =>
                {
                    var title = c.Title.Length > 40 ? $"{c.Title[..39]}…" : c.Title;
                    var marker = state?.CurrentConversationId == c.Id ? "✅ " : string.Empty;
                    return new[]
                    {
                        InlineKeyboardButton.WithCallbackData($"{marker}{title}", $"conv:{c.Id}")
                    };
                })
                .ToArray();

            await _dispatcher.EnqueueSendMessageAsync(
                bot.Id,
                message.Chat.Id,
                "Pick a conversation to continue:",
                replyMarkup: new InlineKeyboardMarkup(keyboardRows),
                ct: ct);

            return true;
        }

        if (command is "/start")
        {
            var isOwner = user.Id == bot.OwnerId;
            var helpText = "Commands:\n/new — start a new conversation\n/conversations — choose a conversation to continue";
            if (isOwner)
                helpText += "\n\n*Owner commands:*\n/persona \\<direction\\> — nudge the bot's personality toward a direction\n/resetpersona — revert the evolved persona back to the original character";
            await _dispatcher.EnqueueSendMessageAsync(
                bot.Id,
                message.Chat.Id,
                helpText,
                parseMode: ParseMode.MarkdownV2,
                ct: ct);
            return true;
        }

        // ── Owner-only commands ──────────────────────────────────────────

        if (command is "/persona" or "/resetpersona")
        {
            if (user.Id != bot.OwnerId)
            {
                await _dispatcher.EnqueueSendMessageAsync(
                    bot.Id,
                    message.Chat.Id,
                    "Only the bot owner can use this command.",
                    ct: ct);
                return true;
            }

            if (command is "/resetpersona")
            {
                bot.EvolvingPersonaPrompt = bot.CharacterDescription;
                bot.PersonaPushText = null;
                bot.PersonaPushRemainingCycles = 0;
                bot.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                await _dispatcher.EnqueueSendMessageAsync(
                    bot.Id,
                    message.Chat.Id,
                    "✅ Persona has been reset to the original character description.",
                    ct: ct);
                return true;
            }

            // /persona <direction>
            var parts = message.Text!.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            {
                await _dispatcher.EnqueueSendMessageAsync(
                    bot.Id,
                    message.Chat.Id,
                    "Usage: /persona <direction>\nExample: /persona become more sarcastic and witty",
                    ct: ct);
                return true;
            }

            bot.PersonaPushText = parts[1].Trim();
            bot.PersonaPushRemainingCycles = _evolutionOptions.PersonaPushDecayCycles;
            bot.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _dispatcher.EnqueueSendMessageAsync(
                bot.Id,
                message.Chat.Id,
                $"✅ Persona push set for {bot.PersonaPushRemainingCycles} evolution cycles:\n\"{bot.PersonaPushText}\"",
                ct: ct);
            return true;
        }

        return false;
    }

    private async Task HandleConversationSelectionCallbackAsync(
        Guid botId,
        CallbackQuery callbackQuery,
        CancellationToken ct)
    {
        var data = callbackQuery.Data;
        if (data is null) return;

        var rawConversationId = data["conv:".Length..];
        if (!Guid.TryParse(rawConversationId, out var conversationId)) return;

        var bot = await _db.Bots.FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot?.EncryptedTelegramBotToken is null) return;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.TelegramId == callbackQuery.From.Id, ct);
        if (user is null)
        {
            await _dispatcher.EnqueueAnswerCallbackQueryAsync(botId, callbackQuery.Id, "Unknown user.", ct);
            return;
        }

        var canUse = await _db.BotAccessLists.AnyAsync(a =>
            a.BotId == botId
            && a.SubjectType == AccessSubjectType.TelegramUser
            && a.SubjectId == callbackQuery.From.Id.ToString()
            && a.Status == AccessStatus.Allowed, ct);

        if (!canUse)
        {
            await _dispatcher.EnqueueAnswerCallbackQueryAsync(botId, callbackQuery.Id, "Access denied.", ct);
            return;
        }

        var conversation = await _db.BotConversations.FirstOrDefaultAsync(c =>
            c.Id == conversationId && c.BotId == botId && c.UserId == user.Id, ct);

        if (conversation is null)
        {
            await _dispatcher.EnqueueAnswerCallbackQueryAsync(botId, callbackQuery.Id, "Conversation not found.", ct);
            return;
        }

        await UpsertConversationStateAsync(botId, user.Id, conversation.Id, ct);
        await _db.SaveChangesAsync(ct);

        await _dispatcher.EnqueueAnswerCallbackQueryAsync(botId, callbackQuery.Id, "Conversation selected.", ct);

        if (callbackQuery.Message is not null)
        {
            await _dispatcher.EnqueueEditMessageTextAsync(
                botId,
                callbackQuery.Message.Chat.Id,
                callbackQuery.Message.MessageId,
                $"✅ Active conversation: {conversation.Title}",
                ct);
        }
    }

    private async Task<BotConversation> ResolveConversationForTelegramAsync(Guid botId, Guid userId, CancellationToken ct)
    {
        var state = await _db.BotConversationStates
            .FirstOrDefaultAsync(s => s.BotId == botId && s.UserId == userId, ct);

        if (state is not null)
        {
            var current = await _db.BotConversations
                .FirstOrDefaultAsync(c => c.Id == state.CurrentConversationId
                                          && c.BotId == botId
                                          && c.UserId == userId, ct);
            if (current is not null) return current;
        }

        var latest = await _db.BotConversations
            .Where(c => c.BotId == botId && c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (latest is not null)
        {
            await UpsertConversationStateAsync(botId, userId, latest.Id, ct);
            await _db.SaveChangesAsync(ct);
            return latest;
        }

        return await CreateNewConversationAsync(botId, userId, "New conversation", ct);
    }

    private async Task<BotConversation> CreateNewConversationAsync(
        Guid botId,
        Guid userId,
        string title,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var conversation = new BotConversation
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            UserId = userId,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
            LastMessageAt = now
        };

        _db.BotConversations.Add(conversation);
        await UpsertConversationStateAsync(botId, userId, conversation.Id, ct);
        await _db.SaveChangesAsync(ct);
        return conversation;
    }

    private async Task UpsertConversationStateAsync(
        Guid botId,
        Guid userId,
        Guid conversationId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var state = await _db.BotConversationStates
            .FirstOrDefaultAsync(s => s.BotId == botId && s.UserId == userId, ct);

        if (state is null)
        {
            _db.BotConversationStates.Add(new BotConversationState
            {
                Id = Guid.NewGuid(),
                BotId = botId,
                UserId = userId,
                CurrentConversationId = conversationId,
                UpdatedAt = now
            });
        }
        else
        {
            state.CurrentConversationId = conversationId;
            state.UpdatedAt = now;
        }
    }

    private static string BuildConversationTitle(string content)
    {
        var normalized = content.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "New conversation";

        var firstLine = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        const int maxLen = 64;
        return firstLine.Length <= maxLen
            ? firstLine
            : $"{firstLine[..(maxLen - 1)].TrimEnd()}…";
    }


    private async Task NotifyOwnerAsync(
        AChat.Core.Entities.Bot bot,
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

        await _dispatcher.EnqueueSendMessageAsync(
            bot.Id,
            owner.TelegramId.Value,
            text,
            parseMode: ParseMode.Markdown,
            replyMarkup: keyboard,
            ct: ct);
    }
}
