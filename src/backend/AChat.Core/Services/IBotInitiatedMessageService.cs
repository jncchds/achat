namespace AChat.Core.Services;

public interface IBotInitiatedMessageService
{
    /// <summary>
    /// Makes the bot send an unprompted message to a user based on the given prompt.
    /// Only fires if the user has at least one active web connection; offline users are skipped.
    /// The message is persisted as an assistant message with no preceding user message.
    /// </summary>
    Task SendInitiatedMessageAsync(Guid botId, Guid userId, string prompt, CancellationToken ct = default);
}
