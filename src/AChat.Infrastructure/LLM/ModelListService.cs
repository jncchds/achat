using System.Net.Http.Json;
using System.Text.Json;
using AChat.Core.Entities;
using AChat.Core.Enums;
using AChat.Core.Interfaces.Services;
using Microsoft.Extensions.Logging;

namespace AChat.Infrastructure.LLM;

public partial class ModelListService(ILogger<ModelListService> logger) : IModelListService
{
    public async Task<IReadOnlyList<string>> GetModelsAsync(LlmPreset preset, CancellationToken ct = default)
    {
        try
        {
            return preset.ProviderType switch
            {
                ProviderType.Ollama => await GetOllamaModelsAsync(preset, ct),
                ProviderType.OpenAI => await GetOpenAiModelsAsync(preset, ct),
                ProviderType.GoogleAI => await GetGoogleAiModelsAsync(preset, ct),
                _ => []
            };
        }
        catch (Exception ex)
        {
            LogModelListError(logger, preset.ProviderType.ToString(), ex);
            return [];
        }
    }

    private static async Task<IReadOnlyList<string>> GetOllamaModelsAsync(LlmPreset preset, CancellationToken ct)
    {
        using var client = new HttpClient { BaseAddress = new Uri(preset.ProviderUrl.TrimEnd('/')) };
        var response = await client.GetFromJsonAsync<JsonElement>("/api/tags", ct);
        return response.TryGetProperty("models", out var models)
            ? models.EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? string.Empty)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList()
            : [];
    }

    private static async Task<IReadOnlyList<string>> GetOpenAiModelsAsync(LlmPreset preset, CancellationToken ct)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", preset.ApiToken ?? string.Empty);
        var baseUrl = string.IsNullOrWhiteSpace(preset.ProviderUrl)
            ? "https://api.openai.com"
            : preset.ProviderUrl.TrimEnd('/');
        var response = await client.GetFromJsonAsync<JsonElement>($"{baseUrl}/v1/models", ct);
        return response.TryGetProperty("data", out var data)
            ? data.EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? string.Empty)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .ToList()
            : [];
    }

    private static async Task<IReadOnlyList<string>> GetGoogleAiModelsAsync(LlmPreset preset, CancellationToken ct)
    {
        using var client = new HttpClient();
        var apiKey = preset.ApiToken ?? string.Empty;
        var response = await client.GetFromJsonAsync<JsonElement>(
            $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}", ct);
        return response.TryGetProperty("models", out var models)
            ? models.EnumerateArray()
                .Select(m => m.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList()
            : [];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to load model list for provider {Provider}")]
    private static partial void LogModelListError(ILogger logger, string provider, Exception ex);
}
