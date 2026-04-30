using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AChat.Infrastructure.Telegram;

public class TelegramWebhookService : ITelegramWebhookService
{
    private readonly HttpClient _http;
    private readonly string _webhookBaseUrl;

    public TelegramWebhookService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _webhookBaseUrl = config["Telegram:WebhookBaseUrl"]
            ?? throw new InvalidOperationException("Telegram:WebhookBaseUrl is not configured.");
    }

    public async Task RegisterWebhookAsync(Guid botId, string token, CancellationToken ct = default)
    {
        var webhookUrl = $"{_webhookBaseUrl.TrimEnd('/')}/api/telegram/webhook/{botId}";
        var url = $"https://api.telegram.org/bot{token}/setWebhook";

        var payload = JsonSerializer.Serialize(new
        {
            url = webhookUrl,
            secret_token = token[^10..] // last 10 chars of token as shared secret
        });

        var response = await _http.PostAsync(url,
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteWebhookAsync(string token, CancellationToken ct = default)
    {
        var url = $"https://api.telegram.org/bot{token}/deleteWebhook";
        var response = await _http.PostAsync(url, null, ct);
        response.EnsureSuccessStatusCode();
    }
}
