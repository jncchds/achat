using AChat.Core.DTOs.Bots;

namespace AChat.Core.Interfaces.Services;

public interface IBotService
{
    Task<IReadOnlyList<BotDto>> GetUserBotsAsync(Guid userId, CancellationToken ct = default);
    Task<BotDto?> GetBotAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task<BotDto> CreateBotAsync(Guid userId, CreateBotRequest request, CancellationToken ct = default);
    Task<BotDto?> UpdateBotAsync(Guid id, Guid userId, UpdateBotRequest request, CancellationToken ct = default);
    Task<bool> DeleteBotAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task<bool> ReplacePersonalityAsync(Guid id, Guid userId, string personality, CancellationToken ct = default);
    Task<bool> NudgeEvolutionAsync(Guid id, Guid userId, string? direction, CancellationToken ct = default);
    Task<IReadOnlyList<BotAccessRequestDto>> GetAccessRequestsAsync(Guid botId, Guid ownerId, CancellationToken ct = default);
    Task<bool> RespondToAccessRequestAsync(Guid botId, Guid requestId, Guid ownerId, bool approve, CancellationToken ct = default);
    Task<BotAccessRequestDto?> RequestAccessAsync(Guid botId, Guid requesterId, CancellationToken ct = default);
    Task<bool> HasAccessAsync(Guid botId, Guid userId, CancellationToken ct = default);
}
