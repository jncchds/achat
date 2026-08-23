using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Llm;
using System.Net;
using System.Text;
using Xunit;

namespace AChat.Tests;

/// <summary>
/// Verifies the HTTP request shape each provider issues (path, method, auth header)
/// against a local listener that captures the request and returns a canned body.
/// All four families are OpenAI-shaped, so each must hit <c>/chat/completions</c>,
/// <c>/embeddings</c>, and <c>/models</c> with a Bearer key — except Ollama, which
/// sends no Authorization header (no key).
/// </summary>
public sealed class ProviderHttpTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _base;
    private Task? _serve;
    private List<(string Method, string Path, string? Auth)> _captured;

    public ProviderHttpTests()
    {
        // Bind to a random free port on loopback.
        var port = GetFreePort();
        _base = $"http://127.0.0.1:{port}/v1";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _captured = new List<(string, string, string?)>();
        _serve = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { /* best effort */ }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            var req = ctx.Request;
            var auth = req.Headers["Authorization"];
            lock (_captured)
                _captured.Add((req.HttpMethod, req.Url!.AbsolutePath, auth));

            var path = req.Url!.AbsolutePath;
            string body =
                path.EndsWith("/models", StringComparison.Ordinal)
                    ? """{"data":[{"id":"m1"},{"id":"m2","name":"Two"}]}"""
                    : path.EndsWith("/chat/completions", StringComparison.Ordinal)
                        ? """{"choices":[{"message":{"content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}"""
                        : path.EndsWith("/embeddings", StringComparison.Ordinal)
                            ? """{"data":[{"index":0,"embedding":[0.1,0.2,0.3]}]}"""
                            : "{}";

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static LlmPresetConfig Cfg(LlmProvider p, string endpoint, string? key = "sekrit",
        int maxRetries = 0, int baseDelayMs = 10) => new()
    {
        Provider = p,
        Endpoint = endpoint,
        ApiKey = key,
        GenerationModel = "m1",
        TimeoutMs = 5000,
        MaxRetries = maxRetries,
        RetryBaseDelayMs = baseDelayMs,
    };

    private (string Method, string Path, string? Auth) Last()
    {
        lock (_captured)
            return _captured[^1];
    }

    [Theory]
    [InlineData(LlmProvider.OpenAICompatible, typeof(OpenAICompatibleProvider))]
    [InlineData(LlmProvider.OpenAI, typeof(OpenAIProvider))]
    [InlineData(LlmProvider.Ollama, typeof(OllamaProvider))]
    [InlineData(LlmProvider.GoogleAIStudio, typeof(GoogleAIStudioProvider))]
    public async Task ListModels_hits_models_endpoint(LlmProvider family, Type type)
    {
        var provider = (ILlmProvider)Activator.CreateInstance(type, Cfg(family, _base))!;
        var models = await provider.ListModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.Contains("m1", models.Select(m => m.Id));
        var (method, path, auth) = Last();
        Assert.Equal("GET", method);
        Assert.EndsWith("/models", path);
    }

    [Theory]
    [InlineData(LlmProvider.OpenAICompatible, true)]
    [InlineData(LlmProvider.OpenAI, true)]
    [InlineData(LlmProvider.Ollama, false)]   // no key → no Authorization header
    [InlineData(LlmProvider.GoogleAIStudio, true)]
    public async Task Chat_posts_to_completions_with_expected_auth(LlmProvider family, bool expectAuth)
    {
        var factory = new LlmProviderFactory();
        var provider = factory.Create(Cfg(family, _base, expectAuth ? "sekrit" : null));

        var completion = await provider.ChatAsync(new LlmChatRequest
        {
            Model = "m1",
            Messages = [new LlmChatMessage(MessageRole.User, "hi")],
        });

        Assert.Equal("hello", completion.Content);
        var (method, path, auth) = Last();
        Assert.Equal("POST", method);
        Assert.EndsWith("/chat/completions", path);
        if (expectAuth)
            Assert.Equal("Bearer sekrit", auth);
        else
            Assert.Null(auth);
    }

    [Fact]
    public async Task Embed_posts_to_embeddings_endpoint()
    {
        var factory = new LlmProviderFactory();
        var provider = factory.Create(Cfg(LlmProvider.OpenAI, _base));

        var vecs = await provider.EmbedAsync("emb", ["a", "b"]);
        var (method, path, auth) = Last();
        Assert.Equal("POST", method);
        Assert.EndsWith("/embeddings", path);
        Assert.Equal("Bearer sekrit", auth);
        // The listener returns one vector; ordering/shape is preserved.
        Assert.Single(vecs);
        Assert.Equal(3, vecs[0].Length);
    }

    [Fact]
    public async Task Transient_500_is_retried_then_succeeds()
    {
        // First request → 500 (transient), second → 200. With MaxRetries ≥ 1 the
        // provider should recover and return the second response (§4.5).
        var port = GetFreePort();
        var flaky = new HttpListener();
        flaky.Prefixes.Add($"http://127.0.0.1:{port}/");
        flaky.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    var ctx = await flaky.GetContextAsync();
                    string body = i == 0
                        ? """{"error":"transient"}"""
                        : """{"choices":[{"message":{"content":"recovered"},"finish_reason":"stop"}]}""";
                    ctx.Response.StatusCode = i == 0 ? 500 : 200;
                    var b = Encoding.UTF8.GetBytes(body);
                    ctx.Response.ContentLength64 = b.Length;
                    await ctx.Response.OutputStream.WriteAsync(b);
                    ctx.Response.Close();
                }
            }
            catch { }
            finally { flaky.Stop(); }
        });

        var provider = new OpenAIProvider(Cfg(LlmProvider.OpenAI, $"http://127.0.0.1:{port}/v1", maxRetries: 2, baseDelayMs: 10));
        var completion = await provider.ChatAsync(new LlmChatRequest
        {
            Model = "m1",
            Messages = [new LlmChatMessage(MessageRole.User, "hi")],
        });
        Assert.Equal("recovered", completion.Content);
    }

    [Fact]
    public async Task Error_status_surfaces_as_LlmProviderException_with_status_and_body()
    {
        // Point at a port with no listener → connection refused, not a clean 4xx.
        // Instead, verify the provider propagates a non-success from a real 500 by
        // using a listener that returns 500 for /chat/completions.
        var port = GetFreePort();
        var bad = new HttpListener();
        bad.Prefixes.Add($"http://127.0.0.1:{port}/");
        bad.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await bad.GetContextAsync();
                ctx.Response.StatusCode = 500;
                var bytes = Encoding.UTF8.GetBytes("""{"error":"boom"}""");
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch { }
            finally { bad.Stop(); }
        });

        var provider = new OpenAIProvider(Cfg(LlmProvider.OpenAI, $"http://127.0.0.1:{port}/v1"));
        var ex = await Assert.ThrowsAsync<LlmProviderException>(() => provider.ChatAsync(new LlmChatRequest
        {
            Model = "m1",
            Messages = [new LlmChatMessage(MessageRole.User, "hi")],
        }));
        Assert.Equal(500, ex.HttpStatus);
        Assert.Contains("boom", ex.ErrorPayload);
    }
}
