namespace AChat.Core.Models;

public class User
{
    public int Id { get; set; }

    /// <summary>Unique login name.</summary>
    public string Username { get; set; } = "";

    /// <summary>Display name used across the UI. Defaults to <see cref="Username"/>.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Argon2id hash; never the plaintext.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>System admin flag (bootstrap admin, §7.2).</summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Whether the account can sign in (§7.4). A disabled account is rejected at
    /// login and cannot start new work; existing data is retained, so re-enabling
    /// restores the relationship intact. Default true.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// IANA timezone name (e.g. "Europe/Moscow") used to render the current time in
    /// prompts and relative ages (§5.9). Null until the user sets one.
    /// </summary>
    public string? Timezone { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}
