using AChat.Core.Enums;

namespace AChat.Core.Entities;

public class BotAccessRequest
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public Guid RequesterId { get; set; }
    public AccessRequestStatus Status { get; set; } = AccessRequestStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
    public User Requester { get; set; } = null!;
}
