namespace AChat.Core.Entities;

public class BotAccessRequest
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public AccessSubjectType SubjectType { get; set; }
    public string SubjectId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public DateTime RequestedAt { get; set; }
    public AccessRequestStatus Status { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }

    public Bot Bot { get; set; } = null!;
    public User? ResolvedByUser { get; set; }
}
