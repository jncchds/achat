namespace AChat.Core.Entities;

public class BotConversationState
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public Guid CurrentConversationId { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
    public User User { get; set; } = null!;
    public BotConversation CurrentConversation { get; set; } = null!;
}