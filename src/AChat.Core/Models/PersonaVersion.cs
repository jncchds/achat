namespace AChat.Core.Models;

/// <summary>
/// Who produced a version of the evolved-traits layer (§2.4).
/// </summary>
public enum PersonaVersionSource
{
    /// <summary>The empty baseline created with the bot.</summary>
    Initial = 0,

    /// <summary>A rewrite by the background evolution worker.</summary>
    Evolution = 1,

    /// <summary>A direct edit by the bot admin — the next evolution pass treats it as the new baseline.</summary>
    UserEdited = 2,

    /// <summary>A revert to an older version, by the bot admin.</summary>
    Revert = 3,
}

/// <summary>
/// One version of a bot's evolved-traits layer (§2.4). Versioning is required,
/// not optional: every rewrite is stored with a timestamp and the interaction
/// that produced it, and the admin can read the history and revert.
///
/// The latest version (by Id) is the current one. Older versions are never
/// mutated — a human edit or a revert appends a new version.
/// </summary>
public class PersonaVersion
{
    public int Id { get; set; }

    public int BotId { get; set; }

    /// <summary>The evolved-traits document. Empty for the Initial baseline.</summary>
    public string Content { get; set; } = "";

    public PersonaVersionSource Source { get; set; }

    /// <summary>
    /// The LlmInteraction that produced this version (Evolution versions only) —
    /// the audit trail from §4.4.
    /// </summary>
    public int? InteractionId { get; set; }

    public DateTime CreatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
}
