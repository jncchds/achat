namespace AChat.Infrastructure.Telegram;

public interface ITelegramWebhookService
{
    Task RegisterWebhookAsync(Guid botId, string token, CancellationToken ct = default);
    Task DeleteWebhookAsync(string token, CancellationToken ct = default);
}
