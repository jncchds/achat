namespace AChat.Core.Models;

/// <summary>A turn in a conversation timeline (§2.3).</summary>
public class Message
{
    public int Id { get; set; }
    public int ConversationId { get; set; }

    public MessageRole Role { get; set; }

    public string Content { get; set; } = "";

    /// <summary>
    /// Reasoning/thinking content, if the model produced any (§4.3). Stored; how it is
    /// presented is a UI concern.
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// False when the run was cancelled ("stop generating") and the stored content is a
    /// partial (§4.2).
    /// </summary>
    public bool IsComplete { get; set; } = true;

    /// <summary>
    /// Trustworthy creation time in UTC (§5.9). Stored UTC, rendered in the user's zone.
    /// Drives gap markers in the prompt and relative ages in the UI.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    public Conversation Conversation { get; set; } = null!;
}
