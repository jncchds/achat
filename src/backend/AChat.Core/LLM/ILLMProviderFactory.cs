using AChat.Core.Entities;

namespace AChat.Core.LLM;

public interface ILLMProviderFactory
{
    ILLMChatProvider GetChatProvider(LLMProviderPreset preset);
    ILLMEmbeddingProvider GetEmbeddingProvider(LLMProviderPreset preset);
}
