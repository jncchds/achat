using AChat.Core.DTOs.Conversations;

namespace AChat.Core.Interfaces.Services;

public interface IConversationService
{
    Task<IReadOnlyList<ConversationDto>> GetConversationsAsync(Guid botId, Guid userId, CancellationToken ct = default);
    Task<ConversationDto?> GetConversationAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task<ConversationDto> CreateConversationAsync(Guid botId, Guid userId, string? title, CancellationToken ct = default);
    Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid conversationId, Guid userId, CancellationToken ct = default);
    Task<bool> DeleteConversationAsync(Guid id, Guid userId, CancellationToken ct = default);
}
