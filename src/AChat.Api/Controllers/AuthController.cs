using AChat.Api.Auth;
using AChat.Api.Dtos;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(AppDbContext db, PasswordHasher hasher, JwtTokenService tokens) : ControllerBase
{
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { message = "Username and password are required." });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username.Trim());
        if (user is null || !hasher.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new { message = "Invalid username or password." });

        // A disabled account is refused outright (§7.4) — a distinct 403 so the UI
        // can say "this account is disabled" rather than "wrong password".
        if (!user.IsActive)
            return StatusCode(403, new { message = "This account has been disabled by an administrator." });

        user.LastLoginAt = DateTime.UtcNow;
        if (TryValidTimezone(req.Timezone, out var tz))
            user.Timezone = tz;
        await db.SaveChangesAsync();

        return Ok(new LoginResponse(tokens.CreateToken(user), UserDto.From(user)));
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me()
    {
        var sub = User.FindFirst("sub")?.Value;
        if (int.TryParse(sub, out var id))
        {
            var user = await db.Users.FindAsync(id);
            if (user is not null) return Ok(UserDto.From(user));
        }
        return Unauthorized();
    }

    [Authorize]
    [HttpPost("logout")]
    public OkResult Logout() => Ok();

    /// <summary>
    /// Update the current user's IANA timezone (§5.9) — the "manual override" half
    /// of timezone capture (browser auto-capture happens at login).
    /// </summary>
    [Authorize]
    [HttpPatch("me/timezone")]
    public async Task<ActionResult<UserDto>> SetTimezone([FromBody] TimezoneRequest req)
    {
        var sub = User.FindFirst("sub")?.Value;
        if (!int.TryParse(sub, out var id))
            return Unauthorized();

        if (!TryValidTimezone(req.Timezone, out var tz))
            return BadRequest(new { message = $"\"{req.Timezone}\" is not a recognised IANA timezone (e.g. \"Europe/Moscow\")." });

        var user = await db.Users.FindAsync(id);
        if (user is null) return Unauthorized();

        user.Timezone = tz;
        await db.SaveChangesAsync();
        return Ok(UserDto.From(user));
    }

    private static bool TryValidTimezone(string? name, out string? timezone)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(name.Trim());
                timezone = name.Trim();
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
                // fall through
            }
        }
        timezone = null;
        return false;
    }
}
