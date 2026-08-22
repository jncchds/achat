using AChat.Api.Dtos;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

/// <summary>
/// Browsable LLM interaction log (§4.4) — the primary debugging tool for "why did my
/// friend say that". v1 is admin-scoped: the log runs on the bot admin's key and
/// would expose invitee conversations, so invitees must not read it (§2.5 open item 2).
/// </summary>
[ApiController]
[Authorize]
[Route("api/interactions")]
public class InteractionsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InteractionDto>>> List(
        [FromQuery] int? botId,
        [FromQuery] int? userId,
        [FromQuery] bool? failed,
        [FromQuery] InteractionPurpose? purpose,
        [FromQuery] int take = 100)
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        take = Math.Clamp(take, 1, 500);
        var query = db.LlmInteractions.AsNoTracking();
        if (botId.HasValue) query = query.Where(i => i.BotId == botId.Value);
        if (userId.HasValue) query = query.Where(i => i.UserId == userId.Value);
        if (failed.HasValue) query = query.Where(i => i.Failed == failed.Value);
        if (purpose.HasValue) query = query.Where(i => i.Purpose == purpose.Value);

        var items = await query
            .OrderByDescending(i => i.CreatedAt)
            .Take(take)
            .ToListAsync();

        var botNames = await db.Bots.ToDictionaryAsync(b => b.Id, b => b.Name);
        var userNames = await db.Users.ToDictionaryAsync(u => u.Id, u => u.Username);

        return Ok(items.Select(i => new InteractionDto
        {
            Id = i.Id,
            Purpose = i.Purpose,
            BotId = i.BotId,
            BotName = i.BotId is { } bid ? botNames.GetValueOrDefault(bid) : null,
            UserId = i.UserId,
            Username = userNames.GetValueOrDefault(i.UserId) ?? "?",
            Model = i.Model,
            Endpoint = i.Endpoint,
            PromptTokens = i.PromptTokens,
            CompletionTokens = i.CompletionTokens,
            TotalTokens = i.TotalTokens,
            FinishReason = i.FinishReason,
            Failed = i.Failed,
            HttpStatus = i.HttpStatus,
            CreatedAt = i.CreatedAt,
        }).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<InteractionDetailDto>> Get(int id)
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        var i = await db.LlmInteractions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (i is null) return NotFound();

        return Ok(new InteractionDetailDto
        {
            Id = i.Id,
            Purpose = i.Purpose,
            BotId = i.BotId,
            UserId = i.UserId,
            PresetId = i.PresetId,
            Provider = i.Provider,
            Model = i.Model,
            Endpoint = i.Endpoint,
            RequestBody = i.RequestBody,
            Reasoning = i.Reasoning,
            ResponseContent = i.ResponseContent,
            PromptTokens = i.PromptTokens,
            CompletionTokens = i.CompletionTokens,
            TotalTokens = i.TotalTokens,
            FinishReason = i.FinishReason,
            Failed = i.Failed,
            FailureReason = i.FailureReason,
            RecalledMemoryIds = i.RecalledMemoryIds,
            CreatedAt = i.CreatedAt,
        });
    }
}
