namespace AChat.Core.Models;

/// <summary>
/// A join entity granting a user access to a shared bot (§2.5). The creator holds an
/// Admin row automatically; invitee approval creates a User row.
/// </summary>
public class BotAccess
{
    public int Id { get; set; }
    public int BotId { get; set; }
    public int UserId { get; set; }

    public AccessRole Role { get; set; } = AccessRole.User;

    public DateTime GrantedAt { get; set; }

    /// <summary>The invite code that was redeemed to produce this grant (provenance, §2.5).</summary>
    public string? InviteCode { get; set; }

    public Bot Bot { get; set; } = null!;
    public User User { get; set; } = null!;
}
