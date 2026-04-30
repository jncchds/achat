using Pgvector;

namespace AChat.Core.Entities;

public class BotMemorySummary
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public Guid ConversationId { get; set; }
    public string SummaryText { get; set; } = string.Empty;
    public Vector? Embedding { get; set; }
    public Guid MessageRangeStart { get; set; }
    public Guid MessageRangeEnd { get; set; }
    public DateTime CreatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
    public User User { get; set; } = null!;
    public BotConversation Conversation { get; set; } = null!;
}
