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
    /// Watermark for the memory-extraction worker (§5.3): messages with Id above
    /// this have not been read by extraction yet. The worker advances it after a
    /// successful pass; a failed pass leaves it untouched so the same tail retries.
    /// </summary>
    public int ExtractionUpToMessageId { get; set; }

    public Bot Bot { get; set; } = null!;
    public User User { get; set; } = null!;
}
