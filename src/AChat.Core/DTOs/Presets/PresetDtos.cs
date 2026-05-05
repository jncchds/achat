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
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record CreatePresetRequest(
    string Name,
    ProviderType ProviderType,
    string ProviderUrl,
    string? ApiToken,
    string GenerationModel,
    string? EmbeddingModel);

public record UpdatePresetRequest(
    string? Name,
    ProviderType? ProviderType,
    string? ProviderUrl,
    string? ApiToken,
    string? GenerationModel,
    string? EmbeddingModel);
