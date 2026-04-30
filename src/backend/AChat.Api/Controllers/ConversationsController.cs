using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AChat.Api.Models.Bots;
using AChat.Core.Entities;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/bots/{botId:guid}/conversations")]
[Authorize]
public class ConversationsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ConversationsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ConversationResponse>>> List(Guid botId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await CanAccessBotAsync(botId, userId, ct)) return NotFound();

        var conversations = await _db.BotConversations
            .Where(c => c.BotId == botId && c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new ConversationResponse(
                c.Id,
                c.Title,
                c.CreatedAt,
                c.UpdatedAt,
                c.LastMessageAt,
                c.Messages.Count))
            .ToListAsync(ct);

        return Ok(conversations);
    }

    [HttpPost]
    public async Task<ActionResult<ConversationResponse>> Create(
        Guid botId,
        [FromBody] CreateConversationRequest? req,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await CanAccessBotAsync(botId, userId, ct)) return NotFound();

        var now = DateTime.UtcNow;
        var conversation = new BotConversation
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            UserId = userId,
            Title = string.IsNullOrWhiteSpace(req?.InitialTitle)
                ? "New conversation"
                : req!.InitialTitle!.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
            LastMessageAt = now
        };

        _db.BotConversations.Add(conversation);

        var state = await _db.BotConversationStates
            .FirstOrDefaultAsync(s => s.BotId == botId && s.UserId == userId, ct);

        if (state is null)
        {
            _db.BotConversationStates.Add(new BotConversationState
            {
                Id = Guid.NewGuid(),
                BotId = botId,
                UserId = userId,
                CurrentConversationId = conversation.Id,
                UpdatedAt = now
            });
        }
        else
        {
            state.CurrentConversationId = conversation.Id;
            state.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        var response = new ConversationResponse(
            conversation.Id,
            conversation.Title,
            conversation.CreatedAt,
            conversation.UpdatedAt,
            conversation.LastMessageAt,
            MessageCount: 0);

        return CreatedAtAction(nameof(GetMessages), new { botId, conversationId = conversation.Id }, response);
    }

    [HttpGet("{conversationId:guid}/messages")]
    public async Task<ActionResult<IEnumerable<ConversationMessageResponse>>> GetMessages(
        Guid botId,
        Guid conversationId,
        CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await CanAccessBotAsync(botId, userId, ct)) return NotFound();

        var exists = await _db.BotConversations.AnyAsync(c =>
            c.Id == conversationId && c.BotId == botId && c.UserId == userId, ct);

        if (!exists) return NotFound();

        var messages = await _db.Messages
            .Where(m => m.BotId == botId
                        && m.UserId == userId
                        && m.ConversationId == conversationId
                        && m.Role != MessageRole.System)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ConversationMessageResponse(
                m.Id,
                m.ConversationId,
                m.Role == MessageRole.User ? "user" : "assistant",
                m.Content,
                m.CreatedAt))
            .ToListAsync(ct);

        await UpsertConversationStateAsync(botId, userId, conversationId, ct);

        return Ok(messages);
    }

    private async Task<bool> CanAccessBotAsync(Guid botId, Guid userId, CancellationToken ct)
    {
        var bot = await _db.Bots
            .Where(b => b.Id == botId)
            .Select(b => new { b.OwnerId })
            .FirstOrDefaultAsync(ct);

        if (bot is null) return false;

        if (bot.OwnerId == userId) return true;

        return await _db.BotAccessLists.AnyAsync(a =>
            a.BotId == botId
            && a.SubjectType == AccessSubjectType.AchatUser
            && a.SubjectId == userId.ToString()
            && a.Status == AccessStatus.Allowed, ct);
    }

    private async Task UpsertConversationStateAsync(
        Guid botId,
        Guid userId,
        Guid conversationId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var state = await _db.BotConversationStates
            .FirstOrDefaultAsync(s => s.BotId == botId && s.UserId == userId, ct);

        if (state is null)
        {
            _db.BotConversationStates.Add(new BotConversationState
            {
                Id = Guid.NewGuid(),
                BotId = botId,
                UserId = userId,
                CurrentConversationId = conversationId,
                UpdatedAt = now
            });
        }
        else
        {
            state.CurrentConversationId = conversationId;
            state.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
