using AChat.Api.Auth;
using AChat.Api.Controllers;
using AChat.Api.Dtos;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Xunit;

namespace AChat.Tests;

/// <summary>
/// Phase 8 (operational maturity) against an in-memory database: user management
/// (§7.4) — disable/refuse, reset, delete guards; and the bot usage controls
/// (§12a, §2.5) — kill switch, quota, usage breakdown. Assertions are on database
/// state and the concrete result type, not <c>ActionResult.Value</c> (unreliable in
/// the test host — see SharingTests).
/// </summary>
public class Phase8Tests
{
    private sealed class Fixture
    {
        public AppDbContext Db { get; }
        public int AdminId { get; } = 1;
        public int UserId { get; } = 2;

        public Fixture()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = new TestAppDbContext(options);

            Db.Users.Add(new User { Id = AdminId, Username = "admin", DisplayName = "Admin", PasswordHash = "x", IsAdmin = true, IsActive = true, CreatedAt = DateTime.UtcNow });
            Db.Users.Add(new User { Id = UserId, Username = "alice", DisplayName = "Alice", PasswordHash = "x", IsActive = true, CreatedAt = DateTime.UtcNow });
            Db.LlmPresets.Add(new LlmPreset
            {
                Id = 1, OwnerId = AdminId, Name = "Preset", Provider = LlmProvider.OpenAICompatible,
                BaseEndpoint = "http://localhost", GenerationModel = "m", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
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
                    new[] { new Claim("sub", userId.ToString()), new Claim(ClaimTypes.Role, "admin") }, "test")),
            },
        };
        return controller;
    }

    private static JwtTokenService Tokens() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "test-signing-key-0123456789abcdef0123456789abcdef",
            ["Jwt:ExpiryHours"] = "168",
        }).Build());

    // ── User management (§7.4) ─────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_user_is_refused_at_login_with_403()
    {
        var f = new Fixture();
        var hasher = new PasswordHasher();
        var u = await f.Db.Users.FirstAsync(x => x.Id == f.UserId);
        u.PasswordHash = hasher.Hash("password123");
        u.IsActive = false;
        await f.Db.SaveChangesAsync();

        var res = await new AuthController(f.Db, hasher, Tokens())
            .Login(new LoginRequest("alice", "password123"));

        // StatusCode(403, body) is an ObjectResult carrying the 403 (a disabled
        // account is a distinct 403, not a 401 "wrong password").
        var obj = Assert.IsType<ObjectResult>(res.Result);
        Assert.Equal(403, obj.StatusCode);
    }

    [Fact]
    public async Task Active_user_can_login()
    {
        var f = new Fixture();
        var hasher = new PasswordHasher();
        var u = await f.Db.Users.FirstAsync(x => x.Id == f.UserId);
        u.PasswordHash = hasher.Hash("password123");
        await f.Db.SaveChangesAsync();

        var res = await new AuthController(f.Db, hasher, Tokens())
            .Login(new LoginRequest("alice", "password123"));

        Assert.IsType<OkObjectResult>(res.Result);
    }

    [Fact]
    public async Task Admin_can_disable_and_reenable_another_user()
    {
        var f = new Fixture();
        var admin = WithUser(new AdminController(f.Db, new PasswordHasher()), f.AdminId);

        await admin.SetActive(f.UserId, new SetActiveRequest { IsActive = false });
        Assert.False((await f.Db.Users.FirstAsync(x => x.Id == f.UserId)).IsActive);

        await admin.SetActive(f.UserId, new SetActiveRequest { IsActive = true });
        Assert.True((await f.Db.Users.FirstAsync(x => x.Id == f.UserId)).IsActive);
    }

    [Fact]
    public async Task Admin_cannot_disable_or_delete_themselves()
    {
        var f = new Fixture();
        var admin = WithUser(new AdminController(f.Db, new PasswordHasher()), f.AdminId);

        var dis = await admin.SetActive(f.AdminId, new SetActiveRequest { IsActive = false });
        Assert.IsType<BadRequestObjectResult>(dis.Result);
        Assert.True((await f.Db.Users.FirstAsync(x => x.Id == f.AdminId)).IsActive);

        var del = await admin.DeleteUser(f.AdminId);
        Assert.IsType<BadRequestObjectResult>(del);
        Assert.True(await f.Db.Users.AnyAsync(x => x.Id == f.AdminId));
    }

    [Fact]
    public async Task Delete_user_who_owns_bots_is_blocked()
    {
        var f = new Fixture();
        // Give alice a bot.
        f.Db.Bots.Add(new Bot
        {
            Id = 9, OwnerId = f.UserId, Name = "AliceBot", CorePersona = "x",
            InviteCode = "c", PresetId = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();

        var admin = WithUser(new AdminController(f.Db, new PasswordHasher()), f.AdminId);
        var res = await admin.DeleteUser(f.UserId);
        Assert.IsType<ConflictObjectResult>(res);
        Assert.True(await f.Db.Users.AnyAsync(x => x.Id == f.UserId));
    }

    [Fact]
    public async Task Delete_user_with_interactions_is_blocked()
    {
        var f = new Fixture();
        f.Db.LlmInteractions.Add(new LlmInteraction
        {
            Id = 1, BotId = null, UserId = f.UserId, Purpose = InteractionPurpose.Chat,
            Model = "m", Endpoint = "e", RequestBody = "[]", Failed = false, CreatedAt = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();

        var admin = WithUser(new AdminController(f.Db, new PasswordHasher()), f.AdminId);
        var res = await admin.DeleteUser(f.UserId);
        Assert.IsType<ConflictObjectResult>(res);
        Assert.True(await f.Db.Users.AnyAsync(x => x.Id == f.UserId));
    }

    [Fact]
    public async Task Delete_user_without_bots_or_interactions_succeeds()
    {
        var f = new Fixture();
        var admin = WithUser(new AdminController(f.Db, new PasswordHasher()), f.AdminId);
        var res = await admin.DeleteUser(f.UserId);
        Assert.IsType<NoContentResult>(res);
        Assert.False(await f.Db.Users.AnyAsync(x => x.Id == f.UserId));
    }

    [Fact]
    public async Task Reset_password_allows_login_with_the_new_password()
    {
        var f = new Fixture();
        var hasher = new PasswordHasher();
        var admin = WithUser(new AdminController(f.Db, hasher), f.AdminId);

        await admin.ResetPassword(f.UserId, new ResetPasswordRequest { Password = "newpassword123" });

        var res = await new AuthController(f.Db, hasher, Tokens())
            .Login(new LoginRequest("alice", "newpassword123"));
        Assert.IsType<OkObjectResult>(res.Result);
    }

    // ── Usage controls (§12a, §2.5) ───────────────────────────────────────────

    [Fact]
    public async Task Owner_can_set_suspend_and_quota()
    {
        var f = new Fixture();
        f.Db.Bots.Add(new Bot
        {
            Id = 9, OwnerId = f.AdminId, Name = "Bot", CorePersona = "x",
            InviteCode = "c", PresetId = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();

        var owner = WithUser(new BotsController(f.Db), f.AdminId);
        await owner.UpdateSettings(9, new BotSettingsRequest { Suspended = true, MaxInteractions = 50 });

        var bot = await f.Db.Bots.FirstAsync(b => b.Id == 9);
        Assert.True(bot.Suspended);
        Assert.Equal(50, bot.MaxInteractions);

        // 0 = unlimited → stored as null.
        await owner.UpdateSettings(9, new BotSettingsRequest { Suspended = false, MaxInteractions = 0 });
        bot = await f.Db.Bots.FirstAsync(b => b.Id == 9);
        Assert.False(bot.Suspended);
        Assert.Null(bot.MaxInteractions);
    }

    [Fact]
    public async Task Invitee_cannot_set_usage_controls()
    {
        var f = new Fixture();
        f.Db.Bots.Add(new Bot
        {
            Id = 9, OwnerId = f.AdminId, Name = "Bot", CorePersona = "x",
            InviteCode = "c", PresetId = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await f.Db.SaveChangesAsync();

        var invitee = WithUser(new BotsController(f.Db), f.UserId);
        var res = await invitee.UpdateSettings(9, new BotSettingsRequest { Suspended = true });
        Assert.IsType<NotFoundResult>(res.Result); // not the owner
        Assert.False((await f.Db.Bots.FirstAsync(b => b.Id == 9)).Suspended);
    }

    [Fact]
    public async Task Usage_endpoint_reports_totals_and_per_user_breakdown()
    {
        var f = new Fixture();
        f.Db.Bots.Add(new Bot
        {
            Id = 9, OwnerId = f.AdminId, Name = "Bot", CorePersona = "x",
            InviteCode = "c", PresetId = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Suspended = false, MaxInteractions = 100,
        });
        // Two interactions from the owner, one from alice.
        f.Db.LlmInteractions.Add(new LlmInteraction { Id = 1, BotId = 9, UserId = f.AdminId, Purpose = InteractionPurpose.Chat, Model = "m", Endpoint = "e", RequestBody = "[]", PromptTokens = 10, CompletionTokens = 20, TotalTokens = 30, Failed = false, CreatedAt = DateTime.UtcNow });
        f.Db.LlmInteractions.Add(new LlmInteraction { Id = 2, BotId = 9, UserId = f.AdminId, Purpose = InteractionPurpose.Chat, Model = "m", Endpoint = "e", RequestBody = "[]", PromptTokens = 5, CompletionTokens = 5, TotalTokens = 10, Failed = false, CreatedAt = DateTime.UtcNow });
        f.Db.LlmInteractions.Add(new LlmInteraction { Id = 3, BotId = 9, UserId = f.UserId, Purpose = InteractionPurpose.Chat, Model = "m", Endpoint = "e", RequestBody = "[]", PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2, Failed = false, CreatedAt = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();

        var owner = WithUser(new BotsController(f.Db), f.AdminId);
        var res = await owner.Usage(9);
        var ok = Assert.IsType<OkObjectResult>(res.Result);
        var dto = Assert.IsType<BotUsageDto>(ok.Value);

        Assert.Equal(3, dto.Interactions);
        Assert.Equal(16, dto.PromptTokens);
        Assert.Equal(26, dto.CompletionTokens);
        Assert.Equal(42, dto.TotalTokens);
        Assert.Equal(100, dto.MaxInteractions);
        Assert.Equal(2, dto.ByUser.Count);
        Assert.Equal(2, dto.ByUser.First(u => u.UserId == f.AdminId).Interactions);
        Assert.Equal(1, dto.ByUser.First(u => u.UserId == f.UserId).Interactions);
        Assert.True(dto.ByUser.First(u => u.UserId == f.AdminId).IsOwner);
    }
}
