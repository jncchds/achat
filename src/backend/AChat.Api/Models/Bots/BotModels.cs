using System.ComponentModel.DataAnnotations;

namespace AChat.Api.Models.Bots;

public record CreateBotRequest(
    [Required] string Name,
    int? Age,
    string? Gender,
    [Required] string CharacterDescription,
    Guid? LLMProviderPresetId,
    Guid? EmbeddingPresetId,
    string? TelegramBotToken);

public record UpdateBotRequest(
    string? Name,
    int? Age,
    string? Gender,
    string? CharacterDescription,
    Guid? LLMProviderPresetId,
    Guid? EmbeddingPresetId,
    string? TelegramBotToken);

public record BotResponse(
    Guid Id,
    string Name,
    int? Age,
    string? Gender,
    string CharacterDescription,
    string EvolvingPersonaPrompt,
    Guid? LLMProviderPresetId,
    Guid? EmbeddingPresetId,
    bool HasTelegramToken,
    DateTime CreatedAt,
    DateTime UpdatedAt);
