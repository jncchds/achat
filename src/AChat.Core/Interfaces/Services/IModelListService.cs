using AChat.Core.Entities;

namespace AChat.Core.Interfaces.Services;

public interface IModelListService
{
    Task<IReadOnlyList<string>> GetModelsAsync(LlmPreset preset, CancellationToken ct = default);
}
