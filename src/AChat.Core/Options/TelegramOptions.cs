namespace AChat.Core.Options;

public class TelegramOptions
{
    public const string Section = "Telegram";

    public int GlobalRateLimitPerMinute { get; set; } = 100;
    public int PerBotRateLimitPerMinute { get; set; } = 20;
    public string DefaultUnknownUserReply { get; set; } = "I don't know you, go away";
}
