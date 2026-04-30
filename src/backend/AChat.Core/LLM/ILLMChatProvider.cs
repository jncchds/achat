namespace AChat.Core.LLM;

public interface ILLMChatProvider
{
    Task<string> GenerateChatAsync(LLMChatRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamChatAsync(LLMChatRequest request, CancellationToken ct = default);
}
