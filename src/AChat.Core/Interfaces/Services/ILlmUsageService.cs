using AChat.Core.DTOs.LlmUsage;

namespace AChat.Core.Interfaces.Services;

public interface ILlmUsageService
{
    Task<LlmUsagePagedResult> GetUserUsageAsync(Guid userId, int page, int pageSize, CancellationToken ct = default);
    Task<LlmUsagePagedResult> GetAllUsageAsync(int page, int pageSize, CancellationToken ct = default);
    Task<LlmUsagePagedResult> GetBotUsageForUserAsync(Guid botId, Guid userId, int page, int pageSize, CancellationToken ct = default);
}
