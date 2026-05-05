using AChat.Core.DTOs.LlmUsage;
using AChat.Core.Interfaces.Services;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Infrastructure.Services;

public class LlmUsageService(AppDbContext db) : ILlmUsageService
{
    public async Task<LlmUsagePagedResult> GetUserUsageAsync(Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.LlmInteractions
            .AsNoTracking()
            .Include(i => i.Bot)
            .Include(i => i.User)
            .Include(i => i.Preset)
            .Where(i => i.UserId == userId)
            .OrderByDescending(i => i.CreatedAt);

        return await ExecutePagedAsync(query, page, pageSize, ct);
    }

    public async Task<LlmUsagePagedResult> GetAllUsageAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.LlmInteractions
            .AsNoTracking()
            .Include(i => i.Bot)
            .Include(i => i.User)
            .Include(i => i.Preset)
            .OrderByDescending(i => i.CreatedAt);

        return await ExecutePagedAsync(query, page, pageSize, ct);
    }

    public async Task<LlmUsagePagedResult> GetBotUsageForUserAsync(Guid botId, Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.LlmInteractions
            .AsNoTracking()
            .Include(i => i.Bot)
            .Include(i => i.User)
            .Include(i => i.Preset)
            .Where(i => i.BotId == botId && i.UserId == userId)
            .OrderByDescending(i => i.CreatedAt);

        return await ExecutePagedAsync(query, page, pageSize, ct);
    }

    private static async Task<LlmUsagePagedResult> ExecutePagedAsync(
        IQueryable<Core.Entities.LlmInteraction> query, int page, int pageSize, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new LlmInteractionDto(
                i.Id, i.BotId, i.Bot != null ? i.Bot.Name : null,
                i.ConversationId,
                i.UserId, i.User.Username, i.PresetId,
                i.Preset != null ? i.Preset.Name : null,
                i.Endpoint, i.ModelName, i.InputTokens, i.OutputTokens,
                i.Metadata, i.CreatedAt))
            .ToListAsync(ct);

        return new LlmUsagePagedResult(items, total, page, pageSize);
    }
}
