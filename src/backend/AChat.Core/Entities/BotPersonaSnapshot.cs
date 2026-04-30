namespace AChat.Core.Entities;

public class BotPersonaSnapshot
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public string SnapshotText { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
}
