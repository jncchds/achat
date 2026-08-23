namespace AChat.Core.Models;

/// <summary>
/// What kind of background pass a work item is for (§12b).
/// </summary>
public enum WorkKind
{
    Extraction = 0,
    Evolution = 1,
    Summary = 2,
}

/// <summary>
/// Lifecycle state of a work item (§12b). <see cref="Running"/> is a transient
/// in-memory state — it never needs to survive a crash, because a claim that dies
/// mid-flight is simply re-derivable: the watermark never advanced, so the next
/// claim re-executes the pass (at-least-once; the handlers are idempotent).
/// </summary>
public enum WorkStatus
{
    Pending = 0,
    Running = 1,

    /// <summary>
    /// Attempts exhausted — frozen until an admin retries or discards it. The
    /// producer never re-arms a <see cref="Dead"/> item by itself (§12b).
    /// </summary>
    Dead = 2,
}

/// <summary>
/// One unit of background work waiting for, or being given, an LLM pass (§12b).
/// The queue is a table in the same Postgres as everything else — durable retry
/// (backoff, attempt cap, dead-letter, visibility) without a messaging fabric.
///
/// Identity is (Kind, ConversationId): at most one live item per (kind,
/// conversation). Evolution targets the bot's creator conversation, which is
/// unique per bot (§2.3).
///
/// A row is deleted when its pass succeeds; it dead-letters when the attempts
/// are exhausted. It never accumulates history — the audit trail of what each
/// attempt did is the interaction log (§4.4).
/// </summary>
public class WorkItem
{
    public int Id { get; set; }

    public WorkKind Kind { get; set; }

    /// <summary>The target conversation (evolution: the creator's conversation).</summary>
    public int ConversationId { get; set; }

    public WorkStatus Status { get; set; } = WorkStatus.Pending;

    /// <summary>Failed attempts so far (incremented on each claim).</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Attempt cap, captured from config at creation so a config change doesn't
    /// alter the fate of items already in flight.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>When this item may next be claimed (backoff after a failure).</summary>
    public DateTime NextTryAt { get; set; }

    /// <summary>The last failure, shaped (provider HTTP status / parse error), ≤ 2000 chars.</summary>
    public string? LastFailure { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
