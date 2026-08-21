namespace AChat.Core.Models;

/// <summary>
/// One row per (BotId, UserId) — the rolling summary of everything older than the
/// recent-history window (§5.2, §5.8).
/// </summary>
public class ConversationSummary
{
    public int Id { get; set; }
    public int BotId { get; set; }
    public int UserId { get; set; }

    /// <summary>The relationship so far, rewritten in place by the summarisation worker.</summary>
    public string Content { get; set; } = "";

    /// <summary>Id of the last message folded into <see cref="Content"/>.</summary>
    public int CoversUpToMessageId { get; set; }

    public int TokenCount { get; set; }

    /// <summary>
    /// When the summary was last rewritten — the anchor the summary block carries into
    /// the prompt so the bot knows how stale its own narrative is (§5.9).
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
