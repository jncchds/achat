using AChat.Core.Enums;
using Pgvector;

namespace AChat.Core.Entities;

public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public Vector? Embedding { get; set; }

    public Conversation Conversation { get; set; } = null!;
}
