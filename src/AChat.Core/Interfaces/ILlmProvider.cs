using AChat.Core.Models;

namespace AChat.Core.Interfaces;

/// <summary>A single message in a provider request.</summary>
public record LlmChatMessage(MessageRole Role, string Content);

/// <summary>
/// A provider-agnostic chat request. All provider-specific shaping (role names,
/// parameter names, stream flags) happens behind <see cref="ILlmProvider"/> (§4.1).
/// </summary>
public sealed record LlmChatRequest
{
    public required IReadOnlyList<LlmChatMessage> Messages { get; init; }
    public required string Model { get; init; }
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }

    /// <summary>When set, the provider is asked for machine-read JSON conforming to this schema.</summary>
    public string? JsonSchema { get; init; }
}

/// <summary>The result of a non-streaming completion.</summary>
public sealed record LlmCompletion
{
    public required string Content { get; init; }
    public string? Reasoning { get; init; }
    public string? FinishReason { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
}

/// <summary>
/// One streamed chunk. Exactly one of <see cref="Content"/> / <see cref="Reasoning"/>
/// is normally non-empty; both may be present on a final usage chunk.
/// </summary>
public sealed record LlmStreamChunk
{
    public string? Content { get; init; }
    public string? Reasoning { get; init; }
    public string? FinishReason { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
}

/// <summary>A model from the provider's catalogue (for the "Load models" button, §3).</summary>
public sealed record LlmModelInfo(string Id, string? Name = null);

/// <summary>
/// One implementation per provider type. All provider-specific request/response
/// shaping, error mapping, and streaming-event normalisation lives behind it (§4.1).
/// Implementations are created per <see cref="LlmPreset"/> and are safe to use from
/// any context; the factory returns a fresh instance per call.
/// </summary>
public interface ILlmProvider
{
    LlmProvider Provider { get; }

    /// <summary>Non-streaming completion (§4.1).</summary>
    Task<LlmCompletion> ChatAsync(LlmChatRequest request, CancellationToken ct = default);

    /// <summary>Streaming completion; tokens as they arrive (§4.2).</summary>
    IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(LlmChatRequest request, CancellationToken ct = default);

    /// <summary>Text → vectors (§4.1). <paramref name="model"/> selects the embedding model.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default);

    /// <summary>Model catalogue for the "Load models" button (§3, §4.1).</summary>
    Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default);
}
