namespace AChat.Core.Models;

/// <summary>
/// Audit record of every LLM call, success or failure (§4.4). This is the primary
/// debugging tool for "why did my friend say that," alongside the memory viewer.
/// </summary>
public class LlmInteraction
{
    public int Id { get; set; }

    public InteractionPurpose Purpose { get; set; } = InteractionPurpose.Chat;

    /// <summary>The bot this call was made for, if any. Null for preset-level ops.</summary>
    public int? BotId { get; set; }

    /// <summary>The user whose request triggered this call.</summary>
    public int UserId { get; set; }

    /// <summary>The preset (provider + endpoint + key) that was used.</summary>
    public int PresetId { get; set; }

    /// <summary>Provider type that was actually called.</summary>
    public LlmProvider Provider { get; set; }

    /// <summary>The generation/utility model used.</summary>
    public string Model { get; set; } = "";

    /// <summary>Endpoint (base) that was hit. Key is never stored here (§7.3).</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Full request as sent: messages + parameters, serialized (§4.4).</summary>
    public string RequestBody { get; set; } = "";

    /// <summary>Reasoning content, if any (§4.3, §4.4).</summary>
    public string? Reasoning { get; set; }

    /// <summary>Response content (full, or the error text on failure).</summary>
    public string? ResponseContent { get; set; }

    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }

    /// <summary>finish_reason from the provider (stop / length / tool_calls / …).</summary>
    public string? FinishReason { get; set; }

    /// <summary>HTTP status code, if the call reached the provider and got a response.</summary>
    public int? HttpStatus { get; set; }

    /// <summary>True when the call failed in any way (§4.4).</summary>
    public bool Failed { get; set; }

    /// <summary>Error message + provider error payload when <see cref="Failed"/>.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Ids of the memories recalled into this prompt, as a JSON array of ints
    /// (e.g. "[3,7]"). Null when no retrieval ran. Lets the viewer show exactly
    /// which memories a reply leaned on (§5.6, §5.10).
    /// </summary>
    public string? RecalledMemoryIds { get; set; }

    public DateTime CreatedAt { get; set; }

    public Bot? Bot { get; set; }
    public User User { get; set; } = null!;
    public LlmPreset Preset { get; set; } = null!;
}
