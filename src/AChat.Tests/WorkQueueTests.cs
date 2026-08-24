using AChat.Api.Controllers;
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
/// The durable work queue (§12b) against an in-memory database: claim semantics,
/// back-off, dead-lettering, the producer's idempotent upsert, the admin retry /
/// discard endpoints, and the chat "try again" path (a failed turn is re-runnable,
/// a completed one is not).
/// </summary>
public class WorkQueueTests
{
    private sealed class Fixture
    {
        public AppDbContext Db { get; }
        public int AdminId { get; } = 1;
        public int UserId { get; } = 2;
        public int BotId { get; } = 9;
        public int ConversationId { get; } = 7;

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
            Db.Bots.Add(new Bot
            {
                Id = BotId, OwnerId = AdminId, Name = "Mira", CorePersona = "x",
                InviteCode = "c", PresetId = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            Db.Conversations.Add(new Conversation
            {
                Id = ConversationId, BotId = BotId, UserId = AdminId,
                CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow,
            });
            Db.SaveChanges();
        }
    }

    private static T WithUser<T>(T controller, int userId, bool admin = true) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim("sub", userId.ToString()),
                        new Claim(ClaimTypes.Role, admin ? "admin" : "user"),
                    }, "test")),
            },
        };
        return controller;
    }

    private static WorkItem NewItem(Fixture f, WorkStatus status = WorkStatus.Pending,
        DateTime? nextTryAt = null, int maxAttempts = 5, string? failure = null, int attempts = 0)
    {
        var now = DateTime.UtcNow;
        var item = new WorkItem
        {
            Kind = WorkKind.Extraction,
            ConversationId = f.ConversationId,
            Status = status,
            MaxAttempts = maxAttempts,
            NextTryAt = nextTryAt ?? now,
            CreatedAt = now,
            UpdatedAt = now,
            LastFailure = failure,
            Attempts = attempts,
        };
        f.Db.WorkItems.Add(item);
        f.Db.SaveChanges();
        return item;
    }

    // ── Claim ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Claim_returns_oldest_due_item_marked_running_with_attempt()
    {
        var f = new Fixture();
        var older = NewItem(f); // (Extraction, conversation)
        // A different kind for the same conversation — the unique key is (Kind, ConversationId).
        var newer = new WorkItem
        {
            Kind = WorkKind.Summary, ConversationId = f.ConversationId,
            Status = WorkStatus.Pending, MaxAttempts = 5,
            NextTryAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow.AddSeconds(10), UpdatedAt = DateTime.UtcNow,
        };
        f.Db.WorkItems.Add(newer);
        f.Db.SaveChanges();

        var claimed = await WorkQueue.ClaimAsync(f.Db);

        Assert.NotNull(claimed);
        Assert.Equal(older.Id, claimed!.Id); // oldest first
        Assert.Equal(WorkStatus.Running, claimed.Status);
        Assert.Equal(1, claimed.Attempts);

        var otherClaimed = await WorkQueue.ClaimAsync(f.Db);
        Assert.NotNull(otherClaimed);
        Assert.Equal(newer.Id, otherClaimed!.Id);

        Assert.Null(await WorkQueue.ClaimAsync(f.Db));
    }

    [Fact]
    public async Task Claim_skips_items_not_yet_due()
    {
        var f = new Fixture();
        NewItem(f, nextTryAt: DateTime.UtcNow.AddMinutes(5));
        Assert.Null(await WorkQueue.ClaimAsync(f.Db));
    }

    // ── Outcomes ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Success_deletes_the_item()
    {
        var f = new Fixture();
        var item = NewItem(f);
        await WorkQueue.RecordOutcomeAsync(f.Db, item, WorkOutcome.Ok);
        Assert.False(await f.Db.WorkItems.AnyAsync(w => w.Id == item.Id));
    }

    [Fact]
    public async Task Failure_backs_off_to_pending_with_delay_and_failure_text()
    {
        var f = new Fixture();
        var item = NewItem(f);
        item.Status = WorkStatus.Running;
        item.Attempts = 1;
        f.Db.SaveChanges();

        var before = DateTime.UtcNow;
        await WorkQueue.RecordOutcomeAsync(f.Db, item, WorkOutcome.Fail("Provider returned HTTP 500: boom"),
            baseDelaySeconds: 60, maxDelaySeconds: 3600);
        var after = DateTime.UtcNow;

        item = await f.Db.WorkItems.FirstAsync(w => w.Id == item.Id);
        Assert.Equal(WorkStatus.Pending, item.Status);
        Assert.Equal("Provider returned HTTP 500: boom", item.LastFailure);
        // 60s ± 10% jitter.
        Assert.InRange(item.NextTryAt, before.AddSeconds(54 - 1), after.AddSeconds(66 + 1));
    }

    [Fact]
    public async Task Failure_at_the_attempt_cap_dead_letters()
    {
        var f = new Fixture();
        var item = NewItem(f, maxAttempts: 2);
        item.Status = WorkStatus.Running;
        item.Attempts = 2; // claim already counted this attempt
        f.Db.SaveChanges();

        await WorkQueue.RecordOutcomeAsync(f.Db, item, WorkOutcome.Fail("still failing"));

        item = await f.Db.WorkItems.FirstAsync(w => w.Id == item.Id);
        Assert.Equal(WorkStatus.Dead, item.Status);
        Assert.Equal("still failing", item.LastFailure);
    }

    [Fact]
    public void DelaySeconds_doubles_per_attempt_and_caps()
    {
        Assert.Equal(60, WorkQueue.DelaySeconds(1));
        Assert.Equal(120, WorkQueue.DelaySeconds(2));
        Assert.Equal(240, WorkQueue.DelaySeconds(3));
        Assert.Equal(480, WorkQueue.DelaySeconds(4));
        Assert.Equal(3600, WorkQueue.DelaySeconds(7));  // 60·2^6 = 3840 → capped
        Assert.Equal(3600, WorkQueue.DelaySeconds(20)); // capped
    }

    // ── Producer upsert ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Upsert_is_idempotent_per_kind_and_conversation()
    {
        var f = new Fixture();
        await WorkQueue.UpsertAsync(f.Db, WorkKind.Extraction, f.ConversationId, 5);
        await WorkQueue.UpsertAsync(f.Db, WorkKind.Extraction, f.ConversationId, 5); // same key — no-op
        await WorkQueue.UpsertAsync(f.Db, WorkKind.Summary, f.ConversationId, 5);   // different kind
        await WorkQueue.UpsertAsync(f.Db, WorkKind.Evolution, f.ConversationId, 5); // different kind

        Assert.Equal(3, await f.Db.WorkItems.CountAsync());
    }

    [Fact]
    public async Task Upsert_skips_when_a_dead_item_exists_so_it_stays_frozen()
    {
        var f = new Fixture();
        var dead = NewItem(f, status: WorkStatus.Dead, failure: "doomed");
        await WorkQueue.UpsertAsync(f.Db, WorkKind.Extraction, f.ConversationId, 5);

        Assert.Equal(1, await f.Db.WorkItems.CountAsync());
        Assert.Equal(WorkStatus.Dead, (await f.Db.WorkItems.FirstAsync(w => w.Id == dead.Id)).Status);
    }

    // ── Admin endpoints ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_can_retry_a_dead_item()
    {
        var f = new Fixture();
        var dead = NewItem(f, status: WorkStatus.Dead, failure: "doomed", attempts: 5);
        var admin = WithUser(new WorkController(f.Db), f.AdminId);

        var res = await admin.Retry(dead.Id);
        var ok = Assert.IsType<OkObjectResult>(res.Result);
        var dto = Assert.IsType<AChat.Api.Dtos.WorkItemDto>(ok!.Value);

        Assert.Equal("Pending", dto.Status);
        Assert.Equal(0, dto.Attempts);
        Assert.Null(dto.LastFailure);
        Assert.Equal(WorkStatus.Pending, (await f.Db.WorkItems.FirstAsync(w => w.Id == dead.Id)).Status);
    }

    [Fact]
    public async Task Admin_cannot_retry_a_running_item()
    {
        var f = new Fixture();
        var running = NewItem(f, status: WorkStatus.Running);
        var admin = WithUser(new WorkController(f.Db), f.AdminId);

        var res = await admin.Retry(running.Id);
        Assert.IsType<ConflictObjectResult>(res.Result);
    }

    [Fact]
    public async Task Admin_can_discard_an_item()
    {
        var f = new Fixture();
        var dead = NewItem(f, status: WorkStatus.Dead);
        var admin = WithUser(new WorkController(f.Db), f.AdminId);

        var res = await admin.Discard(dead.Id);
        Assert.IsType<NoContentResult>(res);
        Assert.False(await f.Db.WorkItems.AnyAsync(w => w.Id == dead.Id));
    }

    [Fact]
    public async Task Non_admin_cannot_list_the_work_queue()
    {
        var f = new Fixture();
        NewItem(f);
        var user = WithUser(new WorkController(f.Db), f.UserId, admin: false);

        // List() returns ActionResult<T>, which wraps the ForbidResult.
        var res = await user.List();
        var wrap = Assert.IsType<ActionResult<IReadOnlyList<AChat.Api.Dtos.WorkItemDto>>>(res);
        Assert.IsType<ForbidResult>(wrap.Result);
    }

    // ── Chat "try again" (§12b) ──────────────────────────────────────────────────

    private sealed class FakeKeys : IApiKeyProtector
    {
        public string? Protect(string? plaintext) => throw new NotSupportedException();
        public string? Unprotect(string? protectedValue) => null;
    }

    private sealed class FakeProvider : ILlmProvider
    {
        public string Reply { get; init; } = "hello!";
        public int StreamCalls { get; private set; }
        public LlmProvider Provider => LlmProvider.OpenAICompatible;

        public Task<LlmCompletion> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
            => Task.FromResult(new LlmCompletion { Content = Reply, FinishReason = "stop" });

        public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
            LlmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamCalls++;
            yield return new LlmStreamChunk
            {
                Content = Reply,
                FinishReason = "stop",
                PromptTokens = 1,
                CompletionTokens = 1,
                TotalTokens = 2,
            };
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[2]).ToList());

        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LlmModelInfo>>(new[] { new LlmModelInfo("m") });
    }

    private sealed class FakeFactory : ILlmProviderFactory
    {
        private readonly ILlmProvider _provider;
        public FakeFactory(ILlmProvider provider) => _provider = provider;
        public bool IsSupported(LlmProvider provider) => true;
        public ILlmProvider Create(LlmPresetConfig config) => _provider;
    }

    /// <summary>Streams several slow chunks so a stop can land mid-generation.</summary>
    private sealed class SlowProvider : ILlmProvider
    {
        public LlmProvider Provider => LlmProvider.OpenAICompatible;

        public Task<LlmCompletion> ChatAsync(LlmChatRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
            LlmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 1; i <= 5; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(50, ct);
                yield return new LlmStreamChunk { Content = $"part{i} " };
            }
            yield return new LlmStreamChunk
            {
                Content = "", FinishReason = "stop", PromptTokens = 1, CompletionTokens = 5, TotalTokens = 6,
            };
        }

        public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<float[]>>(new List<float[]>());

        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LlmModelInfo>>(new List<LlmModelInfo>());
    }

    private sealed class ChatFixture
    {
        public AppDbContext Db { get; }
        public int AdminId { get; } = 1;
        public int BotId { get; } = 9;
        public int ConversationId { get; } = 7;
        public FakeProvider Provider { get; } = new();

        public ChatFixture()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = new TestAppDbContext(options);

            Db.Users.Add(new User { Id = AdminId, Username = "admin", DisplayName = "Admin", PasswordHash = "x", IsAdmin = true, IsActive = true, CreatedAt = DateTime.UtcNow });
            Db.LlmPresets.Add(new LlmPreset
            {
                Id = 1, OwnerId = AdminId, Name = "Preset", Provider = LlmProvider.OpenAICompatible,
                BaseEndpoint = "http://localhost", GenerationModel = "m",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            Db.Bots.Add(new Bot
            {
                Id = BotId, OwnerId = AdminId, Name = "Mira", CorePersona = "x",
                InviteCode = "c", PresetId = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            Db.Conversations.Add(new Conversation
            {
                Id = ConversationId, BotId = BotId, UserId = AdminId,
                CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow,
            });
            Db.SaveChanges();
        }

        public ChatService Service(ILlmProvider? provider = null)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Chat:HistoryTurns"] = "20",
                    ["Chat:GapThresholdMinutes"] = "360",
                    ["Chat:AssumedContextTokens"] = "32000",
                    ["Chat:BudgetPersonaPercent"] = "10",
                    ["Chat:BudgetMemoryPercent"] = "15",
                    ["Chat:BudgetSummaryPercent"] = "10",
                    ["Chat:BudgetHistoryPercent"] = "50",
                }).Build();
            return new ChatService(
                Db, new FakeFactory(provider ?? Provider), new FakeKeys(),
                MemoryRetriever.FromConfiguration(config), config,
                NullLogger<ChatService>.Instance);
        }

        public async Task AddMessage(MessageRole role, string content, bool complete = true)
        {
            var m = new Message
            {
                ConversationId = ConversationId,
                Role = role,
                Content = content,
                IsComplete = complete,
                CreatedAt = DateTime.UtcNow,
            };
            Db.Messages.Add(m);
            await Db.SaveChangesAsync();
        }
    }

    private static Task Noop() => Task.CompletedTask;

    // ── Detached generation (§4.2) — the run outlives its watcher ──────────────

    [Fact]
    public async Task A_dead_relay_does_not_kill_the_run()
    {
        var f = new ChatFixture();
        var service = f.Service();

        // A dead watcher: every relay attempt throws (navigated away, network drop).
        Task Dead(string _) => throw new InvalidOperationException("watcher gone");

        var result = await service.RunAsync(f.BotId, f.AdminId, "hi",
            onStart: _ => Noop(), onContentDelta: Dead, onReasoningDelta: Dead, CancellationToken.None);

        Assert.False(result.Failed);
        Assert.False(result.Canceled);
        Assert.Equal(f.Provider.Reply, result.AssistantContent);

        // The full reply is persisted, complete — as if nobody had been watching (§4.2).
        var assistant = await f.Db.Messages.LastAsync();
        Assert.Equal(MessageRole.Assistant, assistant.Role);
        Assert.Equal(f.Provider.Reply, assistant.Content);
        Assert.True(assistant.IsComplete);
    }

    [Fact]
    public async Task An_explicit_stop_cancels_mid_stream_and_persists_the_partial()
    {
        var f = new ChatFixture();
        var service = f.Service(new SlowProvider());
        var cts = new CancellationTokenSource();
        var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = service.RunAsync(f.BotId, f.AdminId, "hi",
            onStart: _ => Noop(),
            onContentDelta: _ => { firstChunk.TrySetResult(); return Noop(); },
            onReasoningDelta: _ => Noop(),
            cts.Token);

        await firstChunk.Task; // the first chunk has reached the (virtual) watcher
        cts.Cancel();         // explicit Stop — the run must die here
        var result = await task;

        Assert.True(result.Canceled);
        Assert.False(result.Failed);
        Assert.Equal("part1 ", result.AssistantContent);

        // The partial is on the record (§4.2) — a "Try again" can pick it up.
        var assistant = await f.Db.Messages.LastAsync();
        Assert.Equal(MessageRole.Assistant, assistant.Role);
        Assert.False(assistant.IsComplete);
    }

    [Fact]
    public async Task RunAsync_still_persists_user_and_assistant_turns()
    {
        var f = new ChatFixture();
        var service = f.Service();

        var result = await service.RunAsync(f.BotId, f.AdminId, "hi",
            onStart: _ => Noop(), onContentDelta: _ => Noop(), onReasoningDelta: _ => Noop(), CancellationToken.None);

        Assert.False(result.Failed);
        Assert.Equal("hello!", result.AssistantContent);
        var msgs = await f.Db.Messages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(2, msgs.Count);
        Assert.Equal(MessageRole.User, msgs[0].Role);
        Assert.Equal(MessageRole.Assistant, msgs[1].Role);
        Assert.Equal(1, f.Provider.StreamCalls);
    }

    [Fact]
    public async Task RetryLast_reuses_the_last_user_message_without_appending()
    {
        var f = new ChatFixture();
        var service = f.Service();
        await f.AddMessage(MessageRole.User, "hi"); // went unanswered (a failed turn left no assistant row)

        var result = await service.RetryLastAsync(f.BotId, f.AdminId,
            onStart: _ => Noop(), onContentDelta: _ => Noop(), onReasoningDelta: _ => Noop(), CancellationToken.None);

        Assert.False(result.Failed);
        var msgs = await f.Db.Messages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(2, msgs.Count); // user + the new assistant — no duplicated user turn
        Assert.Equal(MessageRole.User, msgs[0].Role);
        Assert.Equal(MessageRole.Assistant, msgs[1].Role);
        Assert.Equal(1, f.Provider.StreamCalls);
    }

    [Fact]
    public async Task RetryLast_deletes_the_partial_assistant_and_retries_the_turn()
    {
        var f = new ChatFixture();
        var service = f.Service();
        await f.AddMessage(MessageRole.User, "hi");
        await f.AddMessage(MessageRole.Assistant, "…(a cut-off partial", complete: false);

        var result = await service.RetryLastAsync(f.BotId, f.AdminId,
            onStart: _ => Noop(), onContentDelta: _ => Noop(), onReasoningDelta: _ => Noop(), CancellationToken.None);

        Assert.False(result.Failed);
        var msgs = await f.Db.Messages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
        Assert.Equal(2, msgs.Count); // the partial was dropped; the turn was re-run
        Assert.Equal(MessageRole.User, msgs[0].Role);
        Assert.Equal(MessageRole.Assistant, msgs[1].Role);
        Assert.True(msgs[1].IsComplete);
        Assert.Equal("hello!", msgs[1].Content);
    }

    [Fact]
    public async Task RetryLast_refuses_when_the_last_turn_already_completed()
    {
        var f = new ChatFixture();
        var service = f.Service();
        await f.AddMessage(MessageRole.User, "hi");
        await f.AddMessage(MessageRole.Assistant, "hello");

        await Assert.ThrowsAsync<NothingToRetryException>(() =>
            service.RetryLastAsync(f.BotId, f.AdminId,
                onStart: _ => Noop(), onContentDelta: _ => Noop(), onReasoningDelta: _ => Noop(), CancellationToken.None));

        // Nothing was re-run.
        Assert.Equal(0, f.Provider.StreamCalls);
    }

    [Fact]
    public async Task RetryLast_is_subject_to_the_kill_switch()
    {
        var f = new ChatFixture();
        var service = f.Service();
        await f.AddMessage(MessageRole.User, "hi");
        var bot = await f.Db.Bots.FirstAsync(b => b.Id == f.BotId);
        bot.Suspended = true;
        await f.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<BotUnavailableException>(() =>
            service.RetryLastAsync(f.BotId, f.AdminId,
                onStart: _ => Noop(), onContentDelta: _ => Noop(), onReasoningDelta: _ => Noop(), CancellationToken.None));

        Assert.Equal(0, f.Provider.StreamCalls);
    }
}
