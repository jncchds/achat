namespace AChat.Api.Models.Bots;

public record ConversationResponse(
    Guid Id,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime LastMessageAt,
    int MessageCount);

public record ConversationMessageResponse(
    Guid Id,
    Guid ConversationId,
    string Role,
    string Content,
    DateTime CreatedAt);

public record CreateConversationRequest(string? InitialTitle);

public record RenameConversationRequest(string Title);
