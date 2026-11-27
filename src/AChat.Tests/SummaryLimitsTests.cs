using AChat.Api.Services;
using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AChat.Tests;

/// <summary>
/// Token limits of the rolling-summary pass (§5.3, §12b): the input budget and summarising in
/// steps, think-stripping, context error → halve → success, truncation → permanent, and that a
/// failure never moves the watermark past what the summary covers.
/// </summary>
public class SummaryLimitsTests
{
    private static IConfiguration Config(params (string, string)[] extra)
    {
        var d = new Dictionary<string, string?> { ["Chat:SummaryTailTokens"] = "50" };
        foreach (var (k, v) in extra) d[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    private sealed class Fixture
    {
        public AppDbContext Db { get; }
        public List<int> MessageIds { get; } = [];

        public Fixture(int messages, int chars = 200, string? previousSummary = null)
        {
            Db = new TestAppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Db.Users.Add(new User { Id = 1, Username = "o", DisplayName = "O", PasswordHash = "x", IsActive = true, CreatedAt = DateTime.UtcNow });
            Db.LlmPresets.Add(new LlmPreset { Id = 1, OwnerId = 1, Name = "P", Provider = LlmProvider.OpenAICompatible, BaseEndpoint = "http://x", GenerationModel = "m", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Db.Bots.Add(new Bot { Id = 9, OwnerId = 1, Name = "B", CorePersona = "c", InviteCode = "c", PresetId = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Db.Conversations.Add(new Conversation { Id = 7, BotId = 9, UserId = 1, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow });
            if (previousSummary is not null)
                Db.ConversationSummaries.Add(new ConversationSummary { BotId = 9, UserId = 1, Content = previousSummary, CoversUpToMessageId = 0, UpdatedAt = DateTime.UtcNow });
            Db.SaveChanges();
            for (var i = 1; i <= messages; i++)
            {
                var m = new Message
                {
                    ConversationId = 7, Role = i % 2 == 1 ? MessageRole.User : MessageRole.Assistant,
                    Content = $"msg{i:000} " + new string('w', chars), CreatedAt = DateTime.UtcNow.AddMinutes(i),
                };
                Db.Messages.Add(m);
                Db.SaveChanges();
                MessageIds.Add(m.Id);
            }
        }

        public WorkItem NewItem()
        {
            var now = DateTime.UtcNow;
            var w = new WorkItem { Kind = WorkKind.Summary, ConversationId = 7, Status = WorkStatus.Running, Attempts = 1, MaxAttempts = 5, NextTryAt = now, CreatedAt = now, UpdatedAt = now };
            Db.WorkItems.Add(w);
            Db.SaveChanges();
            return w;
        }

        public ConversationSummary? Summary => Db.ConversationSummaries.AsNoTracking().FirstOrDefault();
    }

    /// <summary>Scripted provider: <c>respond</c> sees the request, its token estimate and the message numbers in it.</summary>
    private sealed class ScriptedProvider(Func<LlmChatRequest, int, List<int>, LlmCompletion> respond) : ILlmProvider, ILlmProviderFactory
    {
        public List<(int Tokens, List<int> Messages)> Calls { get; } = [];
        public LlmProvider Provider => LlmProvider.OpenAICompatible;
        public Task<LlmCompletion> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
        {
            var text = string.Join("\n", request.Messages.Select(m => m.Content));
            var tokens = PromptAssembly.EstimateTokens(text);
            var user = request.Messages.Last().Content;
            var newPart = user[user.IndexOf("New messages since then", StringComparison.Ordinal)..];
            var msgs = System.Text.RegularExpressions.Regex.Matches(newPart, @"(user|friend): msg(\d{3})")
                .Select(m => int.Parse(m.Groups[2].Value)).ToList();
            Calls.Add((tokens, msgs));
            return Task.FromResult(respond(request, tokens, msgs));
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

    private static SummaryHandler Handler(ScriptedProvider p, IConfiguration cfg) =>
        new(new NoKeys(), p, cfg, NullLogger<SummaryHandler>.Instance);

    private static LlmCompletion Ok(string content, string finish = "stop") => new() { Content = content, FinishReason = finish };

    private static LlmProviderException ContextError() =>
        new("Provider returned 400", 400, "{\"error\":{\"code\":\"context_length_exceeded\",\"message\":\"maximum context length exceeded\"}}");

    [Fact]
    public async Task A_short_tail_is_one_call_and_thinking_is_stripped()
    {
        var f = new Fixture(6);
        var p = new ScriptedProvider((_, _, _) => Ok("<think>who said what</think>They talked about the weather."));
        var outcome = await Handler(p, Config()).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.Single(p.Calls);
        Assert.Equal("They talked about the weather.", f.Summary!.Content);
        Assert.Equal(f.MessageIds[^1], f.Summary.CoversUpToMessageId);
        Assert.Contains("<think>", (await f.Db.LlmInteractions.SingleAsync()).ResponseContent); // raw kept (§4.4)
    }

    [Fact]
    public async Task A_long_tail_is_summarised_in_bounded_steps_oldest_first()
    {
        var f = new Fixture(40); // ~ 40 × 55 tokens = 2200 tokens
        var n = 0;
        string? lastPrevious = null;
        var p = new ScriptedProvider((req, _, msgs) =>
        {
            var user = req.Messages.Last().Content;
            lastPrevious = user[..user.IndexOf("\n\nNew messages", StringComparison.Ordinal)];
            return Ok($"summary after step {++n} (up to msg{msgs.Max():000})");
        });
        var outcome = await Handler(p, Config(("Chat:SummaryMaxInputTokens", "700"))).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.True(p.Calls.Count >= 4, $"{p.Calls.Count} calls");
        Assert.All(p.Calls, c => Assert.True(c.Tokens <= 700, $"prompt of {c.Tokens} tokens exceeds the budget"));
        Assert.Equal(Enumerable.Range(1, 40), p.Calls.SelectMany(c => c.Messages)); // each message once, in order
        Assert.Contains($"summary after step {n - 1}", lastPrevious);                // each step builds on the last
        Assert.Equal(f.MessageIds[^1], f.Summary!.CoversUpToMessageId);
        Assert.StartsWith($"summary after step {n}", f.Summary.Content);
        Assert.Equal(p.Calls.Count, await f.Db.LlmInteractions.CountAsync());
    }

    [Fact]
    public async Task Steps_per_pass_are_capped_and_the_watermark_matches_the_summary()
    {
        var f = new Fixture(40);
        var p = new ScriptedProvider((_, _, msgs) => Ok($"covers up to msg{msgs.Max():000}"));
        var outcome = await Handler(p, Config(("Chat:SummaryMaxInputTokens", "700"), ("Chat:SummaryMaxSteps", "2")))
            .ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success);
        Assert.Equal(2, p.Calls.Count);
        var last = p.Calls[^1].Messages.Max();
        Assert.Equal($"covers up to msg{last:000}", f.Summary!.Content);
        Assert.Equal(f.MessageIds[last - 1], f.Summary.CoversUpToMessageId); // the producer re-arms the rest
    }

    [Fact]
    public async Task Context_error_halves_the_step_once_and_then_succeeds()
    {
        var f = new Fixture(8);
        var p = new ScriptedProvider((_, _, msgs) => msgs.Count > 4 ? throw ContextError() : Ok("ok up to " + msgs.Max()));
        var outcome = await Handler(p, Config(("Chat:SummaryMaxInputTokens", "100000"))).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.Equal([8, 4, 4], p.Calls.Select(c => c.Messages.Count));
        Assert.Equal([true, false, false], (await f.Db.LlmInteractions.OrderBy(i => i.Id).ToListAsync()).Select(i => i.Failed));
        Assert.Equal(400, (await f.Db.LlmInteractions.OrderBy(i => i.Id).FirstAsync()).HttpStatus);
        Assert.Equal(f.MessageIds[^1], f.Summary!.CoversUpToMessageId);
    }

    [Fact]
    public async Task Context_error_that_halving_does_not_cure_is_permanent_and_loses_nothing()
    {
        var f = new Fixture(8, previousSummary: "the old summary");
        var p = new ScriptedProvider((_, _, _) => throw ContextError());
        var outcome = await Handler(p, Config(("Chat:SummaryMaxInputTokens", "100000"))).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(outcome.Success);
        Assert.True(outcome.Permanent);
        Assert.Contains("Chat:SummaryMaxInputTokens", outcome.Failure);
        Assert.Equal(2, p.Calls.Count); // full + first half, never the same input again
        Assert.Equal("the old summary", f.Summary!.Content);
        Assert.Equal(0, f.Summary.CoversUpToMessageId);
    }

    [Fact]
    public async Task Truncation_is_an_actionable_permanent_failure_and_never_replaces_the_summary()
    {
        var f = new Fixture(6, previousSummary: "the old summary");
        var p = new ScriptedProvider((_, _, _) => Ok("They talked about the wea", "length"));
        var outcome = await Handler(p, Config()).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(outcome.Success);
        Assert.True(outcome.Permanent);
        Assert.Contains("finish_reason=length", outcome.Failure);
        Assert.Contains("Chat:SummaryMaxTokens", outcome.Failure);
        Assert.Equal("the old summary", f.Summary!.Content);
        Assert.Equal(0, f.Summary.CoversUpToMessageId);
        Assert.Equal(2, p.Calls.Count); // halved once, then given up
    }

    [Fact]
    public async Task A_failure_after_earlier_steps_keeps_their_progress()
    {
        var f = new Fixture(40);
        var p = new ScriptedProvider((_, _, msgs) => msgs.Contains(30)
            ? throw new LlmProviderException("Provider returned 503", 503, "overloaded")
            : Ok($"covers up to msg{msgs.Max():000}"));
        var outcome = await Handler(p, Config(("Chat:SummaryMaxInputTokens", "700"))).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(outcome.Success);
        Assert.False(outcome.Permanent); // a server error is retryable
        var covered = p.Calls.Where(c => !c.Messages.Contains(30)).SelectMany(c => c.Messages).Max();
        Assert.True(covered < 30);
        Assert.Equal($"covers up to msg{covered:000}", f.Summary!.Content);
        Assert.Equal(f.MessageIds[covered - 1], f.Summary.CoversUpToMessageId);
    }

    [Fact]
    public async Task One_huge_message_is_clipped_not_a_permanent_block()
    {
        var f = new Fixture(3, chars: 40_000);
        var p = new ScriptedProvider((_, _, _) => Ok("a summary"));
        var outcome = await Handler(p, Config(("Chat:SummaryMaxInputTokens", "2000"))).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.Equal(3, p.Calls.Count);
        Assert.All(p.Calls, c => Assert.True(c.Tokens <= 2000, $"{c.Tokens}"));
        Assert.Equal(f.MessageIds[^1], f.Summary!.CoversUpToMessageId);
    }

    [Fact]
    public async Task A_previous_summary_over_the_budget_fails_permanently_with_a_reason()
    {
        var f = new Fixture(6, previousSummary: new string('s', 20_000));
        var p = new ScriptedProvider((_, _, _) => Ok("x"));
        var outcome = await Handler(p, Config(("Chat:SummaryMaxInputTokens", "2000"))).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.True(outcome.Permanent);
        Assert.Contains("Chat:SummaryMaxInputTokens", outcome.Failure);
        Assert.Empty(p.Calls);
        Assert.True((await f.Db.LlmInteractions.SingleAsync()).Failed); // the reason is visible in §4.4
    }

    [Fact]
    public async Task Empty_answers_and_server_errors_stay_retryable()
    {
        var f = new Fixture(6);
        var o1 = await Handler(new ScriptedProvider((_, _, _) => Ok("<think>only thinking</think>")), Config()).ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(o1.Success);
        Assert.False(o1.Permanent);
        var o2 = await Handler(new ScriptedProvider((_, _, _) => throw new LlmProviderException("Provider returned 500", 500, "boom")), Config())
            .ExecuteAsync(f.Db, f.NewItem(), default);
        Assert.False(o2.Success);
        Assert.False(o2.Permanent);
        Assert.Null(f.Summary);
    }
}
