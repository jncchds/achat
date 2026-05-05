using AChat.Core.Entities;

namespace AChat.Core.Interfaces.Services;

public interface IChatService
{
    IAsyncEnumerable<string> StreamAsync(
        Guid conversationId,
        Guid userId,
        string userMessage,
        CancellationToken ct = default);

    Task<string> GenerateAsync(
        Guid botId,
        Guid userId,
        IReadOnlyList<(string Role, string Content)> messages,
        CancellationToken ct = default);
}
