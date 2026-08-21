using AChat.Core.Interfaces;
using AChat.Core.Models;

namespace AChat.Infrastructure.Llm;

public sealed class OpenAICompatibleProviderStrategy : ILlmProviderStrategy
{
    public LlmProvider Provider => LlmProvider.OpenAICompatible;
    public ILlmProvider Create(LlmPresetConfig config) => new OpenAICompatibleProvider(config);
}

/// <summary>
/// Maps provider family → strategy (§4.1). v1 registers OpenAICompatible only; the
/// other families arrive in Phase 7 of the delivery plan (§12a).
/// </summary>
public sealed class LlmProviderFactory : ILlmProviderFactory
{
    private static readonly IReadOnlyDictionary<LlmProvider, ILlmProviderStrategy> Strategies =
        new ILlmProviderStrategy[]
        {
            new OpenAICompatibleProviderStrategy(),
        }.ToDictionary(s => s.Provider);

    public bool IsSupported(LlmProvider provider) => Strategies.ContainsKey(provider);

    public ILlmProvider Create(LlmPresetConfig config) =>
        Strategies.TryGetValue(config.Provider, out var strategy)
            ? strategy.Create(config)
            : throw new NotSupportedException(
                $"Provider '{config.Provider}' is not available in this build yet. v1 ships OpenAICompatible; " +
                "other providers land in Phase 7 (see docs/design.md §12a).");
}
