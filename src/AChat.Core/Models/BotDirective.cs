namespace AChat.Core.Models;

/// <summary>
/// A discrete, individually editable directive on a bot (§2.4) — the admin's
/// deliberate corrections: "be more blunt", "never bring up my ex".
/// Stored as rows (not one blob) so each can be dated, toggled, and deleted.
/// Injected verbatim into the persona block, last, so they win over evolved
/// traits in conflicts — they represent a deliberate decision.
/// </summary>
public class BotDirective
{
    public int Id { get; set; }

    public int BotId { get; set; }

    /// <summary>The directive statement, injected verbatim into the prompt.</summary>
    public string Text { get; set; } = "";

    /// <summary>Inactive directives are kept (dated, re-enableable) but not injected.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Bot Bot { get; set; } = null!;
}
