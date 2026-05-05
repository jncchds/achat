using AChat.Core.Entities;
using AChat.Core.Enums;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Google;

namespace AChat.Infrastructure.LLM;

public static class SemanticKernelFactory
{
    public static Kernel Build(LlmPreset preset)
    {
        var builder = Kernel.CreateBuilder();

        switch (preset.ProviderType)
        {
            case ProviderType.OpenAI:
                builder.AddOpenAIChatCompletion(
                    modelId: preset.GenerationModel,
                    apiKey: preset.ApiToken ?? string.Empty,
                    httpClient: null);
                break;

            case ProviderType.Ollama:
                builder.AddOpenAIChatCompletion(
                    modelId: preset.GenerationModel,
                    apiKey: "ollama",
                    endpoint: BuildOllamaEndpoint(preset.ProviderUrl));
                break;

            case ProviderType.GoogleAI:
#pragma warning disable SKEXP0070
                builder.AddGoogleAIGeminiChatCompletion(
                    modelId: preset.GenerationModel,
                    apiKey: preset.ApiToken ?? string.Empty);
#pragma warning restore SKEXP0070
                break;
        }

        return builder.Build();
    }

    public static Kernel BuildWithEmbedding(LlmPreset preset)
    {
        var builder = Kernel.CreateBuilder();

        switch (preset.ProviderType)
        {
            case ProviderType.OpenAI:
#pragma warning disable SKEXP0010
                builder.AddOpenAIEmbeddingGenerator(
                    modelId: preset.EmbeddingModel ?? "text-embedding-3-small",
                    apiKey: preset.ApiToken ?? string.Empty);
#pragma warning restore SKEXP0010
                break;

            case ProviderType.Ollama:
#pragma warning disable SKEXP0010
                builder.AddOpenAIEmbeddingGenerator(
                    modelId: preset.EmbeddingModel ?? preset.GenerationModel,
                    apiKey: "ollama",
                    httpClient: new HttpClient { BaseAddress = BuildOllamaEndpoint(preset.ProviderUrl) });
#pragma warning restore SKEXP0010
                break;

            case ProviderType.GoogleAI:
#pragma warning disable SKEXP0070
                builder.AddGoogleAIEmbeddingGenerator(
                    modelId: preset.EmbeddingModel ?? "models/text-embedding-004",
                    apiKey: preset.ApiToken ?? string.Empty);
#pragma warning restore SKEXP0070
                break;
        }

        return builder.Build();
    }

    /// <summary>
    /// Normalizes the Ollama base URL to always end with /v1/.
    /// The OpenAI .NET SDK appends "chat/completions" (no /v1 prefix),
    /// so the endpoint must already include /v1.
    /// </summary>
    private static Uri BuildOllamaEndpoint(string providerUrl)
    {
        var url = providerUrl.TrimEnd('/');
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            url = url[..^3];
        return new Uri(url + "/v1/");
    }
}
