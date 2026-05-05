using System.Security.Claims;
using AChat.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/llm-usage")]
[Authorize]
public class LlmUsageController(ILlmUsageService usageService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetMyUsage(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await usageService.GetUserUsageAsync(GetUserId(), page, Math.Min(pageSize, 100), ct));

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
