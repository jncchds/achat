using AChat.Core.Models;

namespace AChat.Core.Interfaces;

/// <summary>
/// Everything a provider needs to know, resolved from an <see cref="LlmPreset"/>.
/// The factory hands this to strategies so they never touch the domain entity (and so
/// a blank utility/embedding model already falls back to the generation model, §3).
/// </summary>
public sealed record LlmPresetConfig
{
    public required LlmProvider Provider { get; init; }
    public required string Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public required string GenerationModel { get; init; }
    public string? UtilityModel { get; init; }
    public string? EmbeddingModel { get; init; }
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int TimeoutMs { get; init; } = 120_000;
}

/// <summary>A strategy for one provider family (§4.1).</summary>
public interface ILlmProviderStrategy
{
    LlmProvider Provider { get; }
    ILlmProvider Create(LlmPresetConfig config);
}

/// <summary>
/// Maps provider family → strategy. Bots never talk to a provider SDK directly (§4.1).
/// </summary>
public interface ILlmProviderFactory
{
    ILlmProvider Create(LlmPresetConfig config);

    /// <summary>Whether a strategy is registered for this family.</summary>
    bool IsSupported(LlmProvider provider);
}
