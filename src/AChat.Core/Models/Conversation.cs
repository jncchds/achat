namespace AChat.Core.Models;

/// <summary>
/// One continuous timeline per (BotId, UserId) (§2.3). No threads, no separate
/// conversations — talking to a friend resumes where you left off.
/// </summary>
public class Conversation
{
    public int Id { get; set; }
    public int BotId { get; set; }
    public int UserId { get; set; }

    /// <summary>When the first message was exchanged.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Timestamp of the most recent message in this timeline. Drives the
    /// "last N bots by most recent interaction" sidebar list (§9.1a) and the
    /// time-aware gap markers (§5.9).
    /// </summary>
    public DateTime? LastMessageAt { get; set; }

    /// <summary>
    /// The bot's current mood toward this person (§4.7) — conversation state, set by the
    /// bot in its turn envelope. A slow-moving undercurrent: changes are damped by
    /// <c>Turn:MoodMinTurns</c> / <c>Turn:MoodMinMinutes</c>. Null = none yet.
    /// </summary>
    public string? Mood { get; set; }

    /// <summary>When <see cref="Mood"/> last changed.</summary>
    public DateTime? MoodChangedAt { get; set; }

    /// <summary>Id of the message that was newest when the mood last changed (turn counting).</summary>
    public int MoodChangedAtMessageId { get; set; }

    public Bot Bot { get; set; } = null!;
    public User User { get; set; } = null!;
}
