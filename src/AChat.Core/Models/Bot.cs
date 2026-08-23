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

    /// <summary>
    /// Whether the background evolution worker may maintain this bot's
    /// evolved-traits layer (§2.4). Off for someone who wants a fixed character.
    /// </summary>
    public bool EvolutionEnabled { get; set; } = true;

    /// <summary>
    /// Whether the model's chain of thought is surfaced in the chat as a
    /// "thinking" block (§2.5). Set by the creator. Off by default — raw
    /// reasoning is a debugging aid, not reading material. The reasoning is
    /// always persisted and logged (§4.4); this only controls display.
    /// </summary>
    public bool ShowThinking { get; set; }

    /// <summary>
    /// Watermark for evolution (§2.4): the highest message Id (across the
    /// creator's conversations) already considered by a completed evolution pass.
    /// Null = nothing processed yet. Only the creator's messages count.
    /// </summary>
    public int? EvolutionUpToMessageId { get; set; }

    /// <summary>
    /// Kill switch (§12a, §2.5). When true, every LLM call for this bot is refused
    /// with a clear notice — an immediate way for the creator to stop the bot from
    /// consuming their key (e.g. after a runaway conversation or a bill spike).
    /// Default false.
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>
    /// Usage quota (§12a, §2.5): a cap on the total number of LLM interactions
    /// logged for this bot. Null or 0 = unlimited. When the cap is reached the bot
    /// stops answering (the kill-switch notice). The creator can raise it later.
    /// </summary>
    public int? MaxInteractions { get; set; }

    public int PresetId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Owner { get; set; } = null!;
    public LlmPreset Preset { get; set; } = null!;
}
