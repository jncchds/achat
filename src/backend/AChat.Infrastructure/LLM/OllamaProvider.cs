using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AChat.Core.LLM;

namespace AChat.Infrastructure.LLM;

public class OllamaProvider : ILLMChatProvider, ILLMEmbeddingProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string? _embeddingModel;

    public OllamaProvider(IHttpClientFactory httpClientFactory, string baseUrl, string model, string? embeddingModel)
    {
        _httpClientFactory = httpClientFactory;
        _baseUrl = baseUrl.TrimEnd('/') + "/";
        _model = model;
        _embeddingModel = embeddingModel ?? model;
    }

    private HttpClient CreateHttpClient()
    {
        var http = _httpClientFactory.CreateClient();
        http.BaseAddress = new Uri(_baseUrl);
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
            options = new
            {
                temperature = request.Temperature,
                num_predict = request.MaxTokens
            }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        using var response = await CreateHttpClient().PostAsync(
            "api/chat",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var content = root
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        return new LLMChatCompletionResult
        {
            Content = content,
            Usage = TryParseUsage(root)
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
            options = new
            {
                temperature = request.Temperature,
                num_predict = request.MaxTokens
            }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/chat")
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

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var contentElement))
            {
                var content = contentElement.GetString();
                if (!string.IsNullOrEmpty(content))
                    yield return new LLMChatStreamUpdate { Content = content };
            }

            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                var usage = TryParseUsage(root);
                if (usage is not null)
                    yield return new LLMChatStreamUpdate { Usage = usage };

                yield break;
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
        var promptTokens = root.TryGetProperty("prompt_eval_count", out var prompt)
            ? prompt.GetInt32()
            : (int?)null;
        var completionTokens = root.TryGetProperty("eval_count", out var completion)
            ? completion.GetInt32()
            : (int?)null;

        if (!promptTokens.HasValue && !completionTokens.HasValue)
            return null;

        return new LLMTokenUsageStats
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = (promptTokens ?? 0) + (completionTokens ?? 0)
        };
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { model = _embeddingModel, input = text });
        var response = await CreateHttpClient().PostAsync("api/embed",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var arr = doc.RootElement.GetProperty("embeddings")[0];
        return arr.EnumerateArray().Select(e => e.GetSingle()).ToArray();
    }
}
