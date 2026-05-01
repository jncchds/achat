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
        var contents = request.Messages.Select(m => new
        {
            role = m.Role == "assistant" ? "model" : "user",
            parts = new[] { new { text = m.Content } }
        }).ToList();

        var body = JsonSerializer.Serialize(new
        {
            system_instruction = new { parts = new[] { new { text = request.SystemPrompt } } },
            contents
        });

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

            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            if (!string.IsNullOrEmpty(text))
                yield return text;
        }
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
