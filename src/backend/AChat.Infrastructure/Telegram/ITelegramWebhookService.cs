namespace AChat.Infrastructure.Telegram;

public interface ITelegramWebhookService
{
    Task RegisterWebhookAsync(Guid botId, string token, CancellationToken ct = default);
    Task DeleteWebhookAsync(string token, CancellationToken ct = default);
    Task<TelegramWebhookInfoResult> GetWebhookInfoAsync(string token, CancellationToken ct = default);
}

public sealed record TelegramWebhookInfoResult(
    bool Ok,
    string? Description,
    TelegramWebhookInfo? Result);

public sealed record TelegramWebhookInfo(
    string Url,
    bool HasCustomCertificate,
    int PendingUpdateCount,
    string? IpAddress,
    int? LastErrorDate,
    string? LastErrorMessage,
    int? LastSynchronizationErrorDate,
    int? MaxConnections,
    IReadOnlyList<string>? AllowedUpdates);
