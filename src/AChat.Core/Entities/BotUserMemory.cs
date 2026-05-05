namespace AChat.Core.Entities;

public class BotUserMemory
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public Guid UserId { get; set; }
    public List<string> Facts { get; set; } = [];
    public DateTime UpdatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
    public User User { get; set; } = null!;
}
