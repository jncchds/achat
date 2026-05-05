namespace AChat.Core.DTOs.Bots;

public record BotDto(
    Guid Id,
    Guid OwnerId,
    string Name,
    Guid PresetId,
    string PresetName,
    string Personality,
    bool HasTelegramToken,
    string UnknownUserReply,
    string? Gender,
    string? Language,
    int? EvolutionIntervalHours,
    DateTime? LastEvolvedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record CreateBotRequest(string Name, Guid PresetId, string Personality, string? Gender = null, string? Language = null);

public record UpdateBotRequest(
    string? Name,
    Guid? PresetId,
    string? Personality,
    string? TelegramToken,
    string? UnknownUserReply,
    string? Gender,
    string? Language,
    int? EvolutionIntervalHours);

public record ReplacePersonalityRequest(string Personality);

public record NudgeRequest(string? Direction);

public record BotAccessRequestDto(
    Guid Id,
    Guid BotId,
    Guid RequesterId,
    string RequesterUsername,
    string Status,
    DateTime CreatedAt);

public record RespondToAccessRequest(bool Approve);
