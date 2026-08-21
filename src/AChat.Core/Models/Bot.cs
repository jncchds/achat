namespace AChat.Core.Models;

/// <summary>
/// A virtual friend (§2.4, §2.5). Owned by its creator, who is the bot admin.
/// Uses exactly one <see cref="LlmPreset"/>, which must belong to the same owner (§2.2).
/// </summary>
public class Bot
{
    public int Id { get; set; }

    /// <summary>Creator — the sole bot admin (§2.5).</summary>
    public int OwnerId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Optional avatar URL (or data URI).</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Core personality layer — the anchor identity, written at creation and changed
    /// only by explicit edit (§2.4). Freeform prose.
    /// </summary>
    public string CorePersona { get; set; } = "";

    /// <summary>Short speech-style hint; steers the model without being a full directive.</summary>
    public string? SpeechStyle { get; set; }

    /// <summary>Optional greeting — a message, not a prompt fragment (§2.4).</summary>
    public string? Greeting { get; set; }

    /// <summary>
    /// Invite code — a GUID, assigned once at creation and stable thereafter (§2.5).
    /// It is a request token, not a grant: presenting it only creates an
    /// <see cref="AccessRequest"/>.
    /// </summary>
    public string InviteCode { get; set; } = "";

    public int PresetId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Owner { get; set; } = null!;
    public LlmPreset Preset { get; set; } = null!;
}
