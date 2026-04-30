using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AChat.Infrastructure.Telegram;

public interface ITelegramRequestDispatcher
{
    bool TryAcquireInboundToken();

    Task EnqueueSendMessageAsync(
        Guid botId,
        long chatId,
        string text,
        ParseMode? parseMode = null,
        InlineKeyboardMarkup? replyMarkup = null,
        CancellationToken ct = default);

    Task EnqueueSendChatActionAsync(
        Guid botId,
        long chatId,
        ChatAction action,
        CancellationToken ct = default);

    Task EnqueueAnswerCallbackQueryAsync(
        Guid botId,
        string callbackQueryId,
        string? text = null,
        CancellationToken ct = default);

    Task EnqueueEditMessageTextAsync(
        Guid botId,
        long chatId,
        int messageId,
        string text,
        CancellationToken ct = default);
}
