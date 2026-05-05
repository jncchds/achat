using System.Security.Claims;
using AChat.Core.DTOs.Bots;
using AChat.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/bots")]
[Authorize]
public class BotsController(IBotService botService, ILlmUsageService usageService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) =>
        Ok(await botService.GetUserBotsAsync(GetUserId(), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await botService.GetBotAsync(id, GetUserId(), ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBotRequest request, CancellationToken ct)
    {
        var result = await botService.CreateBotAsync(GetUserId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateBotRequest request, CancellationToken ct)
    {
        var result = await botService.UpdateBotAsync(id, GetUserId(), request, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var success = await botService.DeleteBotAsync(id, GetUserId(), ct);
        return success ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/personality")]
    public async Task<IActionResult> ReplacePersonality(Guid id, [FromBody] ReplacePersonalityRequest request, CancellationToken ct)
    {
        var success = await botService.ReplacePersonalityAsync(id, GetUserId(), request.Personality, ct);
        return success ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/nudge")]
    public async Task<IActionResult> Nudge(Guid id, [FromBody] NudgeRequest request, CancellationToken ct)
    {
        var success = await botService.NudgeEvolutionAsync(id, GetUserId(), request.Direction, ct);
        return success ? Accepted() : NotFound();
    }

    [HttpGet("{id:guid}/access-requests")]
    public async Task<IActionResult> GetAccessRequests(Guid id, CancellationToken ct) =>
        Ok(await botService.GetAccessRequestsAsync(id, GetUserId(), ct));

    [HttpPut("{id:guid}/access-requests/{requestId:guid}")]
    public async Task<IActionResult> RespondToAccessRequest(
        Guid id, Guid requestId, [FromBody] RespondToAccessRequest request, CancellationToken ct)
    {
        var success = await botService.RespondToAccessRequestAsync(id, requestId, GetUserId(), request.Approve, ct);
        return success ? NoContent() : NotFound();
    }

    [HttpPost("{id:guid}/request-access")]
    public async Task<IActionResult> RequestAccess(Guid id, CancellationToken ct)
    {
        var result = await botService.RequestAccessAsync(id, GetUserId(), ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("{id:guid}/evolution-history")]
    public async Task<IActionResult> GetEvolutionHistory(Guid id, CancellationToken ct) =>
        Ok(await botService.GetEvolutionHistoryAsync(id, GetUserId(), ct));

    [HttpGet("{id:guid}/usage")]
    public async Task<IActionResult> GetUsage(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await usageService.GetBotUsageForUserAsync(id, GetUserId(), page, Math.Min(pageSize, 100), ct));

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
