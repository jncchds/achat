namespace AChat.Core;

/// <summary>
/// A call to a provider failed. Carries the HTTP status and the provider's error
/// payload so the interaction log can store them (§4.4).
/// </summary>
public sealed class LlmProviderException : Exception
{
    public int? HttpStatus { get; }
    public string? ErrorPayload { get; }

    public LlmProviderException(string message, int? httpStatus = null, string? errorPayload = null, Exception? inner = null)
        : base(message, inner)
    {
        HttpStatus = httpStatus;
        ErrorPayload = errorPayload;
    }
}
