using AChat.Core.DTOs.Conversations;

namespace AChat.Core.Interfaces.Services;

public interface IConversationNotifier
{
    void Notify(Guid conversationId, MessageDto message);
    IAsyncEnumerable<MessageDto> SubscribeAsync(Guid conversationId, CancellationToken ct);
}
