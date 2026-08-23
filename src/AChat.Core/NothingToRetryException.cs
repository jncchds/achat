namespace AChat.Core;

/// <summary>
/// There is no failed or stopped turn to retry (§12b) — the conversation is empty
/// or its last turn already completed. The client surfaces this as a plain
/// "nothing to retry" notice, not an error state.
/// </summary>
public sealed class NothingToRetryException : Exception
{
    public NothingToRetryException(string message) : base(message) { }
}
