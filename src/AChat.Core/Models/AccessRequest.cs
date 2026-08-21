namespace AChat.Core.Models;

/// <summary>
/// A user entering a bot's invite code (§2.5). This is a request, not a grant —
/// approval by the bot admin creates the <see cref="BotAccess"/> row.
/// </summary>
public class AccessRequest
{
    public int Id { get; set; }
    public int BotId { get; set; }
    public int RequesterId { get; set; }

    public AccessRequestStatus Status { get; set; } = AccessRequestStatus.Pending;

    /// <summary>The code that was presented (provenance, §2.5).</summary>
    public string? InviteCode { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? DecidedAt { get; set; }
    public int? DecidedById { get; set; }

    public Bot Bot { get; set; } = null!;
    public User Requester { get; set; } = null!;
}
