namespace AChat.Core.LLM;

public interface ILLMEmbeddingProvider
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}
