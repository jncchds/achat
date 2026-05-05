namespace AChat.Core.DTOs.LlmUsage;

public record LlmInteractionDto(
    Guid Id,
    Guid? BotId,
    string? BotName,
    Guid UserId,
    string Username,
    Guid? PresetId,
    string? PresetName,
    string Endpoint,
    string ModelName,
    int InputTokens,
    int OutputTokens,
    string? Metadata,
    DateTime CreatedAt);

public record LlmUsagePagedResult(
    IReadOnlyList<LlmInteractionDto> Items,
    int TotalCount,
    int Page,
    int PageSize);
