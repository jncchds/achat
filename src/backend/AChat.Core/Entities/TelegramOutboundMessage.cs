namespace AChat.Core.Entities;

public class TelegramOutboundMessage
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public TelegramOutboundCommandType CommandType { get; set; }

    public long? ChatId { get; set; }
    public int? MessageId { get; set; }
    public string? Text { get; set; }
    public string? ParseMode { get; set; }
    public string? ReplyMarkupJson { get; set; }
    public string? ChatAction { get; set; }
    public string? CallbackQueryId { get; set; }

    public int AttemptCount { get; set; }
    public DateTime AvailableAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
}
