using System.Net.Http.Headers;
using System.Text.Json;
using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;

namespace AChat.Infrastructure.Llm;

/// <summary>
/// Shared implementation for every OpenAI-shaped endpoint (§4.1): plain HTTP +
/// System.Text.Json, no SDK dependency. The four provider families differ only in
/// their default base URL (and whether a key is expected — a blank key simply omits
/// the Authorization header):
///
///   OpenAICompatible → https://api.openai.com/v1            (any /v1 endpoint)
///   OpenAI           → https://api.openai.com/v1
///   Ollama           → http://host.docker.internal:11434/v1 (Ollama's OpenAI API, no key)
///   GoogleAIStudio   → https://generativelanguage.googleapis.com/v1beta/openai (Google's OpenAI API)
///
/// A configured <c>Endpoint</c> always wins over the default, so a user can point any
/// of these families at a custom gateway.
/// </summary>
public abstract class OpenAiShapedProvider(LlmPresetConfig config) : ILlmProvider
{
    protected LlmPresetConfig Config { get; } = config;

    /// <summary>Which provider family this instance reports.</summary>
    public abstract LlmProvider Provider { get; }

    /// <summary>Used when the preset's endpoint is blank.</summary>
    protected virtual string DefaultBaseUrl => "https://api.openai.com/v1";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private Uri BaseUrl => new(string.IsNullOrWhiteSpace(Config.Endpoint)
        ? DefaultBaseUrl
        : Config.Endpoint.TrimEnd('/'));

    private HttpClient Http => new()
    {
        Timeout = TimeSpan.FromMilliseconds(Config.TimeoutMs > 0 ? Config.TimeoutMs : 120_000),
    };

    private void ApplyAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(Config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
    }

    // ── Non-streaming chat ──────────────────────────────────────────────────────
    public async Task<LlmCompletion> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
    {
        using var http = Http;
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions");
        ApplyAuth(req);
        req.Content = new StringContent(JsonSerializer.Serialize(
            BuildChatBody(request, stream: false), JsonOpts), System.Text.Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new LlmProviderException($"Provider returned {(int)resp.StatusCode}", (int)resp.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        return new LlmCompletion
        {
            Content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "",
            Reasoning = message.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            FinishReason = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String ? fr.GetString() : null,
            PromptTokens = Usage(root, "prompt_tokens"),
            CompletionTokens = Usage(root, "completion_tokens"),
            TotalTokens = Usage(root, "total_tokens"),
        };
    }

    // ── Streaming chat (SSE) ────────────────────────────────────────────────────
    public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
        LlmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var http = Http;
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions")
        {
            Headers = { Accept = { new MediaTypeWithQualityHeaderValue("text/event-stream") } },
        };
        ApplyAuth(req);
        req.Content = new StringContent(JsonSerializer.Serialize(
            BuildChatBody(request, stream: true), JsonOpts), System.Text.Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new LlmProviderException($"Provider returned {(int)resp.StatusCode}", (int)resp.StatusCode, body);
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") yield break;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err))
                throw new LlmProviderException("Provider error in stream", null, err.GetRawText());

            string? content = null, reasoning = null, finish = null;
            int? p = null, c = null, t = null;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
                    content = dc.GetString();
                if (delta.TryGetProperty("reasoning_content", out var dr) && dr.ValueKind == JsonValueKind.String)
                    reasoning = dr.GetString();
                if (choices[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finish = fr.GetString();
            }
            if (root.TryGetProperty("usage", out var usage))
            {
                p = Usage(root, "prompt_tokens");
                c = Usage(root, "completion_tokens");
                t = Usage(root, "total_tokens");
            }

            if (content is not null || reasoning is not null || finish is not null || p.HasValue)
                yield return new LlmStreamChunk
                {
                    Content = content,
                    Reasoning = reasoning,
                    FinishReason = finish,
                    PromptTokens = p,
                    CompletionTokens = c,
                    TotalTokens = t,
                };
        }
    }

    // ── Embeddings ──────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        using var http = Http;
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/embeddings");
        ApplyAuth(req);
        req.Content = new StringContent(JsonSerializer.Serialize(new { model, input = texts.ToArray() }, JsonOpts),
            System.Text.Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new LlmProviderException($"Provider returned {(int)resp.StatusCode}", (int)resp.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .OrderBy(e => e.GetProperty("index").GetInt32())
            .Select(e => e.GetProperty("embedding").GetSingleFloatArray())
            .ToList();
    }

    // ── Model catalogue ─────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        using var http = Http;
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models");
        ApplyAuth(req);

        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new LlmProviderException($"Provider returned {(int)resp.StatusCode}", (int)resp.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var list = new List<LlmModelInfo>();
        foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = m.GetProperty("id").GetString();
            if (id is null) continue;
            list.Add(new LlmModelInfo(id,
                m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null));
        }
        return list.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────
    private static object BuildChatBody(LlmChatRequest request, bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.Select(m => new { role = m.Role switch
            {
                MessageRole.System => "system",
                MessageRole.Assistant => "assistant",
                _ => "user",
            }, content = m.Content }).ToList(),
            ["stream"] = stream,
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
        };
        if (stream) body["stream_options"] = new { include_usage = true };
        if (!string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            using var schema = JsonDocument.Parse(request.JsonSchema);
            body["response_format"] = new Dictionary<string, object?>
            {
                ["type"] = "json_schema",
                ["json_schema"] = new Dictionary<string, object?>
                {
                    ["name"] = "structured_output",
                    ["schema"] = schema.RootElement.Clone(),
                },
            };
        }
        return body;
    }

    private static int? Usage(JsonElement root, string field) =>
        root.TryGetProperty("usage", out var usage) && usage.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;
}

/// <summary>Any OpenAI-compatible /v1 endpoint (llama.cpp, vLLM, LM Studio, OpenRouter, …).</summary>
public sealed class OpenAICompatibleProvider(LlmPresetConfig config) : OpenAiShapedProvider(config)
{
    public override LlmProvider Provider => LlmProvider.OpenAICompatible;
}

/// <summary>OpenAI's hosted API — the canonical OpenAI-compatible endpoint.</summary>
public sealed class OpenAIProvider(LlmPresetConfig config) : OpenAiShapedProvider(config)
{
    public override LlmProvider Provider => LlmProvider.OpenAI;
}

/// <summary>
/// Local Ollama, via its OpenAI-compatible API (<c>{endpoint}/v1</c>). No API key.
/// The default endpoint reaches the Docker host, where Ollama typically runs.
/// </summary>
public sealed class OllamaProvider(LlmPresetConfig config) : OpenAiShapedProvider(config)
{
    public override LlmProvider Provider => LlmProvider.Ollama;
    protected override string DefaultBaseUrl => "http://host.docker.internal:11434/v1";
}

/// <summary>
/// Google AI Studio (Gemini), via its OpenAI-compatible endpoint
/// (<c>https://generativelanguage.googleapis.com/v1beta/openai</c>). Requires a key.
/// </summary>
public sealed class GoogleAIStudioProvider(LlmPresetConfig config) : OpenAiShapedProvider(config)
{
    public override LlmProvider Provider => LlmProvider.GoogleAIStudio;
    protected override string DefaultBaseUrl => "https://generativelanguage.googleapis.com/v1beta/openai";
}

internal static class JsonElementExtensions
{
    public static float[] GetSingleFloatArray(this JsonElement e)
    {
        var list = new List<float>(e.GetArrayLength());
        foreach (var v in e.EnumerateArray()) list.Add(v.GetSingle());
        return list.ToArray();
    }
}
