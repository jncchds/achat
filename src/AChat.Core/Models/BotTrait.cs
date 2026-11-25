namespace AChat.Core.Models;

/// <summary>
/// One short item of a bot's evolved layer (§2.4, §4.7): "teases them about their
/// cooking". The layer is the list of active items (<see cref="RetiredAt"/> null).
/// Items are never deleted — retiring keeps them inspectable and lets a revert
/// restore them.
/// </summary>
public class BotTrait
{
    public int Id { get; set; }
    public int BotId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>The assistant message whose envelope added it; null for human edits, legacy and consolidation.</summary>
    public int? SourceMessageId { get; set; }

    /// <summary>Null = active.</summary>
    public DateTime? RetiredAt { get; set; }

    /// <summary>The assistant message whose envelope retired it (counts toward the daily cap); null otherwise.</summary>
    public int? RetiredByMessageId { get; set; }

    public Bot Bot { get; set; } = null!;
}
