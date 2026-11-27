using AChat.Api.Controllers;
using AChat.Api.Dtos;
using AChat.Api.Services;
using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Xunit;

namespace AChat.Tests;

/// <summary>
/// Token-limit failures of trait consolidation: the input budgeter/chunker, think-stripping,
/// error classification, the human-edit caps, and the handler against a fake provider
/// (context error → shrink → success; truncation → actionable permanent failure).
/// </summary>
public class ConsolidationLimitsTests
{
    // ── pure helpers ────────────────────────────────────────────────────────

    [Fact]
    public void Context_length_errors_are_recognised_by_status_and_text()
    {
        Assert.True(TokenLimits.IsContextLengthError(new LlmProviderException("Provider returned 400", 400,
            "{\"error\":{\"message\":\"This model's maximum context length is 4096 tokens. However, your messages resulted in 9000 tokens.\",\"code\":\"context_length_exceeded\"}}")));
        Assert.True(TokenLimits.IsContextLengthError(new LlmProviderException("x", 400, "Trying to keep the first 5000 tokens when context the overflows. n_ctx: 4096")));
        Assert.True(TokenLimits.IsContextLengthError(new LlmProviderException("x", 413, "prompt is too long")));
        Assert.False(TokenLimits.IsContextLengthError(new LlmProviderException("x", 400, "invalid model")));
        Assert.False(TokenLimits.IsContextLengthError(new LlmProviderException("x", 500, "context length exceeded"))); // server error: retryable
        Assert.False(TokenLimits.IsContextLengthError(new LlmProviderException("Could not reach the provider")));
    }

    [Theory]
    [InlineData("<think>hmm [\"x\"]</think>[\"a\"]", "[\"a\"]")]
    [InlineData("pre <think>a</think> mid <think>b</think> post", "pre  mid  post")]
    [InlineData("reasoning without opener</think>[\"a\"]", "[\"a\"]")]
    [InlineData("[\"a\"]<think>cut off while thin", "[\"a\"]")]
    [InlineData("[\"a\"]", "[\"a\"]")]
    [InlineData(null, "")]
    public void Inline_thinking_is_stripped(string? input, string expected) =>
        Assert.Equal(expected, TokenLimits.StripThinking(input));

    [Fact]
    public void Chunking_keeps_order_and_respects_the_budget()
    {
        var items = Enumerable.Range(1, 10).Select(i => new string('x', 40)).ToList(); // 10 tokens each + 2 overhead
        var chunks = TokenLimits.Chunk(items, s => PromptAssembly.EstimateTokens(s), budgetTokens: 40);
        Assert.Equal(10, chunks.Sum(c => c.Count));
        Assert.All(chunks, c => Assert.InRange(c.Count, 1, 3)); // 3 × 12 = 36 ≤ 40
        Assert.Equal(4, chunks.Count);

        var one = TokenLimits.Chunk(items, s => PromptAssembly.EstimateTokens(s), budgetTokens: 10_000);
        Assert.Single(one);
        Assert.Empty(TokenLimits.Chunk(items, s => 1, budgetTokens: 0));
        // an oversized item gets a chunk of its own instead of vanishing
        var big = TokenLimits.Chunk(new[] { "a", new string('b', 4000), "c" }, s => PromptAssembly.EstimateTokens(s), 50);
        Assert.Equal(3, big.Count);
    }

    [Fact]
    public void Bot_memories_are_clipped_before_they_reach_the_embedding_call()
    {
        Assert.Equal("short fact", MemoryWriter.Clip("short fact"));
        var words = string.Join(' ', Enumerable.Repeat("word", 1000)); // 4999 chars
        var clipped = MemoryWriter.Clip(words);
        Assert.True(clipped.Length <= MemoryWriter.MaxContentChars + 1);
        Assert.EndsWith("word…", clipped); // cut at a word boundary
        Assert.Equal(MemoryWriter.MaxContentChars + 1, MemoryWriter.Clip(new string('x', 5000)).Length);
    }

    [Fact]
    public async Task Permanent_failures_dead_letter_on_the_first_attempt()
    {
        using var db = new TestAppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTime.UtcNow;
        var item = new WorkItem { Kind = WorkKind.Evolution, ConversationId = 1, Status = WorkStatus.Running, Attempts = 1, MaxAttempts = 5, NextTryAt = now, CreatedAt = now, UpdatedAt = now };
        db.WorkItems.Add(item);
        db.SaveChanges();
        await WorkQueue.RecordOutcomeAsync(db, item, WorkOutcome.FailPermanent("context too long"));
        Assert.Equal(WorkStatus.Dead, item.Status);
        Assert.Equal("context too long", item.LastFailure);

        var retry = new WorkItem { Kind = WorkKind.Summary, ConversationId = 1, Status = WorkStatus.Running, Attempts = 1, MaxAttempts = 5, NextTryAt = now, CreatedAt = now, UpdatedAt = now };
        db.WorkItems.Add(retry);
        db.SaveChanges();
        await WorkQueue.RecordOutcomeAsync(db, retry, WorkOutcome.Fail("flaky"));
        Assert.Equal(WorkStatus.Pending, retry.Status);
    }

    // ── human edit caps ─────────────────────────────────────────────────────

    private static readonly TraitLimits Lim = new(3, 3, 20, 50);

    private static List<TraitItem> Items(params string[] texts) => texts.Select((t, i) => new TraitItem(i + 1, t)).ToList();

    [Fact]
    public void Human_add_obeys_count_and_total_caps()
    {
        Assert.Null(PersonaTraits.CheckHumanEdit(Items("a", "b"), null, "c", Lim));
        Assert.Contains("full", PersonaTraits.CheckHumanEdit(Items("a", "b", "c"), null, "d", Lim));
        Assert.Contains("50 characters", PersonaTraits.CheckHumanEdit(Items(new string('x', 20), new string('y', 20)), null, new string('z', 15), Lim));
        Assert.Contains("at most 20", PersonaTraits.CheckHumanEdit(Items("a"), null, new string('x', 21), Lim));
        Assert.Contains("empty", PersonaTraits.CheckHumanEdit(Items("a"), null, "  ", Lim));
    }

    [Fact]
    public void Human_edit_may_shrink_a_list_that_is_already_over_a_cap()
    {
        var over = Items(new string('x', 20), new string('y', 20), new string('z', 20)); // 60 > 50
        Assert.Null(PersonaTraits.CheckHumanEdit(over, 1, "short", Lim));                // shrinks the total
        Assert.Null(PersonaTraits.CheckHumanEdit(over, 1, new string('q', 20), Lim));    // same size: allowed (not growing)
        Assert.Contains("50 characters", PersonaTraits.CheckHumanEdit(Items("a", new string('y', 20), new string('z', 20)), 1, new string('q', 15), Lim));
    }

    [Fact]
    public async Task Controller_returns_400_when_a_human_trait_breaks_a_cap()
    {
        var f = new Fixture(traitCount: 3);
        var cfg = Config(("Turn:MaxTraits", "3"), ("Turn:MaxTraitChars", "20"), ("Turn:MaxTraitsTotalChars", "1000"));
        var res = await f.Owner(cfg).AddTrait(9, new TraitRequest { Text = "one too many" });
        Assert.IsType<BadRequestObjectResult>(res.Result);
        Assert.Equal(3, (await PersonaTraits.ActiveAsync(f.Db, 9)).Count);

        var cfg2 = Config(("Turn:MaxTraits", "30"), ("Turn:MaxTraitChars", "200"), ("Turn:MaxTraitsTotalChars", "30"));
        var res2 = await f.Owner(cfg2).AddTrait(9, new TraitRequest { Text = new string('w', 25) });
        Assert.IsType<BadRequestObjectResult>(res2.Result);
        var id = (await PersonaTraits.ActiveAsync(f.Db, 9))[0].Id;
        var res3 = await f.Owner(cfg2).EditTrait(9, id, new TraitRequest { Text = new string('w', 90) });
        Assert.IsType<BadRequestObjectResult>(res3.Result);
        var ok = await f.Owner(cfg2).EditTrait(9, id, new TraitRequest { Text = "fine" });
        Assert.IsType<OkObjectResult>(ok.Result);
    }

    // ── the handler against a fake provider ─────────────────────────────────

    private static IConfiguration Config(params (string, string)[] extra)
    {
        var d = new Dictionary<string, string?>
        {
            ["Turn:ConsolidateAbove"] = "2",
            ["Evolution:CooldownHours"] = "12",
        };
        foreach (var (k, v) in extra) d[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private sealed class Fixture
    {
        public AppDbContext Db { get; }
        public Fixture(int traitCount, string core = "c")
        {
            Db = new TestAppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Db.Users.Add(new User { Id = 1, Username = "o", DisplayName = "O", PasswordHash = "x", IsActive = true, CreatedAt = DateTime.UtcNow });
            Db.LlmPresets.Add(new LlmPreset { Id = 1, OwnerId = 1, Name = "P", Provider = LlmProvider.OpenAICompatible, BaseEndpoint = "http://x", GenerationModel = "m", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Db.Bots.Add(new Bot { Id = 9, OwnerId = 1, Name = "B", CorePersona = core, InviteCode = "c", PresetId = 1, EvolutionEnabled = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Db.Conversations.Add(new Conversation { Id = 7, BotId = 9, UserId = 1, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow });
            for (var i = 1; i <= traitCount; i++)
                Db.BotTraits.Add(new BotTrait { BotId = 9, Text = $"trait number {i:000} " + new string('z', 60), CreatedAt = DateTime.UtcNow.AddDays(-3) });
            Db.PersonaVersions.Add(new PersonaVersion { BotId = 9, Content = "", Snapshot = "[]", Source = PersonaVersionSource.Initial, CreatedAt = DateTime.UtcNow.AddDays(-3) });
            Db.SaveChanges();
        }

        public BotsController Owner(IConfiguration cfg)
        {
            var c = new BotsController(Db, cfg);
            c.ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "1")], "test")) },
            };
            return c;
        }

        public WorkItem NewItem()
        {
            var now = DateTime.UtcNow;
            var w = new WorkItem { Kind = WorkKind.Evolution, ConversationId = 7, Status = WorkStatus.Running, Attempts = 1, MaxAttempts = 5, NextTryAt = now, CreatedAt = now, UpdatedAt = now };
            Db.WorkItems.Add(w);
            Db.SaveChanges();
            return w;
        }
    }

    /// <summary>Scripted provider: <paramref name="respond"/> sees the request and its prompt-token estimate.</summary>
    private sealed class ScriptedProvider(Func<LlmChatRequest, int, LlmCompletion> respond) : ILlmProvider, ILlmProviderFactory
    {
        public List<(int Tokens, int Items)> Calls { get; } = [];
        public LlmProvider Provider => LlmProvider.OpenAICompatible;
        public Task<LlmCompletion> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
        {
            var text = string.Join("\n", request.Messages.Select(m => m.Content));
            var tokens = PromptAssembly.EstimateTokens(text);
            var items = request.Messages.Last().Content.Split('\n').Count(l => l.StartsWith("- trait number"));
            Calls.Add((tokens, items));
            return Task.FromResult(respond(request, tokens));
        }
        public IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(LlmChatRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public bool IsSupported(LlmProvider provider) => true;
        public ILlmProvider Create(LlmPresetConfig config) => this;
    }

    private sealed class NoKeys : IApiKeyProtector
    {
        public string? Protect(string? plaintext) => throw new NotSupportedException();
        public string? Unprotect(string? protectedValue) => null;
    }

    private static EvolutionHandler Handler(ScriptedProvider p, IConfiguration cfg) =>
        new(new NoKeys(), p, cfg, NullLogger<EvolutionHandler>.Instance);

    private static LlmCompletion Ok(string content, string finish = "stop") => new() { Content = content, FinishReason = finish };

    [Fact]
    public async Task Small_list_is_one_call_and_replaces_the_traits()
    {
        var f = new Fixture(5);
        var p = new ScriptedProvider((_, _) => Ok("<think>let me think [\"no\"]</think>[\"merged one\",\"merged two\"]"));
        var outcome = await Handler(p, Config()).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success);
        Assert.Single(p.Calls);
        Assert.Equal(["merged one", "merged two"], (await PersonaTraits.ActiveAsync(f.Db, 9)).Select(t => t.Text));
        Assert.Equal(PersonaVersionSource.Consolidation, (await f.Db.PersonaVersions.OrderBy(v => v.Id).LastAsync()).Source);
        Assert.Equal(1, await f.Db.LlmInteractions.CountAsync());
    }

    [Fact]
    public async Task Input_is_bounded_and_a_long_list_is_consolidated_in_chunks_oldest_first()
    {
        var f = new Fixture(60); // ~ 60 × 25 tokens = 1500 tokens of traits
        var n = 0;
        var p = new ScriptedProvider((req, _) => Ok("[\"chunk result " + System.Threading.Interlocked.Increment(ref n) + "\"]"));
        var outcome = await Handler(p, Config(("Evolution:MaxInputTokens", "500"))).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success);
        Assert.True(p.Calls.Count >= 4, $"expected chunking, got {p.Calls.Count} call(s)");
        Assert.All(p.Calls, c => Assert.True(c.Tokens <= 500 + 20, $"prompt of {c.Tokens} tokens exceeds the budget"));
        Assert.Equal(60, p.Calls.Sum(c => c.Items)); // every trait seen exactly once
        Assert.Equal(p.Calls.Count, await f.Db.LlmInteractions.CountAsync()); // each call logged (§4.4)
        // the chunk results (one each) are still above ConsolidateAbove=2 → one final pass over them
        Assert.Equal(0, p.Calls[^1].Items);
        Assert.Equal(["chunk result " + p.Calls.Count], (await PersonaTraits.ActiveAsync(f.Db, 9)).Select(t => t.Text));
    }

    [Fact]
    public async Task Context_error_shrinks_once_and_then_succeeds()
    {
        var f = new Fixture(8);
        var p = new ScriptedProvider((req, tokens) =>
            tokens > 300
                ? throw new LlmProviderException("Provider returned 400", 400, "{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"maximum context length is 300 tokens\"}}")
                : Ok("[\"half result\"]"));
        var outcome = await Handler(p, Config(("Evolution:MaxInputTokens", "100000"))).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.Equal(3, p.Calls.Count);          // full (rejected) + two halves
        Assert.Equal([8, 4, 4], p.Calls.Select(c => c.Items));
        var logs = await f.Db.LlmInteractions.OrderBy(i => i.Id).ToListAsync();
        Assert.Equal([true, false, false], logs.Select(l => l.Failed)); // the shrunk retries are logged too
        Assert.Equal(["half result"], (await PersonaTraits.ActiveAsync(f.Db, 9)).Select(t => t.Text));
    }

    [Fact]
    public async Task Context_error_that_shrinking_does_not_cure_is_permanent_and_loses_nothing()
    {
        var f = new Fixture(8);
        var p = new ScriptedProvider((_, _) => throw new LlmProviderException("Provider returned 400", 400, "context_length_exceeded"));
        var outcome = await Handler(p, Config(("Evolution:MaxInputTokens", "100000"))).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(outcome.Success);
        Assert.True(outcome.Permanent);
        Assert.Contains("context", outcome.Failure);
        Assert.Equal(2, p.Calls.Count);            // full + first half only, never a third attempt on the same input
        Assert.Equal(8, (await PersonaTraits.ActiveAsync(f.Db, 9)).Count);
        Assert.Single(await f.Db.PersonaVersions.ToListAsync());
    }

    [Fact]
    public async Task Truncation_is_an_actionable_permanent_failure_not_a_parse_error()
    {
        var f = new Fixture(3);
        var p = new ScriptedProvider((_, _) => Ok("", "length")); // reasoning model spent the cap thinking
        var outcome = await Handler(p, Config()).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(outcome.Success);
        Assert.True(outcome.Permanent);
        Assert.Contains("Evolution:MaxTokens", outcome.Failure);
        Assert.DoesNotContain("not a non-empty JSON array", outcome.Failure);
        Assert.Equal(3, (await PersonaTraits.ActiveAsync(f.Db, 9)).Count);
        Assert.All(await f.Db.LlmInteractions.ToListAsync(), i => Assert.True(i.Failed));
    }

    [Fact]
    public async Task Cut_off_content_with_finish_reason_length_is_never_parsed()
    {
        var f = new Fixture(4);
        var p = new ScriptedProvider((req, _) => req.Messages.Last().Content.Split('\n').Count(l => l.StartsWith("- trait number")) > 2
            ? Ok("[\"cut off but valid\"]", "length")
            : Ok("[\"whole\"]"));
        var outcome = await Handler(p, Config()).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.Equal(["whole"], (await PersonaTraits.ActiveAsync(f.Db, 9)).Select(t => t.Text)); // halves of 2 each → same dedup → one item
    }

    [Fact]
    public async Task Oversized_core_persona_fails_permanently_with_a_reason()
    {
        var f = new Fixture(5, core: new string('p', 40_000));
        var p = new ScriptedProvider((_, _) => Ok("[\"x\"]"));
        var outcome = await Handler(p, Config()).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Permanent);
        Assert.Contains("Evolution:MaxInputTokens", outcome.Failure);
        Assert.Empty(p.Calls);
        Assert.Single(await f.Db.LlmInteractions.ToListAsync()); // the reason is visible in §4.4
    }

    [Fact]
    public async Task Non_token_provider_errors_and_bad_json_stay_retryable()
    {
        var f = new Fixture(5);
        var down = new ScriptedProvider((_, _) => throw new LlmProviderException("Provider returned 503", 503, "overloaded"));
        var o1 = await Handler(down, Config()).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(o1.Success);
        Assert.False(o1.Permanent);

        var junk = new ScriptedProvider((_, _) => Ok("sorry, I cannot"));
        var o2 = await Handler(junk, Config()).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(o2.Success);
        Assert.False(o2.Permanent);
    }

    // ── output floor and the final pass ─────────────────────────────────────

    private static string Items(int n, string prefix = "kept") =>
        System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, n).Select(i => $"{prefix} {i}"));

    [Fact]
    public async Task A_result_below_the_floor_is_logged_retryable_and_not_applied()
    {
        var f = new Fixture(40);
        var p = new ScriptedProvider((_, _) => Ok("[\"one vague trait\"]")); // 40 → 1: the personality is gone
        var cfg = Config(("Turn:ConsolidateAbove", "12"), ("Evolution:MaxInputTokens", "100000"));
        var outcome = await Handler(p, cfg).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(outcome.Success);
        Assert.False(outcome.Permanent);                      // retryable: the next attempt may do better
        Assert.Contains("below the floor of 6", outcome.Failure);
        Assert.Contains("Evolution:MinTraitsAfter", outcome.Failure);
        Assert.Equal(40, (await PersonaTraits.ActiveAsync(f.Db, 9)).Count);
        Assert.Single(await f.Db.PersonaVersions.ToListAsync());
        var log = Assert.Single(await f.Db.LlmInteractions.ToListAsync());
        Assert.True(log.Failed);
        Assert.Equal("[\"one vague trait\"]", log.ResponseContent); // the rejected answer stays inspectable (§4.4)
    }

    [Fact]
    public async Task The_prompt_states_the_target_range_and_a_result_inside_it_is_applied()
    {
        var f = new Fixture(40);
        string? brief = null;
        var p = new ScriptedProvider((req, _) => { brief = req.Messages.Last().Content; return Ok(Items(8)); });
        var cfg = Config(("Turn:ConsolidateAbove", "12"), ("Evolution:MaxInputTokens", "100000"));
        var outcome = await Handler(p, cfg).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.Contains("between 6 and 12 items", brief);
        Assert.Equal(8, (await PersonaTraits.ActiveAsync(f.Db, 9)).Count);
    }

    [Fact]
    public async Task The_floor_is_configurable_and_never_above_the_input_count()
    {
        var f = new Fixture(5);
        string? brief = null;
        var p = new ScriptedProvider((req, _) => { brief = req.Messages.Last().Content; return Ok(Items(5)); });
        var cfg = Config(("Turn:ConsolidateAbove", "4"), ("Evolution:MinTraitsAfter", "30"));
        var outcome = await Handler(p, cfg).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.Contains("between 5 and 5 items", brief); // clamped to the 5 traits it was given
    }

    [Fact]
    public async Task Chunk_results_over_the_trigger_get_one_final_pass_within_the_range()
    {
        var f = new Fixture(60);
        var briefs = new List<string>();
        var p = new ScriptedProvider((req, _) =>
        {
            var b = req.Messages.Last().Content;
            briefs.Add(b);
            return b.Contains("- trait number") ? Ok(Items(4, $"c{briefs.Count}")) : Ok(Items(9, "final"));
        });
        var cfg = Config(("Turn:ConsolidateAbove", "12"), ("Evolution:MaxInputTokens", "500"));
        var outcome = await Handler(p, cfg).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.True(p.Calls.Count >= 4, $"{p.Calls.Count} calls");
        Assert.Contains("between 6 and 12 items", briefs[^1]);   // the final pass asks for the whole-list range
        Assert.Contains("- c1 1", briefs[^1]);                    // ... over the merged chunk results
        Assert.All(p.Calls, c => Assert.True(c.Tokens <= 500 + 20));
        Assert.Equal(Enumerable.Range(1, 9).Select(i => $"final {i}"), (await PersonaTraits.ActiveAsync(f.Db, 9)).Select(t => t.Text));
    }

    [Fact]
    public async Task A_failed_final_pass_keeps_the_chunk_results()
    {
        var f = new Fixture(60);
        var p = new ScriptedProvider((req, _) => req.Messages.Last().Content.Contains("- trait number")
            ? Ok(Items(4, "c" + Guid.NewGuid().ToString("N")[..4]))
            : Ok("[\"too few\"]")); // final pass collapses the list → below the floor → ignored
        var cfg = Config(("Turn:ConsolidateAbove", "12"), ("Evolution:MaxInputTokens", "500"));
        var outcome = await Handler(p, cfg).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        var chunkCalls = p.Calls.Count(c => c.Items > 0);
        Assert.Equal(chunkCalls + 1, p.Calls.Count);
        Assert.Equal(chunkCalls * 4, (await PersonaTraits.ActiveAsync(f.Db, 9)).Count);
        Assert.True((await f.Db.LlmInteractions.OrderBy(i => i.Id).LastAsync()).Failed);
    }
}
