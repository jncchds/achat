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

    /// <summary>Current user's Id (from the <c>sub</c> claim).</summary>
    private int _SelfId => int.TryParse(User.FindFirst("sub")?.Value, out var id) ? id : -1;

    /// <summary>
    /// Enable or disable an account (§7.4). Disabling is refused at login and on
    /// every authorized request (immediate). Disabling yourself is blocked to
    /// prevent self-lockout.
    /// </summary>
    [HttpPatch("users/{id}")]
    public async Task<ActionResult<UserDto>> SetActive(int id, [FromBody] SetActiveRequest req)
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();
        if (!req.IsActive && id == _SelfId)
            return BadRequest(new { message = "You cannot disable your own account." });

        user.IsActive = req.IsActive;
        await db.SaveChangesAsync();
        return Ok(UserDto.From(user));
    }

    /// <summary>Reset a user's password (§7.4). Works for yourself too.</summary>
    [HttpPost("users/{id}/reset-password")]
    public async Task<ActionResult<UserDto>> ResetPassword(int id, [FromBody] ResetPasswordRequest req)
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 4)
            return BadRequest(new { message = "Password must be at least 4 characters." });

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        user.PasswordHash = hasher.Hash(req.Password);
        await db.SaveChangesAsync();
        return Ok(UserDto.From(user));
    }

    /// <summary>
    /// Delete a user (§7.4). Blocked when the user owns bots or has logged LLM
    /// interactions (the audit log references them) — disable such a user instead.
    /// Deleting yourself is blocked to prevent self-lockout.
    /// </summary>
    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        if (id == _SelfId)
            return BadRequest(new { message = "You cannot delete your own account." });

        var user = await db.Users.FindAsync(id);
        if (user is null) return NotFound();

        var ownsBots = await db.Bots.AnyAsync(b => b.OwnerId == id);
        if (ownsBots)
            return Conflict(new { message = $"'{user.Username}' owns bots. Delete their bots first, or disable the account instead." });

        var hasInteractions = await db.LlmInteractions.AnyAsync(i => i.UserId == id);
        if (hasInteractions)
            return Conflict(new { message = $"'{user.Username}' has logged LLM interactions (the audit log references them). Disable the account instead, or prune the log first." });

        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return NoContent();
    }
}
