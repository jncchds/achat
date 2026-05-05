using System.Security.Claims;
using AChat.Core.DTOs.Auth;
using AChat.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService authService) : ControllerBase
{
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await authService.LoginAsync(request.Username, request.Password, ct);
        if (result is null) return Unauthorized(new { error = "Invalid username or password" });
        return Ok(result);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = GetUserId();
        var result = await authService.GetCurrentUserAsync(userId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPut("me")]
    [Authorize]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        var success = await authService.UpdateProfileAsync(userId, request, ct);
        return success ? NoContent() : BadRequest(new { error = "Failed to update profile. Check your current password." });
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
