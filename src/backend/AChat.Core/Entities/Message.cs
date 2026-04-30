using Pgvector;

namespace AChat.Core.Entities;

public class Message
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public Vector? Embedding { get; set; }
    public MessageSource Source { get; set; }
    public DateTime CreatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
    public User User { get; set; } = null!;
    public BotConversation Conversation { get; set; } = null!;
}
