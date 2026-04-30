namespace AChat.Core.Entities;

public class BotAccessList
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public AccessSubjectType SubjectType { get; set; }
    public string SubjectId { get; set; } = string.Empty;
    public AccessStatus Status { get; set; }
    public DateTime AddedAt { get; set; }

    public Bot Bot { get; set; } = null!;
}
