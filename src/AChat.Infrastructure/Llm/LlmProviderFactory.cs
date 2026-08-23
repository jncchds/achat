using AChat.Core.Interfaces;
using AChat.Core.Models;

namespace AChat.Infrastructure.Llm;

public sealed class OpenAICompatibleProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.OpenAICompatible;
    public ILlmProvider Create(LlmPresetConfig config) => new OpenAICompatibleProvider(config);
}

public sealed class OpenAIProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.OpenAI;
    public ILlmProvider Create(LlmPresetConfig config) => new OpenAIProvider(config);
}

public sealed class OllamaProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.Ollama;
    public ILlmProvider Create(LlmPresetConfig config) => new OllamaProvider(config);
}

public sealed class GoogleAIStudioProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.GoogleAIStudio;
    public ILlmProvider Create(LlmPresetConfig config) => new GoogleAIStudioProvider(config);
}

/// <summary>
/// Maps provider family → strategy (§4.1). All four families in the
/// <see cref="LlmProvider"/> enum are registered: each is an OpenAI-shaped endpoint,
/// so they share the <see cref="OpenAiShapedProvider"/> implementation and differ only
/// in their default base URL and whether a key is expected.
/// </summary>
public sealed class LlmProviderFactory : ILlmProviderFactory
{
    private static readonly IReadOnlyDictionary<LlmProvider, ILlmProviderStrategy> Strategies =
        new ILlmProviderStrategy[]
        {
            new OpenAICompatibleProviderStrategy(),
            new OpenAIProviderStrategy(),
            new OllamaProviderStrategy(),
            new GoogleAIStudioProviderStrategy(),
        }.ToDictionary(s => s.Provider);

    public bool IsSupported(LlmProvider provider) => Strategies.ContainsKey(provider);

    public ILlmProvider Create(LlmPresetConfig config) =>
        Strategies.TryGetValue(config.Provider, out var strategy)
            ? strategy.Create(config)
            : throw new NotSupportedException($"Provider '{config.Provider}' has no registered strategy.");
}
