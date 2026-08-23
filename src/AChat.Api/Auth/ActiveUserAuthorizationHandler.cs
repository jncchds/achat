using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Auth;

/// <summary>
/// Authorization requirement: the signed-in user must have an active account
/// (§7.4). A disabled user is refused at login, but a token already issued stays
/// valid until it expires — this requirement enforces the disablement on every
/// authorized request so "disable" takes effect immediately, not after re-login.
/// </summary>
public sealed class ActiveUserRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Loads the user from the <c>sub</c> claim and checks <see cref="User.IsActive"/>.
/// A missing or disabled user fails the requirement (→ 403).
/// </summary>
public sealed class ActiveUserAuthorizationHandler(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<ActiveUserAuthorizationHandler> log)
    : AuthorizationHandler<ActiveUserRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ActiveUserRequirement requirement)
    {
        var sub = context.User.FindFirst("sub")?.Value;
        if (!int.TryParse(sub, out var id))
        {
            log.LogWarning("ActiveUser check: no usable sub claim (sub={Sub})", sub);
            return;
        }

        bool active;
        try
        {
            using var db = dbFactory.CreateDbContext();
            active = await db.Users.AsNoTracking()
                .Where(u => u.Id == id)
                .Select(u => u.IsActive)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            // A handler that throws fails closed (403). Log so it is diagnosable.
            log.LogError(ex, "ActiveUser check failed for user {Id} — failing closed", id);
            return;
        }

        log.LogDebug("ActiveUser check: user {Id} IsActive={Active}", id, active);
        if (active)
            context.Succeed(requirement);
        // else: requirement not met → 403 Forbidden.
    }
}
