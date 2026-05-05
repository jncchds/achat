namespace AChat.Core.DTOs.Conversations;

public record ConversationDto(
    Guid Id,
    Guid BotId,
    string BotName,
    string Title,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record MessageDto(
    Guid Id,
    string Role,
    string Content,
    DateTime CreatedAt);

public record ChatRequest(string Content);

public record CreateConversationRequest(string? Title);
