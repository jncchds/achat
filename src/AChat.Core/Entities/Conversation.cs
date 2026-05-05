namespace AChat.Core.Entities;

public class Conversation
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
    public User User { get; set; } = null!;
    public ICollection<Message> Messages { get; set; } = [];
}
