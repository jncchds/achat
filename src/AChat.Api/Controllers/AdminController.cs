using AChat.Api.Dtos;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

/// <summary>System administration — user listing (§7.4, Phase 8). Admins only.</summary>
[ApiController]
[Authorize]
[Route("api/admin")]
public class AdminController(AppDbContext db) : ControllerBase
{
    [HttpGet("users")]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> Users()
    {
        if (!User.IsInRole("admin"))
            return Forbid();

        var users = await db.Users.OrderBy(u => u.Username).ToListAsync();
        return Ok(users.Select(UserDto.From).ToList());
    }
}
