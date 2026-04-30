namespace AChat.Api.Models.Access;

public record AccessRequestResponse(
    Guid Id,
    Guid BotId,
    string SubjectType,
    string SubjectId,
    string? DisplayName,
    DateTime RequestedAt,
    string Status);

public record AccessListEntryResponse(
    Guid Id,
    Guid BotId,
    string SubjectType,
    string SubjectId,
    string Status,
    DateTime AddedAt);
