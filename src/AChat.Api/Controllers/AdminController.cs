using AChat.Core.DTOs.Users;
using AChat.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public class AdminController(IUserService userService, ILlmUsageService usageService) : ControllerBase
{
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(CancellationToken ct) =>
        Ok(await userService.GetAllUsersAsync(ct));

    [HttpGet("users/{id:guid}")]
    public async Task<IActionResult> GetUser(Guid id, CancellationToken ct)
    {
        var result = await userService.GetUserAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var result = await userService.CreateUserAsync(request, ct);
        return CreatedAtAction(nameof(GetUser), new { id = result.Id }, result);
    }

    [HttpPut("users/{id:guid}")]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var result = await userService.UpdateUserAsync(id, request, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken ct)
    {
        var success = await userService.DeleteUserAsync(id, ct);
        return success ? NoContent() : NotFound();
    }

    [HttpGet("llm-usage")]
    public async Task<IActionResult> GetAllUsage(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        Ok(await usageService.GetAllUsageAsync(page, Math.Min(pageSize, 100), ct));
}
