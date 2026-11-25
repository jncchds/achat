using AChat.Api.Controllers;
using AChat.Api.Dtos;
using AChat.Api.Services;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace AChat.Tests;

/// <summary>Trait list guardrails (pure) and the apply / version / revert round trip (§2.4, §4.7).</summary>
public class TraitTests
{
    private static readonly TraitLimits Lim = new(3, 20, 200, 2000);
    private static TraitItem T(int id, string text = "x") => new(id, text);
    private static TraitChanges Add(params string[] a) => new(a, []);
    private static TraitChanges Retire(params int[] r) => new([], r);

    [Fact]
    public void Adds_and_retires_apply_and_spend_the_daily_budget()
    {
        var plan = PersonaTraits.Plan(new TraitChanges(["new one"], [1]), [T(1, "old")], 0, true, false, Lim);
        Assert.Equal(["new one"], plan.Add);
        Assert.Equal([1], plan.Retire);
    }

    [Fact]
    public void Daily_cap_counts_changes_already_made_and_drops_the_rest()
    {
        var plan = PersonaTraits.Plan(Add("a", "b", "c"), [], 1, true, false, Lim); // 2 left of 3
        Assert.Equal(["a", "b"], plan.Add);
        Assert.Contains(plan.Notes, n => n.StartsWith("TraitAddDropped: daily limit"));
        Assert.True(PersonaTraits.Plan(Add("a"), [], 3, true, false, Lim).IsEmpty);
    }

    [Fact]
    public void Non_creator_evolution_off_and_cooldown_drop_everything()
    {
        Assert.True(PersonaTraits.Plan(Add("a"), [], 0, false, false, Lim).IsEmpty);
        var cd = PersonaTraits.Plan(Add("a"), [T(1)], 0, true, true, Lim);
        Assert.True(cd.IsEmpty);
        Assert.Contains(cd.Notes, n => n.Contains("cooldown"));
    }

    [Fact]
    public void Unknown_or_inactive_ids_are_ignored_without_spending_budget()
    {
        var plan = PersonaTraits.Plan(Retire(99, 1), [T(1)], 0, true, false, Lim);
        Assert.Equal([1], plan.Retire);
        Assert.Contains(plan.Notes, n => n.StartsWith("TraitRetireIgnored"));
    }

    [Fact]
    public void Count_and_size_caps_refuse_adds_until_something_is_retired()
    {
        var full = Enumerable.Range(1, 20).Select(i => T(i)).ToList();
        Assert.True(PersonaTraits.Plan(Add("one more"), full, 0, true, false, Lim).IsEmpty);
        // retiring one in the same turn frees the slot
        Assert.Equal(["one more"], PersonaTraits.Plan(new TraitChanges(["one more"], [1]), full, 0, true, false, Lim).Add);

        var big = new[] { T(1, new string('a', 1900)) };
        Assert.True(PersonaTraits.Plan(Add(new string('b', 150)), big, 0, true, false, Lim).IsEmpty);
    }

    [Fact]
    public void Long_and_duplicate_adds_are_clipped_or_skipped()
    {
        var plan = PersonaTraits.Plan(Add(new string('z', 500), "Already There"), [T(1, "already there")], 0, true, false, Lim);
        Assert.Single(plan.Add);
        Assert.Equal(200, plan.Add[0].Length);
    }

    [Fact]
    public void Consolidation_result_must_be_a_non_empty_json_array_of_strings()
    {
        Assert.Equal(["a", "b"], PersonaTraits.ParseConsolidation("```json\n[\"a\", \" b \", \"A\", \"\"]\n```", 200));
        Assert.Null(PersonaTraits.ParseConsolidation("[]", 200));
        Assert.Null(PersonaTraits.ParseConsolidation("[\"\", \"  \"]", 200));
        Assert.Null(PersonaTraits.ParseConsolidation("[1, 2]", 200));
        Assert.Null(PersonaTraits.ParseConsolidation("Here you go: not json", 200));
        Assert.Null(PersonaTraits.ParseConsolidation(null, 200));
    }

    // ── DB-backed ───────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        public AppDbContext Db { get; }
        public Bot Bot { get; }
        public Fixture()
        {
            Db = new TestAppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Db.Users.Add(new User { Id = 1, Username = "o", DisplayName = "O", PasswordHash = "x", IsActive = true, CreatedAt = DateTime.UtcNow });
            Db.LlmPresets.Add(new LlmPreset { Id = 1, OwnerId = 1, Name = "P", Provider = LlmProvider.OpenAICompatible, BaseEndpoint = "http://x", GenerationModel = "m", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Bot = new Bot { Id = 9, OwnerId = 1, Name = "B", CorePersona = "c", InviteCode = "c", PresetId = 1, EvolutionEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Bots.Add(Bot);
            Db.PersonaVersions.Add(new PersonaVersion { BotId = 9, Content = "", Snapshot = "[]", Source = PersonaVersionSource.Initial, CreatedAt = DateTime.UtcNow.AddDays(-3) });
            Db.SaveChanges();
        }
        public Task<(PersonaTraits.Summary?, PersonaVersion?)> BotTurn(TraitChanges c, int userId = 1, List<string>? notes = null) =>
            PersonaTraits.ApplyBotChangesAsync(Db, Bot, userId, c, 100, Lim, 12, notes ?? []);
        public BotsController Owner()
        {
            var c = new BotsController(Db);
            c.ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "1")], "test")) },
            };
            return c;
        }
    }

    [Fact]
    public async Task Bot_turn_adds_a_trait_and_a_BotTurn_version_and_a_second_user_changes_nothing()
    {
        var f = new Fixture();
        var (summary, version) = await f.BotTurn(Add("teases them about cooking"));
        Assert.Equal(["teases them about cooking"], summary!.Added);
        Assert.Equal(PersonaVersionSource.BotTurn, version!.Source);
        var row = await f.Db.BotTraits.SingleAsync();
        Assert.Equal(100, row.SourceMessageId);
        Assert.Contains(row.Text, version.Content);
        Assert.Equal([row.Id], PersonaTraits.ItemsOf(version).Select(i => i.Id));

        var notes = new List<string>();
        var (other, v2) = await f.BotTurn(Add("sneaky"), userId: 2, notes);
        Assert.Null(other); Assert.Null(v2);
        Assert.Single(await f.Db.BotTraits.ToListAsync());
        Assert.Contains(notes, n => n.StartsWith("TraitsIgnored"));
    }

    [Fact]
    public async Task Human_edit_arms_the_cooldown_and_revert_restores_the_snapshot()
    {
        var f = new Fixture();
        await f.BotTurn(Add("first"));
        var botVersion = await f.Db.PersonaVersions.OrderBy(v => v.Id).LastAsync();

        var owner = f.Owner();
        Assert.IsType<OkObjectResult>((await owner.AddTrait(9, new TraitRequest { Text = "human added" })).Result);
        Assert.Equal(PersonaVersionSource.UserEdited, (await f.Db.PersonaVersions.OrderBy(v => v.Id).LastAsync()).Source);

        // the correction must not be overwritten next turn
        var notes = new List<string>();
        var (s, _) = await f.BotTurn(Add("bot again"), 1, notes);
        Assert.Null(s);
        Assert.Contains(notes, n => n.Contains("cooldown"));

        var first = await f.Db.BotTraits.FirstAsync(t => t.Text == "first");
        Assert.IsType<OkObjectResult>((await owner.RetireTrait(9, first.Id)).Result);
        Assert.Equal(["human added"], (await PersonaTraits.ActiveAsync(f.Db, 9)).Select(i => i.Text));

        // revert to the bot-turn version: the original item returns with its id, the human one is retired
        var before = await f.Db.PersonaVersions.CountAsync();
        Assert.IsType<OkObjectResult>((await owner.RevertPersona(9, botVersion.Id)).Result);
        var active = await PersonaTraits.ActiveAsync(f.Db, 9);
        Assert.Equal([new TraitItem(first.Id, "first")], active);
        Assert.Equal(before + 1, await f.Db.PersonaVersions.CountAsync());
        Assert.Equal(PersonaVersionSource.Revert, (await f.Db.PersonaVersions.OrderBy(v => v.Id).LastAsync()).Source);
    }

    [Fact]
    public async Task Bot_retire_works_by_id_and_counts_toward_the_daily_cap()
    {
        var f = new Fixture();
        await f.BotTurn(Add("a", "b"));
        var a = await f.Db.BotTraits.FirstAsync(t => t.Text == "a");
        var (s, _) = await f.BotTurn(new TraitChanges(["c", "d"], [a.Id])); // 2 used; the retire takes the last one
        Assert.Equal(["a"], s!.Retired);
        Assert.Empty(s.Added);
        Assert.Equal(["b"], (await PersonaTraits.ActiveAsync(f.Db, 9)).Select(i => i.Text));
        Assert.Equal(3, await PersonaTraits.BotChangesSinceAsync(f.Db, 9, DateTime.UtcNow.AddHours(-24)));
    }

    [Fact]
    public async Task Legacy_prose_becomes_one_item_on_first_read()
    {
        var f = new Fixture();
        f.Db.PersonaVersions.Add(new PersonaVersion { BotId = 9, Content = "You share a running joke.", Source = PersonaVersionSource.Evolution, CreatedAt = DateTime.UtcNow.AddDays(-1) });
        await f.Db.SaveChangesAsync();
        var items = await PersonaTraits.ActiveAsync(f.Db, 9);
        Assert.Equal("You share a running joke.", Assert.Single(items).Text);
        Assert.Single(await f.Db.BotTraits.ToListAsync());
        // idempotent
        await PersonaTraits.ActiveAsync(f.Db, 9);
        Assert.Single(await f.Db.BotTraits.ToListAsync());
    }
}
