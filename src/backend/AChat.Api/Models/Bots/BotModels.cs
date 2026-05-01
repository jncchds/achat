using System.ComponentModel.DataAnnotations;

namespace AChat.Api.Models.Bots;

public record CreateBotRequest(
    [Required] string Name,
    int? Age,
    string? Gender,
    [Required] string CharacterDescription,
    string? PreferredLanguage,
    Guid? LLMProviderPresetId,
    Guid? EmbeddingPresetId,
    string? TelegramBotToken);

public record UpdateBotRequest(
    string? Name,
    int? Age,
    string? Gender,
    string? CharacterDescription,
    string? PreferredLanguage,
    Guid? LLMProviderPresetId,
    Guid? EmbeddingPresetId,
    string? TelegramBotToken);

public record PersonaPushRequest([Required] string Direction);

public record RandomizePersonaRequest(
    Guid? PresetId,
    int? Age,
    string? Gender,
    string? CharacterDescription);

public record RandomizePersonaResponse(string CharacterDescription);

public record BotResponse(
    Guid Id,
    string Name,
    int? Age,
    string? Gender,
    string CharacterDescription,
    string EvolvingPersonaPrompt,
    string? PersonaPushText,
    int PersonaPushRemainingCycles,
    string? PreferredLanguage,
    Guid? LLMProviderPresetId,
    Guid? EmbeddingPresetId,
    bool HasTelegramToken,
    DateTime CreatedAt,
    DateTime UpdatedAt);
