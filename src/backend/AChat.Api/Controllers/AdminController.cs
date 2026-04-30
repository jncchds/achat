using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AChat.Api.Models.Admin;
using AChat.Core.Entities;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = "AdminOnly")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("users")]
    public async Task<ActionResult<List<UserResponse>>> GetUsers(CancellationToken ct)
    {
        var users = await _db.Users
            .Where(u => !u.IsStubAccount)
            .OrderBy(u => u.CreatedAt)
            .Select(u => new UserResponse(u.Id, u.Email, u.IsAdmin, u.CreatedAt))
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPost("users")]
    public async Task<ActionResult<UserResponse>> CreateUser(CreateUserRequest req, CancellationToken ct)
    {
        if (await _db.Users.AnyAsync(u => u.Email == req.Email, ct))
            return Conflict("Email already in use.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = req.Email,
            PasswordHash = HashPassword(req.Password),
            IsAdmin = false,
            IsStubAccount = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetUsers), new UserResponse(user.Id, user.Email, user.IsAdmin, user.CreatedAt));
    }

    [HttpDelete("users/{id:guid}")]
    public async Task<IActionResult> DeleteUser(Guid id, CancellationToken ct)
    {
        var currentUserId = Guid.Parse(
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")!);

        if (id == currentUserId)
            return BadRequest("Cannot delete your own account.");

        var user = await _db.Users.FindAsync([id], ct);
        if (user is null) return NotFound();

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, 350_000,
            HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
}
