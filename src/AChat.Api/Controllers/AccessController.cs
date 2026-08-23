using AChat.Api.Dtos;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

/// <summary>
/// The invitee side of sharing (§2.5): presenting a bot's invite code and tracking one's
/// own access requests. Presenting a code only creates a <b>request</b> — the bot admin
/// must approve it before a <see cref="BotAccess"/> grant exists. One live request per
/// (bot, user); a rejected user may re-request (re-submitting the code re-opens it).
/// </summary>
[ApiController]
[Authorize]
[Route("api/access")]
public class AccessController(AppDbContext db) : ControllerBase
{
    private int _userId => int.Parse(User.FindFirst("sub")!.Value);

    /// <summary>
    /// Present an invite code to request access to a bot (§2.5).
    /// </summary>
    [HttpPost("request")]
    public async Task<ActionResult<AccessRequestResponseDto>> RequestAccess(AccessRequestByCode req)
    {
        var code = req.Code?.Trim() ?? "";
        if (code.Length == 0)
            return BadRequest(new { message = "Enter the invite code." });

        var bot = await db.Bots.FirstOrDefaultAsync(b => b.InviteCode == code);
        if (bot is null)
            return NotFound(new { message = "That code is not recognised. Check it with the bot's creator." });

        // The creator needs no request.
        if (bot.OwnerId == _userId)
            return Conflict(new { message = "You created this bot — no request needed." });

        // Already granted — nothing to request.
        if (await db.BotAccesses.AnyAsync(a => a.BotId == bot.Id && a.UserId == _userId))
            return Conflict(new AccessRequestResponseDto
            {
                State = "already",
                BotId = bot.Id,
                BotName = bot.Name,
                Request = null,
            });

        var existing = await db.AccessRequests
            .FirstOrDefaultAsync(r => r.BotId == bot.Id && r.RequesterId == _userId);

        AccessRequest request;
        string state;
        var now = DateTime.UtcNow;

        if (existing is null)
        {
            request = new AccessRequest
            {
                BotId = bot.Id,
                RequesterId = _userId,
                Status = AccessRequestStatus.Pending,
                InviteCode = code,
                CreatedAt = now,
            };
            db.AccessRequests.Add(request);
            state = "pending";
        }
        else if (existing.Status == AccessRequestStatus.Pending)
        {
            // One live request per (bot, user) — already waiting on the admin.
            return Ok(new AccessRequestResponseDto
            {
                State = "pending",
                BotId = bot.Id,
                BotName = bot.Name,
                Request = ToDto(bot.Name, existing),
            });
        }
        else
        {
            // Rejected (or a stale approved-without-grant) → re-open as a fresh request.
            existing.Status = AccessRequestStatus.Pending;
            existing.InviteCode = code;
            existing.CreatedAt = now;
            existing.DecidedAt = null;
            existing.DecidedById = null;
            request = existing;
            state = "reopened";
        }

        await db.SaveChangesAsync();
        return Ok(new AccessRequestResponseDto
        {
            State = state,
            BotId = bot.Id,
            BotName = bot.Name,
            Request = ToDto(bot.Name, request),
        });
    }

    /// <summary>The caller's access requests across all bots, most recent first (§2.5).</summary>
    [HttpGet("requests")]
    public async Task<ActionResult<IReadOnlyList<AccessRequestDto>>> MyRequests()
    {
        var requests = await db.AccessRequests.AsNoTracking()
            .Where(r => r.RequesterId == _userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        var botIds = requests.Select(r => r.BotId).Distinct().ToList();
        var bots = await db.Bots.AsNoTracking()
            .Where(b => botIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Name);

        return Ok(requests.Select(r => ToDto(
            bots.GetValueOrDefault(r.BotId) ?? "?", r)).ToList());
    }

    private static AccessRequestDto ToDto(string botName, AccessRequest r) => new()
    {
        Id = r.Id,
        BotId = r.BotId,
        BotName = botName,
        RequesterId = r.RequesterId,
        Status = r.Status,
        CreatedAt = r.CreatedAt,
        DecidedAt = r.DecidedAt,
    };
}
