using AChat.Api.Controllers;
using AChat.Api.Dtos;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace AChat.Tests;

/// <summary>
/// A test stand-in for <see cref="AppDbContext"/> that ignores the pgvector column
/// (InMemory has no notion of the vector type). Only relationship logic is under test.
/// </summary>
internal sealed class TestAppDbContext : AppDbContext
{
    public TestAppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Memory>().Ignore(m => m.Embedding);
    }
}

/// <summary>
/// Sharing flow (§2.5) against an in-memory database: request → approve → access,
/// visible-vs-owned authorization, and the one-live-request-per-(bot,user) rule.
/// Assertions are on <c>ActionResult.Value</c> (null for a 4xx) and the resulting
/// database state — the real behaviour, not the transport status.
/// </summary>
public class SharingTests
{
    private sealed class Fixture
    {
        public AppDbContext Db { get; }
        public int OwnerId { get; } = 1;
        public int InviteeId { get; } = 2;
        public int BotId { get; } = 1;
        public string Code { get; } = "testinvitecode0001";

        public Fixture()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            Db = new TestAppDbContext(options);

            Db.Users.Add(new User { Id = OwnerId, Username = "owner", DisplayName = "Owner", PasswordHash = "x", IsAdmin = true, CreatedAt = DateTime.UtcNow });
            Db.Users.Add(new User { Id = InviteeId, Username = "invitee", DisplayName = "Invitee", PasswordHash = "x", CreatedAt = DateTime.UtcNow });
            // A bot always has a valid preset (required FK) — the Include in the
            // owner/visibility queries depends on it existing.
            Db.LlmPresets.Add(new LlmPreset
            {
                Id = 1,
                OwnerId = OwnerId,
                Name = "Preset",
                Provider = LlmProvider.OpenAICompatible,
                BaseEndpoint = "http://localhost",
                GenerationModel = "test-model",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            Db.Bots.Add(new Bot
            {
                Id = BotId,
                OwnerId = OwnerId,
                Name = "Mira",
                CorePersona = "A dry, warm friend.",
                InviteCode = Code,
                PresetId = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            Db.SaveChanges();
        }
    }

    private static T WithUser<T>(T controller, int userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("sub", userId.ToString()) }, "test")),
            },
        };
        return controller;
    }

    private static async Task RequestAccess(Fixture f, int userId, string code)
    {
        var c = WithUser(new AccessController(f.Db), userId);
        await c.RequestAccess(new AccessRequestByCode(code));
    }

    [Fact]
    public async Task Invitee_requests_by_code_creates_pending_request()
    {
        var f = new Fixture();
        await RequestAccess(f, f.InviteeId, f.Code);

        var row = await f.Db.AccessRequests.FirstOrDefaultAsync(r =>
            r.BotId == f.BotId && r.RequesterId == f.InviteeId);
        Assert.NotNull(row);
        Assert.Equal(AccessRequestStatus.Pending, row!.Status);
    }

    [Fact]
    public async Task Owner_cannot_request_their_own_bot()
    {
        var f = new Fixture();
        var c = WithUser(new AccessController(f.Db), f.OwnerId);
        var res = await c.RequestAccess(new AccessRequestByCode(f.Code));

        Assert.Null(res.Value); // 409 Conflict → no data
        Assert.False(await f.Db.AccessRequests.AnyAsync());
    }

    [Fact]
    public async Task Unknown_code_is_rejected()
    {
        var f = new Fixture();
        var c = WithUser(new AccessController(f.Db), f.InviteeId);
        var res = await c.RequestAccess(new AccessRequestByCode("nope"));

        Assert.Null(res.Value); // 404 → no data
        Assert.False(await f.Db.AccessRequests.AnyAsync());
    }

    [Fact]
    public async Task Approve_grants_access_and_invitee_sees_bot_as_shared()
    {
        var f = new Fixture();
        await RequestAccess(f, f.InviteeId, f.Code);
        int rid = await f.Db.AccessRequests.Where(r => r.BotId == f.BotId).Select(r => r.Id).FirstAsync();

        var approve = WithUser(new BotsController(f.Db), f.OwnerId);
        await approve.ApproveRequest(f.BotId, rid);

        // A grant now exists for the invitee.
        Assert.True(await f.Db.BotAccesses.AnyAsync(a => a.BotId == f.BotId && a.UserId == f.InviteeId));

        // The request is now marked approved.
        Assert.Equal(AccessRequestStatus.Approved,
            await f.Db.AccessRequests.Where(r => r.Id == rid).Select(r => r.Status).FirstAsync());

        // Visibility: the invitee can now see the bot (owner OR granted), and it is not theirs.
        var visible = await f.Db.Bots.AnyAsync(b => b.Id == f.BotId
            && (b.OwnerId == f.InviteeId || f.Db.BotAccesses.Any(a => a.BotId == b.Id && a.UserId == f.InviteeId)));
        Assert.True(visible);
        Assert.NotEqual(f.InviteeId,
            await f.Db.Bots.Where(b => b.Id == f.BotId).Select(b => b.OwnerId).FirstAsync());
    }

    [Fact]
    public async Task Invitee_cannot_edit_or_delete_a_shared_bot()
    {
        var f = new Fixture();
        f.Db.BotAccesses.Add(new BotAccess { BotId = f.BotId, UserId = f.InviteeId, Role = AccessRole.User, GrantedAt = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();

        var edit = WithUser(new BotsController(f.Db), f.InviteeId);
        var eres = await edit.Update(f.BotId, new BotRequest { Name = "hax", CorePersona = "x", PresetId = 1 });
        Assert.Null(eres.Value); // 404 → not the owner
        Assert.Equal("Mira", await f.Db.Bots.Where(b => b.Id == f.BotId).Select(b => b.Name).FirstAsync());

        var del = WithUser(new BotsController(f.Db), f.InviteeId);
        Assert.IsType<NotFoundResult>(await del.Delete(f.BotId));
        Assert.True(await f.Db.Bots.AnyAsync(b => b.Id == f.BotId)); // still there
    }

    [Fact]
    public async Task Rejected_invitee_can_re_request_reopening_the_request()
    {
        var f = new Fixture();
        await RequestAccess(f, f.InviteeId, f.Code);
        int rid = await f.Db.AccessRequests.Where(r => r.BotId == f.BotId).Select(r => r.Id).FirstAsync();

        var reject = WithUser(new BotsController(f.Db), f.OwnerId);
        await reject.RejectRequest(f.BotId, rid);
        Assert.Equal(AccessRequestStatus.Rejected,
            await f.Db.AccessRequests.Where(r => r.Id == rid).Select(r => r.Status).FirstAsync());

        // Re-request → the same row re-opens to Pending (one live request per pair).
        await RequestAccess(f, f.InviteeId, f.Code);
        var rows = await f.Db.AccessRequests
            .Where(r => r.BotId == f.BotId && r.RequesterId == f.InviteeId)
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal(AccessRequestStatus.Pending, rows[0].Status);
    }

    [Fact]
    public async Task Revoke_removes_the_grant()
    {
        var f = new Fixture();
        f.Db.BotAccesses.Add(new BotAccess { BotId = f.BotId, UserId = f.InviteeId, Role = AccessRole.User, GrantedAt = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();
        Assert.True(await f.Db.BotAccesses.AnyAsync(a => a.UserId == f.InviteeId));

        var revoke = WithUser(new BotsController(f.Db), f.OwnerId);
        Assert.IsType<NoContentResult>(await revoke.RevokeAccess(f.BotId, f.InviteeId));
        Assert.False(await f.Db.BotAccesses.AnyAsync(a => a.BotId == f.BotId && a.UserId == f.InviteeId));
    }

}
