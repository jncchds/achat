namespace AChat.Core.Entities;

public enum TelegramOutboundCommandType
{
    SendMessage = 1,
    SendChatAction = 2,
    AnswerCallbackQuery = 3,
    EditMessageText = 4
}
