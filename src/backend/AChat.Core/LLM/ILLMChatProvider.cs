namespace AChat.Core.LLM;

public interface ILLMChatProvider
{
    Task<LLMChatCompletionResult> GenerateChatCompletionAsync(LLMChatRequest request, CancellationToken ct = default);
    IAsyncEnumerable<LLMChatStreamUpdate> StreamChatCompletionAsync(LLMChatRequest request, CancellationToken ct = default);
    Task<string> GenerateChatAsync(LLMChatRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamChatAsync(LLMChatRequest request, CancellationToken ct = default);
}
