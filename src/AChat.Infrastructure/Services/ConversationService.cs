using AChat.Core.DTOs.Conversations;
using AChat.Core.Entities;
using AChat.Core.Enums;
using AChat.Core.Interfaces.Services;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AChat.Infrastructure.Services;

public partial class ConversationService(
    AppDbContext db,
    IBotService botService,
    ILogger<ConversationService> logger) : IConversationService
{
    public async Task<IReadOnlyList<ConversationDto>> GetConversationsAsync(Guid botId, Guid userId, CancellationToken ct = default)
    {
        if (!await botService.HasAccessAsync(botId, userId, ct)) return [];

        return await db.Conversations
            .AsNoTracking()
            .Include(c => c.Bot)
            .Where(c => c.BotId == botId && c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => ToDto(c))
            .ToListAsync(ct);
    }

    public async Task<ConversationDto?> GetConversationAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var conv = await db.Conversations.AsNoTracking().Include(c => c.Bot)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        return conv is null ? null : ToDto(conv);
    }

    public async Task<ConversationDto> CreateConversationAsync(Guid botId, Guid userId, string? title, CancellationToken ct = default)
    {
        var bot = await db.Bots.AsNoTracking().FirstOrDefaultAsync(b => b.Id == botId, ct);
        var conv = new Conversation
        {
            BotId = botId,
            UserId = userId,
            Title = string.IsNullOrWhiteSpace(title) ? $"Chat {DateTime.UtcNow:g}" : title,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Conversations.Add(conv);
        await db.SaveChangesAsync(ct);
        conv.Bot = bot!;
        LogConversationCreated(logger, conv.Id, botId, userId);
        return ToDto(conv);
    }

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid conversationId, Guid userId, CancellationToken ct = default)
    {
        var conv = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);
        if (conv is null) return [];

        return await db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new MessageDto(m.Id, m.Role.ToString().ToLower(), m.Content, m.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<bool> DeleteConversationAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
        if (conv is null) return false;
        db.Conversations.Remove(conv);
        await db.SaveChangesAsync(ct);
        LogConversationDeleted(logger, id);
        return true;
    }

    private static ConversationDto ToDto(Conversation c) =>
        new(c.Id, c.BotId, c.Bot?.Name ?? string.Empty, c.Title, c.CreatedAt, c.UpdatedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation {ConversationId} created for bot {BotId} by user {UserId}")]
    private static partial void LogConversationCreated(ILogger logger, Guid conversationId, Guid botId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation {ConversationId} deleted")]
    private static partial void LogConversationDeleted(ILogger logger, Guid conversationId);
}
