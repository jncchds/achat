using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AChat.Core.LLM;

namespace AChat.Infrastructure.LLM;

public class OpenAIProvider : ILLMChatProvider, ILLMEmbeddingProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string? _embeddingModel;

    public OpenAIProvider(IHttpClientFactory httpClientFactory, string apiKey, string model, string? embeddingModel, string baseUrl = "https://api.openai.com/v1/")
    {
        _httpClientFactory = httpClientFactory;
        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        _model = model;
        _embeddingModel = embeddingModel ?? "text-embedding-3-small";
    }

    private HttpClient CreateHttpClient()
    {
        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(_baseUrl);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return http;
    }

    public async Task<LLMChatCompletionResult> GenerateChatCompletionAsync(LLMChatRequest request, CancellationToken ct = default)
    {
        var messages = BuildMessages(request);

        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            messages,
            stream = false,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        using var response = await CreateHttpClient().PostAsync(
            "chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        return new LLMChatCompletionResult
        {
            Content = content,
            Usage = TryParseUsage(doc.RootElement)
        };
    }

    public async IAsyncEnumerable<LLMChatStreamUpdate> StreamChatCompletionAsync(
        LLMChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = BuildMessages(request);

        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            messages,
            stream = true,
            stream_options = new { include_usage = true },
            temperature = request.Temperature,
            max_tokens = request.MaxTokens
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
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
            if (data == "[DONE]") yield break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("content", out var contentElement))
                {
                    var text = contentElement.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new LLMChatStreamUpdate { Content = text };
                    }
                }
            }

            var usage = TryParseUsage(root);
            if (usage is not null)
            {
                yield return new LLMChatStreamUpdate { Usage = usage };
            }
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

    private static List<object> BuildMessages(LLMChatRequest request)
    {
        var messages = new List<object>
        {
            new { role = "system", content = request.SystemPrompt }
        };
        messages.AddRange(request.Messages.Select(m => new { role = m.Role, content = m.Content }));
        return messages;
    }

    private static LLMTokenUsageStats? TryParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement)
            || usageElement.ValueKind != JsonValueKind.Object)
            return null;

        var promptTokens = usageElement.TryGetProperty("prompt_tokens", out var prompt)
            ? prompt.GetInt32()
            : (int?)null;
        var completionTokens = usageElement.TryGetProperty("completion_tokens", out var completion)
            ? completion.GetInt32()
            : (int?)null;
        var totalTokens = usageElement.TryGetProperty("total_tokens", out var total)
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
        var body = JsonSerializer.Serialize(new { model = _embeddingModel, input = text });
        var response = await CreateHttpClient().PostAsync("embeddings",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        return arr.EnumerateArray().Select(e => e.GetSingle()).ToArray();
    }
}
