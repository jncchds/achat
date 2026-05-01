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

    public async Task<string> GenerateChatAsync(LLMChatRequest request, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in StreamChatAsync(request, ct))
            sb.Append(chunk);
        return sb.ToString();
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        LLMChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new List<object>
        {
            new { role = "system", content = request.SystemPrompt }
        };
        messages.AddRange(request.Messages.Select(m => new { role = m.Role, content = m.Content }));

        var body = JsonSerializer.Serialize(new
        {
            model = _model,
            messages,
            stream = true,
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
            var delta = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("delta");

            if (delta.TryGetProperty("content", out var content))
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    yield return text;
            }
        }
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
