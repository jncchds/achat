using AChat.Core.DTOs.Bots;
using AChat.Core.Entities;
using AChat.Core.Enums;
using AChat.Core.Interfaces.Services;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace AChat.Infrastructure.Services;

public partial class BotService(
    AppDbContext db,
    ILogger<BotService> logger) : IBotService
{
    public async Task<IReadOnlyList<BotDto>> GetUserBotsAsync(Guid userId, CancellationToken ct = default)
    {
        return await db.Bots
            .AsNoTracking()
            .Include(b => b.Preset)
            .Where(b => b.OwnerId == userId)
            .OrderBy(b => b.Name)
            .Select(b => ToDto(b))
            .ToListAsync(ct);
    }

    public async Task<BotDto?> GetBotAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var bot = await db.Bots.AsNoTracking().Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == id && (b.OwnerId == userId || db.BotAccessRequests
                .Any(r => r.BotId == id && r.RequesterId == userId && r.Status == AccessRequestStatus.Approved)), ct);
        return bot is null ? null : ToDto(bot);
    }

    public async Task<BotDto> CreateBotAsync(Guid userId, CreateBotRequest request, CancellationToken ct = default)
    {
        var bot = new Bot
        {
            OwnerId = userId,
            PresetId = request.PresetId,
            Name = request.Name,
            Personality = request.Personality,
            Gender = request.Gender,
            Language = request.Language,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Bots.Add(bot);
        await db.SaveChangesAsync(ct);
        await db.Entry(bot).Reference(b => b.Preset).LoadAsync(ct);
        LogBotCreated(logger, bot.Id, userId);
        return ToDto(bot);
    }

    public async Task<BotDto?> UpdateBotAsync(Guid id, Guid userId, UpdateBotRequest request, CancellationToken ct = default)
    {
        var bot = await db.Bots.Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == userId, ct);
        if (bot is null) return null;

        if (request.Name is not null) bot.Name = request.Name;
        if (request.PresetId is not null) bot.PresetId = request.PresetId.Value;
        if (request.Personality is not null) bot.Personality = request.Personality;
        if (request.TelegramToken is not null) bot.TelegramToken = string.IsNullOrEmpty(request.TelegramToken) ? null : request.TelegramToken;
        if (request.UnknownUserReply is not null) bot.UnknownUserReply = request.UnknownUserReply;
        if (request.Gender is not null) bot.Gender = string.IsNullOrEmpty(request.Gender) ? null : request.Gender;
        if (request.Language is not null) bot.Language = string.IsNullOrEmpty(request.Language) ? null : request.Language;
        if (request.EvolutionIntervalHours is not null) bot.EvolutionIntervalHours = request.EvolutionIntervalHours;
        bot.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        if (bot.Preset is null) await db.Entry(bot).Reference(b => b.Preset).LoadAsync(ct);
        LogBotUpdated(logger, id);
        return ToDto(bot);
    }

    public async Task<bool> DeleteBotAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var bot = await db.Bots.FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == userId, ct);
        if (bot is null) return false;
        db.Bots.Remove(bot);
        await db.SaveChangesAsync(ct);
        LogBotDeleted(logger, id);
        return true;
    }

    public async Task<bool> ReplacePersonalityAsync(Guid id, Guid userId, string personality, CancellationToken ct = default)
    {
        var bot = await db.Bots.FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == userId, ct);
        if (bot is null) return false;

        bot.Personality = personality;
        bot.UpdatedAt = DateTime.UtcNow;

        // Clear all conversation histories for this bot
        var conversations = await db.Conversations.Where(c => c.BotId == id).ToListAsync(ct);
        var conversationIds = conversations.Select(c => c.Id).ToList();
        await db.Messages.Where(m => conversationIds.Contains(m.ConversationId)).ExecuteDeleteAsync(ct);
        await db.Conversations.Where(c => c.BotId == id).ExecuteDeleteAsync(ct);

        await db.SaveChangesAsync(ct);
        LogPersonalityReplaced(logger, id);
        return true;
    }

    public async Task<bool> NudgeEvolutionAsync(Guid id, Guid userId, string? direction, CancellationToken ct = default)
    {
        var bot = await db.Bots.Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == id && b.OwnerId == userId, ct);
        if (bot is null) return false;

        await RunEvolutionAsync(bot, direction, ct);
        return true;
    }

    public async Task RunEvolutionAsync(Bot bot, string? direction, CancellationToken ct = default)
    {
        var preset = bot.Preset ?? await db.LlmPresets.FindAsync([bot.PresetId], ct);
        if (preset is null) return;

        var intervalHours = bot.EvolutionIntervalHours ?? 24;
        var since = bot.LastEvolvedAt ?? DateTime.MinValue;
        var ownerMessages = await db.Messages
            .AsNoTracking()
            .Include(m => m.Conversation)
            .Where(m => m.Conversation.BotId == bot.Id
                     && m.Conversation.UserId == bot.OwnerId
                     && m.CreatedAt > since
                     && m.Role == MessageRole.User)
            .OrderByDescending(m => m.CreatedAt)
            .Take(50)
            .Select(m => m.Content)
            .ToListAsync(ct);

        if (ownerMessages.Count == 0)
        {
            LogEvolutionSkipped(logger, bot.Id, "no owner messages");
            return;
        }

        var messageContext = string.Join("\n---\n", ownerMessages);
        var directionHint = string.IsNullOrWhiteSpace(direction) ? "" :
            $" Additionally, lean slightly in this direction: {direction}.";

        var prompt = $"""
            You have the following personality:
            {bot.Personality}

            Review these recent messages from your owner and propose a subtly evolved version of your personality 
            that feels natural and continuous — no dramatic shifts, just organic growth.{directionHint}
            Return only the updated personality text, nothing else.

            Owner messages:
            {messageContext}
            """;

        try
        {
            var kernel = SemanticKernelFactory.Build(preset);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();
            history.AddUserMessage(prompt);
            var result = await chatService.GetChatMessageContentAsync(history, cancellationToken: ct);
            var newPersonality = result.Content?.Trim();

            if (!string.IsNullOrWhiteSpace(newPersonality))
            {
                bot.Personality = newPersonality;
                bot.LastEvolvedAt = DateTime.UtcNow;
                bot.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                LogEvolutionComplete(logger, bot.Id);
            }
        }
        catch (Exception ex)
        {
            LogEvolutionError(logger, bot.Id, ex);
        }
    }

    public async Task<IReadOnlyList<BotAccessRequestDto>> GetAccessRequestsAsync(Guid botId, Guid ownerId, CancellationToken ct = default)
    {
        var isOwner = await db.Bots.AnyAsync(b => b.Id == botId && b.OwnerId == ownerId, ct);
        if (!isOwner) return [];

        return await db.BotAccessRequests
            .AsNoTracking()
            .Include(r => r.Requester)
            .Where(r => r.BotId == botId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new BotAccessRequestDto(r.Id, r.BotId, r.RequesterId, r.Requester.Username, r.Status.ToString(), r.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<bool> RespondToAccessRequestAsync(Guid botId, Guid requestId, Guid ownerId, bool approve, CancellationToken ct = default)
    {
        var request = await db.BotAccessRequests
            .Include(r => r.Bot)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.BotId == botId && r.Bot.OwnerId == ownerId, ct);
        if (request is null) return false;

        request.Status = approve ? AccessRequestStatus.Approved : AccessRequestStatus.Rejected;
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        LogAccessRequestResponded(logger, requestId, approve);
        return true;
    }

    public async Task<BotAccessRequestDto?> RequestAccessAsync(Guid botId, Guid requesterId, CancellationToken ct = default)
    {
        var bot = await db.Bots.AsNoTracking().FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot is null) return null;
        if (bot.OwnerId == requesterId) return null; // owner always has access

        var existing = await db.BotAccessRequests
            .FirstOrDefaultAsync(r => r.BotId == botId && r.RequesterId == requesterId, ct);
        if (existing is not null)
            return new BotAccessRequestDto(existing.Id, existing.BotId, existing.RequesterId, string.Empty, existing.Status.ToString(), existing.CreatedAt);

        var req = new BotAccessRequest
        {
            BotId = botId,
            RequesterId = requesterId,
            Status = AccessRequestStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.BotAccessRequests.Add(req);
        await db.SaveChangesAsync(ct);
        LogAccessRequested(logger, botId, requesterId);
        return new BotAccessRequestDto(req.Id, req.BotId, req.RequesterId, string.Empty, req.Status.ToString(), req.CreatedAt);
    }

    public async Task<bool> HasAccessAsync(Guid botId, Guid userId, CancellationToken ct = default)
    {
        var bot = await db.Bots.AsNoTracking().FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot is null) return false;
        if (bot.OwnerId == userId) return true;
        return await db.BotAccessRequests
            .AnyAsync(r => r.BotId == botId && r.RequesterId == userId && r.Status == AccessRequestStatus.Approved, ct);
    }

    private static BotDto ToDto(Bot b) => new(
        b.Id, b.OwnerId, b.Name, b.PresetId, b.Preset?.Name ?? string.Empty, b.Personality,
        !string.IsNullOrEmpty(b.TelegramToken), b.UnknownUserReply, b.Gender, b.Language,
        b.EvolutionIntervalHours, b.LastEvolvedAt, b.CreatedAt, b.UpdatedAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bot {BotId} created by user {UserId}")]
    private static partial void LogBotCreated(ILogger logger, Guid botId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bot {BotId} updated")]
    private static partial void LogBotUpdated(ILogger logger, Guid botId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bot {BotId} deleted")]
    private static partial void LogBotDeleted(ILogger logger, Guid botId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Personality replaced for bot {BotId}, history cleared")]
    private static partial void LogPersonalityReplaced(ILogger logger, Guid botId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evolution complete for bot {BotId}")]
    private static partial void LogEvolutionComplete(ILogger logger, Guid botId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Evolution skipped for bot {BotId}: {Reason}")]
    private static partial void LogEvolutionSkipped(ILogger logger, Guid botId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Evolution failed for bot {BotId}")]
    private static partial void LogEvolutionError(ILogger logger, Guid botId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Access request {RequestId} responded: approved={Approved}")]
    private static partial void LogAccessRequestResponded(ILogger logger, Guid requestId, bool approved);

    [LoggerMessage(Level = LogLevel.Information, Message = "Access requested for bot {BotId} by user {UserId}")]
    private static partial void LogAccessRequested(ILogger logger, Guid botId, Guid userId);
}
