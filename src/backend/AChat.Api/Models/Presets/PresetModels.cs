using System.ComponentModel.DataAnnotations;
using AChat.Core.Entities;

namespace AChat.Api.Models.Presets;

public record CreatePresetRequest(
    [Required] string Name,
    [Required] LLMProvider Provider,
    string? ApiKey,
    string? BaseUrl,
    [Required] string ModelName,
    string? EmbeddingModel,
    string? ParametersJson);

public record UpdatePresetRequest(
    string? Name,
    string? ApiKey,
    string? BaseUrl,
    string? ModelName,
    string? EmbeddingModel,
    string? ParametersJson);

public record PresetResponse(
    Guid Id,
    string Name,
    LLMProvider Provider,
    string? BaseUrl,
    string ModelName,
    string? EmbeddingModel,
    string? ParametersJson,
    bool HasApiKey,
    DateTime CreatedAt,
    DateTime UpdatedAt);
