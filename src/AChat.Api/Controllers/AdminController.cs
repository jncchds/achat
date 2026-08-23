using AChat.Api.Auth;
using AChat.Api.Dtos;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

/// <summary>
/// System administration. User listing (§7.4) and user creation (§7.2 — no
/// self-service registration: an admin creates users, which Phase 6 needs to
/// introduce the second user; full user management is Phase 8). Admins only.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin")]
public class AdminController(AppDbContext db, PasswordHasher hasher) : ControllerBase
{
    [HttpGet("users")]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> Users()
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        var users = await db.Users.OrderBy(u => u.Username).ToListAsync();
        return Ok(users.Select(UserDto.From).ToList());
    }

    /// <summary>
    /// Create a user (§7.2). Username must be unique; the password is Argon2id-hashed.
    /// Created users are non-admins (the bootstrap admin is the only admin).
    /// </summary>
    [HttpPost("users")]
    public async Task<ActionResult<UserDto>> CreateUser(CreateUserRequest req)
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        var username = (req.Username ?? "").Trim();
        if (username.Length == 0)
            return BadRequest(new { message = "Username is required." });
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 4)
            return BadRequest(new { message = "Password must be at least 4 characters." });

        if (await db.Users.AnyAsync(u => u.Username == username))
            return Conflict(new { message = $"A user named '{username}' already exists." });

        var user = new User
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? username : req.DisplayName.Trim(),
            PasswordHash = hasher.Hash(req.Password),
            IsAdmin = false,
            CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Users), null, UserDto.From(user));
    }
}
