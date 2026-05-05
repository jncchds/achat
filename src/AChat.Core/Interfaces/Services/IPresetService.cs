using AChat.Core.DTOs.Presets;

namespace AChat.Core.Interfaces.Services;

public interface IPresetService
{
    Task<IReadOnlyList<PresetDto>> GetUserPresetsAsync(Guid userId, CancellationToken ct = default);
    Task<PresetDto?> GetPresetAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task<PresetDto> CreatePresetAsync(Guid userId, CreatePresetRequest request, CancellationToken ct = default);
    Task<PresetDto?> UpdatePresetAsync(Guid id, Guid userId, UpdatePresetRequest request, CancellationToken ct = default);
    Task<bool> DeletePresetAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetModelsAsync(Guid id, Guid userId, CancellationToken ct = default);
}
