using AChat.Core.Enums;

namespace AChat.Core.DTOs.Presets;

public record PresetDto(
    Guid Id,
    string Name,
    ProviderType ProviderType,
    string ProviderUrl,
    bool HasApiToken,
    string GenerationModel,
    string? EmbeddingModel,
    int? TimeoutSeconds,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record CreatePresetRequest(
    string Name,
    ProviderType ProviderType,
    string ProviderUrl,
    string? ApiToken,
    string GenerationModel,
    string? EmbeddingModel,
    int? TimeoutSeconds);

public record UpdatePresetRequest(
    string? Name,
    ProviderType? ProviderType,
    string? ProviderUrl,
    string? ApiToken,
    string? GenerationModel,
    string? EmbeddingModel,
    int? TimeoutSeconds);

public record GetModelsInlineRequest(
    ProviderType ProviderType,
    string ProviderUrl,
    string? ApiToken,
    string? GenerationModel);
