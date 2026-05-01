using AChat.Api.Models.Access;
using AChat.Core.Entities;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

[Route("api/bots/{botId:guid}")]
[Authorize]
public class AccessController : ApiControllerBase
{
    private readonly AppDbContext _db;

    public AccessController(AppDbContext db) => _db = db;

    // ── Access Requests ───────────────────────────────────────────────────

    [HttpGet("access-requests")]
    public async Task<IActionResult> GetPendingRequests(Guid botId, CancellationToken ct)
    {
        if (!await IsOwnerAsync(botId, ct)) return NotFound();

        var requests = await _db.BotAccessRequests
            .Where(r => r.BotId == botId && r.Status == AccessRequestStatus.Pending)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);

        return Ok(requests.Select(ToRequestResponse));
    }

    [HttpPost("access-requests/{requestId:guid}/approve")]
    public async Task<IActionResult> ApproveRequest(Guid botId, Guid requestId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsOwnerAsync(botId, ct)) return NotFound();

        var request = await _db.BotAccessRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.BotId == botId
                                      && r.Status == AccessRequestStatus.Pending, ct);
        if (request is null) return NotFound();

        request.Status = AccessRequestStatus.Approved;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = userId;

        // Upsert access list entry
        var existing = await _db.BotAccessLists.FirstOrDefaultAsync(
            a => a.BotId == botId && a.SubjectType == request.SubjectType
                                  && a.SubjectId == request.SubjectId, ct);

        if (existing is null)
        {
            _db.BotAccessLists.Add(new BotAccessList
            {
                Id = Guid.NewGuid(),
                BotId = botId,
                SubjectType = request.SubjectType,
                SubjectId = request.SubjectId,
                Status = AccessStatus.Allowed,
                AddedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Status = AccessStatus.Allowed;
        }

        // Create stub user for Telegram-only users if not already present
        if (request.SubjectType == AccessSubjectType.TelegramUser
            && long.TryParse(request.SubjectId, out var telegramId))
        {
            var stubExists = await _db.Users.AnyAsync(u => u.TelegramId == telegramId, ct);
            if (!stubExists)
            {
                _db.Users.Add(new User
                {
                    Id = Guid.NewGuid(),
                    TelegramId = telegramId,
                    IsStubAccount = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("access-requests/{requestId:guid}/deny")]
    public async Task<IActionResult> DenyRequest(Guid botId, Guid requestId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (!await IsOwnerAsync(botId, ct)) return NotFound();

        var request = await _db.BotAccessRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.BotId == botId
                                      && r.Status == AccessRequestStatus.Pending, ct);
        if (request is null) return NotFound();

        request.Status = AccessRequestStatus.Denied;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = userId;

        var existing = await _db.BotAccessLists.FirstOrDefaultAsync(
            a => a.BotId == botId && a.SubjectType == request.SubjectType
                                  && a.SubjectId == request.SubjectId, ct);

        if (existing is null)
        {
            _db.BotAccessLists.Add(new BotAccessList
            {
                Id = Guid.NewGuid(),
                BotId = botId,
                SubjectType = request.SubjectType,
                SubjectId = request.SubjectId,
                Status = AccessStatus.Denied,
                AddedAt = DateTime.UtcNow
            });
        }
        else
        {
            existing.Status = AccessStatus.Denied;
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Access List ───────────────────────────────────────────────────────

    [HttpGet("access-list")]
    public async Task<IActionResult> GetAccessList(Guid botId, CancellationToken ct)
    {
        if (!await IsOwnerAsync(botId, ct)) return NotFound();

        var entries = await _db.BotAccessLists
            .Where(a => a.BotId == botId)
            .OrderBy(a => a.AddedAt)
            .ToListAsync(ct);

        return Ok(entries.Select(ToListResponse));
    }

    [HttpDelete("access-list/{entryId:guid}")]
    public async Task<IActionResult> DeleteAccessListEntry(Guid botId, Guid entryId, CancellationToken ct)
    {
        if (!await IsOwnerAsync(botId, ct)) return NotFound();

        var entry = await _db.BotAccessLists
            .FirstOrDefaultAsync(a => a.Id == entryId && a.BotId == botId, ct);
        if (entry is null) return NotFound();

        _db.BotAccessLists.Remove(entry);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<bool> IsOwnerAsync(Guid botId, CancellationToken ct)
    {
        var userId = GetUserId();
        return await _db.Bots.AnyAsync(b => b.Id == botId && b.OwnerId == userId, ct);
    }

    private static AccessRequestResponse ToRequestResponse(BotAccessRequest r) => new(
        r.Id, r.BotId, r.SubjectType.ToString(), r.SubjectId,
        r.DisplayName, r.RequestedAt, r.Status.ToString());

    private static AccessListEntryResponse ToListResponse(BotAccessList a) => new(
        a.Id, a.BotId, a.SubjectType.ToString(), a.SubjectId,
        a.Status.ToString(), a.AddedAt);
}
