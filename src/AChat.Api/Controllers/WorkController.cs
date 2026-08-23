using AChat.Api.Dtos;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

/// <summary>
/// The durable background-work queue (§12b). Admins can see what work is pending,
/// running, or dead-lettered, retry a failed item, or discard one. System admins
/// only — the queue spans every bot and user.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/work")]
public class WorkController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// The queue, newest first. Each item carries its shaped last failure and the
    /// bot it belongs to (via the target conversation).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkItemDto>>> List()
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        var items = await db.WorkItems
            .AsNoTracking()
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();

        var convIds = items.Select(i => i.ConversationId).Distinct().ToList();
        var convs = await db.Conversations
            .AsNoTracking()
            .Where(c => convIds.Contains(c.Id))
            .Select(c => new { c.Id, c.BotId, c.Bot!.Name })
            .ToDictionaryAsync(c => c.Id);

        return Ok(items.Select(w => new WorkItemDto
        {
            Id = w.Id,
            Kind = w.Kind.ToString(),
            ConversationId = w.ConversationId,
            Status = w.Status.ToString(),
            Attempts = w.Attempts,
            MaxAttempts = w.MaxAttempts,
            NextTryAt = w.NextTryAt,
            LastFailure = w.LastFailure,
            CreatedAt = w.CreatedAt,
            UpdatedAt = w.UpdatedAt,
            BotId = convs.TryGetValue(w.ConversationId, out var c) ? c.BotId : 0,
            BotName = convs.TryGetValue(w.ConversationId, out var c2) ? c2.Name : "(conversation gone)",
        }).ToList());
    }

    /// <summary>
    /// Re-arm a <c>Dead</c> (or overdue <c>Pending</c>) item: attempts reset, due
    /// now. A running item can't be re-armed — it's in flight.
    /// </summary>
    [HttpPost("{id:int}/retry")]
    public async Task<ActionResult<WorkItemDto>> Retry(int id)
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id) ?? throw new KeyNotFoundException();
        if (item.Status == WorkStatus.Running)
            return Conflict(new { message = "This item is running right now — wait for it to finish." });

        var now = DateTime.UtcNow;
        item.Status = WorkStatus.Pending;
        item.Attempts = 0;
        item.LastFailure = null;
        item.NextTryAt = now;
        item.UpdatedAt = now;
        await db.SaveChangesAsync();

        var conv = await db.Conversations
            .AsNoTracking()
            .Include(c => c.Bot)
            .FirstOrDefaultAsync(c => c.Id == item.ConversationId);
        return Ok(new WorkItemDto
        {
            Id = item.Id,
            Kind = item.Kind.ToString(),
            ConversationId = item.ConversationId,
            Status = item.Status.ToString(),
            Attempts = item.Attempts,
            MaxAttempts = item.MaxAttempts,
            NextTryAt = item.NextTryAt,
            LastFailure = item.LastFailure,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            BotId = conv?.BotId ?? 0,
            BotName = conv?.Bot?.Name ?? "(conversation gone)",
        });
    }

    /// <summary>
    /// Discard an item: the row is removed and the producer may re-arm it on the
    /// next sweep if its trigger still holds (§12b). A running item can't be
    /// discarded mid-flight.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Discard(int id)
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id) ?? throw new KeyNotFoundException();
        if (item.Status == WorkStatus.Running)
            return Conflict(new { message = "This item is running right now — wait for it to finish." });

        db.WorkItems.Remove(item);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
