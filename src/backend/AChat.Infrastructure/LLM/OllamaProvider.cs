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
            options = new
            {
                temperature = request.Temperature,
                num_predict = request.MaxTokens
            }
        });

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
            var content = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (!string.IsNullOrEmpty(content))
                yield return content;

            if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
                yield break;
        }
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
