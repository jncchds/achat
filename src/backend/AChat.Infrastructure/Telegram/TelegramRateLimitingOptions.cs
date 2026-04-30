namespace AChat.Infrastructure.Telegram;

public class TelegramRateLimitingOptions
{
    public bool Enabled { get; set; } = true;

    // Inbound webhook admission
    public int GlobalInboundPerSecond { get; set; } = 60;
    public int GlobalInboundBurst { get; set; } = 120;

    // Outbound API calls to Telegram
    public int GlobalOutboundPerSecond { get; set; } = 30;
    public int GlobalOutboundBurst { get; set; } = 60;
    public int PerBotOutboundPerSecond { get; set; } = 20;
    public int PerBotOutboundBurst { get; set; } = 30;

    // Queue behavior
    public int QueueCapacity { get; set; } = 5000;
    public int DispatcherIdleDelayMs { get; set; } = 25;
    public int MaxRetryAttempts { get; set; } = 5;
    public int DefaultRetryAfterSeconds { get; set; } = 2;
}
