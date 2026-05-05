using AChat.Core.DTOs.Presets;
using AChat.Core.Entities;
using AChat.Core.Interfaces.Services;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AChat.Infrastructure.Services;

public partial class PresetService(
    AppDbContext db,
    IModelListService modelListService,
    ILogger<PresetService> logger) : IPresetService
{
    public async Task<IReadOnlyList<PresetDto>> GetUserPresetsAsync(Guid userId, CancellationToken ct = default)
    {
        return await db.LlmPresets
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Name)
            .Select(p => ToDto(p))
            .ToListAsync(ct);
    }

    public async Task<PresetDto?> GetPresetAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var preset = await db.LlmPresets.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
        return preset is null ? null : ToDto(preset);
    }

    public async Task<PresetDto> CreatePresetAsync(Guid userId, CreatePresetRequest request, CancellationToken ct = default)
    {
        var preset = new LlmPreset
        {
            UserId = userId,
            Name = request.Name,
            ProviderType = request.ProviderType,
            ProviderUrl = request.ProviderUrl,
            ApiToken = request.ApiToken,
            GenerationModel = request.GenerationModel,
            EmbeddingModel = request.EmbeddingModel,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.LlmPresets.Add(preset);
        await db.SaveChangesAsync(ct);
        LogPresetCreated(logger, preset.Id, userId);
        return ToDto(preset);
    }

    public async Task<PresetDto?> UpdatePresetAsync(Guid id, Guid userId, UpdatePresetRequest request, CancellationToken ct = default)
    {
        var preset = await db.LlmPresets.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
        if (preset is null) return null;

        if (request.Name is not null) preset.Name = request.Name;
        if (request.ProviderType is not null) preset.ProviderType = request.ProviderType.Value;
        if (request.ProviderUrl is not null) preset.ProviderUrl = request.ProviderUrl;
        if (request.ApiToken is not null) preset.ApiToken = request.ApiToken;
        if (request.GenerationModel is not null) preset.GenerationModel = request.GenerationModel;
        if (request.EmbeddingModel is not null) preset.EmbeddingModel = request.EmbeddingModel;
        preset.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        LogPresetUpdated(logger, preset.Id);
        return ToDto(preset);
    }

    public async Task<bool> DeletePresetAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var preset = await db.LlmPresets.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
        if (preset is null) return false;
        db.LlmPresets.Remove(preset);
        await db.SaveChangesAsync(ct);
        LogPresetDeleted(logger, id);
        return true;
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var preset = await db.LlmPresets.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
        if (preset is null) return [];
        return await modelListService.GetModelsAsync(preset, ct);
    }

    private static PresetDto ToDto(LlmPreset p) => new(
        p.Id, p.Name, p.ProviderType, p.ProviderUrl,
        !string.IsNullOrEmpty(p.ApiToken), p.GenerationModel, p.EmbeddingModel,
        p.CreatedAt, p.UpdatedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preset {PresetId} created for user {UserId}")]
    private static partial void LogPresetCreated(ILogger logger, Guid presetId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preset {PresetId} updated")]
    private static partial void LogPresetUpdated(ILogger logger, Guid presetId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preset {PresetId} deleted")]
    private static partial void LogPresetDeleted(ILogger logger, Guid presetId);
}
