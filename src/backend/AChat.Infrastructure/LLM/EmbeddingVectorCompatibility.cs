using Pgvector;

namespace AChat.Infrastructure.LLM;

public static class EmbeddingVectorCompatibility
{
    public const int ExpectedDimensions = 1536;

    public static bool IsCompatible(float[]? embedding) =>
        embedding is not null && embedding.Length == ExpectedDimensions;

    public static Vector? ToVectorOrNull(float[]? embedding) =>
        IsCompatible(embedding) ? new Vector(embedding!) : null;
}