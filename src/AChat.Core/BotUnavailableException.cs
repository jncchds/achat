namespace AChat.Core;

/// <summary>
/// The bot cannot serve this turn for an administrative reason — it is suspended
/// (kill switch) or has reached its usage quota (§12a, §2.5). Thrown before any
/// LLM work so nothing is logged against the bot and no key is spent. The message
/// is user-facing and surfaces verbatim as a <c>chat.failed</c> notice.
/// </summary>
public sealed class BotUnavailableException(string message) : Exception(message)
{
    /// <summary>True when the bot is paused by its creator (kill switch).</summary>
    public bool Suspended { get; init; }

    /// <summary>True when the usage quota is the reason (not suspension).</summary>
    public bool QuotaExceeded { get; init; }
}
