using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Core.Services;

namespace AChat.Infrastructure.LLM;

public class LLMProviderFactory : ILLMProviderFactory
{
    private readonly IEncryptionService _encryption;
    private readonly IHttpClientFactory _httpClientFactory;

    public LLMProviderFactory(IEncryptionService encryption, IHttpClientFactory httpClientFactory)
    {
        _encryption = encryption;
        _httpClientFactory = httpClientFactory;
    }

    public ILLMChatProvider GetChatProvider(LLMProviderPreset preset)
    {
        return preset.Provider switch
        {
            LLMProvider.Ollama => new OllamaProvider(
                _httpClientFactory,
                preset.BaseUrl ?? "http://localhost:11434",
                preset.ModelName,
                preset.EmbeddingModel),

            LLMProvider.OpenAI => new OpenAIProvider(
                _httpClientFactory,
                DecryptKey(preset),
                preset.ModelName,
                preset.EmbeddingModel,
                preset.BaseUrl ?? "https://api.openai.com/v1/"),

            LLMProvider.GoogleAIStudio => new GoogleAIStudioProvider(
                _httpClientFactory,
                DecryptKey(preset),
                preset.ModelName,
                preset.EmbeddingModel),

            _ => throw new NotSupportedException($"Provider {preset.Provider} is not supported.")
        };
    }

    public ILLMEmbeddingProvider GetEmbeddingProvider(LLMProviderPreset preset)
    {
        return preset.Provider switch
        {
            LLMProvider.Ollama => new OllamaProvider(
                _httpClientFactory,
                preset.BaseUrl ?? "http://localhost:11434",
                preset.ModelName,
                preset.EmbeddingModel),

            LLMProvider.OpenAI => new OpenAIProvider(
                _httpClientFactory,
                DecryptKey(preset),
                preset.ModelName,
                preset.EmbeddingModel,
                preset.BaseUrl ?? "https://api.openai.com/v1/"),

            LLMProvider.GoogleAIStudio => new GoogleAIStudioProvider(
                _httpClientFactory,
                DecryptKey(preset),
                preset.ModelName,
                preset.EmbeddingModel),

            _ => throw new NotSupportedException($"Provider {preset.Provider} is not supported.")
        };
    }

    private string DecryptKey(LLMProviderPreset preset)
    {
        if (string.IsNullOrEmpty(preset.EncryptedApiKey))
            throw new InvalidOperationException(
                $"Preset '{preset.Name}' requires an API key but none is stored.");
        return _encryption.Decrypt(preset.EncryptedApiKey);
    }
}
