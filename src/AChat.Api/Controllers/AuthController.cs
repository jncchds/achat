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

        user.LastLoginAt = DateTime.UtcNow;
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
}
