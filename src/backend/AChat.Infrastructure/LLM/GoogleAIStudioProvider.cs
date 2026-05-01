using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AChat.Core.LLM;

namespace AChat.Infrastructure.LLM;

/// <summary>
/// Google AI Studio (Gemini) provider via the generateContent / streamGenerateContent REST API.
/// </summary>
public class GoogleAIStudioProvider : ILLMChatProvider, ILLMEmbeddingProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string? _embeddingModel;
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/";

    public GoogleAIStudioProvider(IHttpClientFactory httpClientFactory, string apiKey, string model, string? embeddingModel)
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = apiKey;
        _model = model;
        _embeddingModel = embeddingModel ?? "text-embedding-004";
    }

    private HttpClient CreateHttpClient()
    {
        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(BaseUrl);
        return http;
    }

    public async Task<LLMChatCompletionResult> GenerateChatCompletionAsync(LLMChatRequest request, CancellationToken ct = default)
    {
        var body = BuildRequestBody(request);

        var url = $"models/{_model}:generateContent?key={_apiKey}";
        using var response = await CreateHttpClient().PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return new LLMChatCompletionResult
        {
            Content = ExtractResponseText(doc.RootElement),
            Usage = TryParseUsage(doc.RootElement)
        };
    }

    public async IAsyncEnumerable<LLMChatStreamUpdate> StreamChatCompletionAsync(
        LLMChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(request);

        var url = $"models/{_model}:streamGenerateContent?alt=sse&key={_apiKey}";
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var response = await CreateHttpClient().SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            var text = ExtractResponseText(root);
            if (!string.IsNullOrEmpty(text))
                yield return new LLMChatStreamUpdate { Content = text };

            var usage = TryParseUsage(root);
            if (usage is not null)
                yield return new LLMChatStreamUpdate { Usage = usage };
        }
    }

    public async Task<string> GenerateChatAsync(LLMChatRequest request, CancellationToken ct = default)
    {
        var result = await GenerateChatCompletionAsync(request, ct);
        return result.Content;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        LLMChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var update in StreamChatCompletionAsync(request, ct))
        {
            if (!string.IsNullOrEmpty(update.Content))
                yield return update.Content;
        }
    }

    private static string BuildRequestBody(LLMChatRequest request)
    {
        var contents = request.Messages.Select(m => new
        {
            role = m.Role == "assistant" ? "model" : "user",
            parts = new[] { new { text = m.Content } }
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            system_instruction = new { parts = new[] { new { text = request.SystemPrompt } } },
            contents,
            generationConfig = new
            {
                temperature = request.Temperature,
                maxOutputTokens = request.MaxTokens
            }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }

    private static string ExtractResponseText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
            return string.Empty;

        var parts = candidates[0]
            .GetProperty("content")
            .GetProperty("parts")
            .EnumerateArray()
            .Where(p => p.TryGetProperty("text", out _))
            .Select(p => p.GetProperty("text").GetString())
            .Where(t => !string.IsNullOrEmpty(t));

        return string.Concat(parts);
    }

    private static LLMTokenUsageStats? TryParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            return null;

        var promptTokens = usage.TryGetProperty("promptTokenCount", out var prompt)
            ? prompt.GetInt32()
            : (int?)null;
        var completionTokens = usage.TryGetProperty("candidatesTokenCount", out var completion)
            ? completion.GetInt32()
            : (int?)null;
        var totalTokens = usage.TryGetProperty("totalTokenCount", out var total)
            ? total.GetInt32()
            : (int?)null;

        return new LLMTokenUsageStats
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens
        };
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = $"models/{_embeddingModel}",
            content = new { parts = new[] { new { text } } }
        });

        var url = $"models/{_embeddingModel}:embedContent?key={_apiKey}";
        var response = await CreateHttpClient().PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var arr = doc.RootElement.GetProperty("embedding").GetProperty("values");
        return arr.EnumerateArray().Select(e => e.GetSingle()).ToArray();
    }
}
