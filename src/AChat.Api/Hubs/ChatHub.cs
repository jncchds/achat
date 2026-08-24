using System.Collections.Concurrent;
using AChat.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace AChat.Api.Hubs;

/// <summary>
/// The web transport for chat (§4.2). Generation runs **detached from the
/// browser**: leaving the page (or a network drop) drops only the relay — the
/// run continues, and the finished reply is in the timeline when you come back.
/// Stop is explicit (<see cref="StopAsync"/>) and still cancels the in-flight
/// call. One run per (bot, user), so two tabs cannot double-spend the key.
///
/// Client events (client-side names):
///   chat.started   { botId, messageId }
///   chat.delta     { delta }
///   chat.reasoning { delta }
///   chat.completed { assistantMessageId, content, complete, promptTokens, completionTokens, totalTokens, recalledMemoryIds }
///   chat.stopped   {}                          // Stop pressed; the partial is persisted
///   chat.failed    { message }
/// </summary>
[Authorize]
public class ChatHub(IServiceScopeFactory scopes) : Hub
{
    /// <summary>
    /// One in-flight run. The relay target (Client/ClientConn) is mutable — it
    /// follows the latest connection that is watching this run (page load or
    /// reconnect adopts it); the CTS and the accumulated text are stable.
    /// </summary>
    private sealed class Run
    {
        public required int BotId { get; init; }
        public required int UserId { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public int? UserMessageId { get; set; }
        public readonly System.Text.StringBuilder Content = new();
        public readonly System.Text.StringBuilder Reasoning = new();
        public IClientProxy? Client { get; set; }
        public string? ClientConn { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Run> Active = new();

    private int _userId => int.Parse(Context.User!.FindFirst("sub")!.Value);
    private static string Key(int botId, int userId) => $"{botId}:{userId}";

    /// <summary>One fresh turn: starts a detached run; the invoke resolves immediately (§4.2).</summary>
    public Task SendAsync(int botId, string content)
    {
        var userId = _userId;
        var run = StartRun(botId, userId);
        Go(run, (chat, ct) => chat.RunAsync(
            botId, userId, content,
            onStart: m => { run.UserMessageId = m; Relay(run, "chat.started", new { botId, messageId = m }); return Task.CompletedTask; },
            onContentDelta: d =>
            {
                lock (run.Content) run.Content.Append(d);
                Relay(run, "chat.delta", new { delta = d }); return Task.CompletedTask;
            },
            onReasoningDelta: d =>
            {
                lock (run.Reasoning) run.Reasoning.Append(d);
                Relay(run, "chat.reasoning", new { delta = d }); return Task.CompletedTask;
            },
            ct));
        return Task.CompletedTask;
    }

    /// <summary>
    /// "Try again" (§12b): re-run the last turn that has no complete reply — a
    /// failed or stopped turn is not a dead end. Detached from the browser, like
    /// a fresh turn (§4.2).
    /// </summary>
    public Task RetryLastAsync(int botId)
    {
        var userId = _userId;
        var run = StartRun(botId, userId);
        Go(run, (chat, ct) => chat.RetryLastAsync(
            botId, userId,
            onStart: m => { run.UserMessageId = m; Relay(run, "chat.started", new { botId, messageId = m }); return Task.CompletedTask; },
            onContentDelta: d =>
            {
                lock (run.Content) run.Content.Append(d);
                Relay(run, "chat.delta", new { delta = d }); return Task.CompletedTask;
            },
            onReasoningDelta: d =>
            {
                lock (run.Reasoning) run.Reasoning.Append(d);
                Relay(run, "chat.reasoning", new { delta = d }); return Task.CompletedTask;
            },
            ct));
        return Task.CompletedTask;
    }

    /// <summary>
    /// "Stop generating" (§4.2) — explicit cancel; the partial is persisted and a
    /// "Try again" is offered. A plain disconnect no longer stops the run.
    /// </summary>
    public Task StopAsync(int botId)
    {
        if (Active.TryGetValue(Key(botId, _userId), out var run))
            run.Cts.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>What an in-flight run has produced so far (for a re-attaching client).</summary>
    public sealed record ReattachState(int? UserMessageId, string Content, string Reasoning);

    /// <summary>
    /// Adopt an in-flight run for this bot (page load, or a reconnect while the bot
    /// is still working, §4.2): this connection becomes the relay and the caller
    /// gets what has streamed so far. Null when nothing is in flight.
    /// </summary>
    public Task<ReattachState?> ReattachAsync(int botId)
    {
        if (!Active.TryGetValue(Key(botId, _userId), out var run))
            return Task.FromResult<ReattachState?>(null);

        run.Client = Clients.Client(Context.ConnectionId);
        run.ClientConn = Context.ConnectionId;

        string content, reasoning;
        lock (run.Content) content = run.Content.ToString();
        lock (run.Reasoning) reasoning = run.Reasoning.ToString();
        return Task.FromResult<ReattachState?>(new ReattachState(run.UserMessageId, content, reasoning));
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // The run keeps going (§4.2) — drop only the relay target so deltas stop
        // being delivered to this dead connection.
        var conn = Context.ConnectionId;
        foreach (var run in Active.Values)
        {
            if (run.ClientConn == conn)
            {
                run.Client = null;
                run.ClientConn = null;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Register a run for this (bot, user), refusing to start a second one while
    /// a run is already in flight (two tabs must not double-spend the key).
    /// </summary>
    private Run StartRun(int botId, int userId)
    {
        var run = new Run
        {
            BotId = botId,
            UserId = userId,
            Cts = new CancellationTokenSource(),
            Client = Clients.Client(Context.ConnectionId),
            ClientConn = Context.ConnectionId,
        };
        if (!Active.TryAdd(Key(botId, userId), run))
        {
            run.Cts.Dispose();
            throw new InvalidOperationException(
                "This bot is already working on a reply — wait for it to finish, or press Stop.");
        }
        return run;
    }

    /// <summary>
    /// Run the turn on a background thread in its **own DI scope** — independent
    /// of the connection that started it, so it outlives the page (§4.2). The
    /// outcome is relayed to whatever client is currently watching.
    /// </summary>
    private void Go(Run run, Func<ChatService, CancellationToken, Task<ChatService.ChatRunResult>> start)
    {
        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            try
            {
                var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
                var result = await start(chat, run.Cts.Token);

                if (result.Failed)
                {
                    Relay(run, "chat.failed", new { message = result.Error ?? "Generation failed." });
                }
                else if (result.Canceled)
                {
                    Relay(run, "chat.stopped", new { });
                }
                else
                {
                    Relay(run, "chat.completed", new
                    {
                        assistantMessageId = result.AssistantMessageId,
                        content = result.AssistantContent,
                        complete = true,
                        promptTokens = result.PromptTokens,
                        completionTokens = result.CompletionTokens,
                        totalTokens = result.TotalTokens,
                        recalledMemoryIds = result.RecalledMemoryIds,
                    });
                }
            }
            catch (OperationCanceledException) when (run.Cts.IsCancellationRequested)
            {
                // Stopped before generation produced anything — nothing was persisted.
                Relay(run, "chat.stopped", new { });
            }
            catch (Exception ex)
            {
                // A provider timeout is a TaskCanceledException without a user stop.
                // Kill switch / quota / "nothing to retry" surface the same way.
                Relay(run, "chat.failed", new
                {
                    message = ex is OperationCanceledException ? "The model request timed out." : ex.Message,
                });
            }
            finally
            {
                Active.TryRemove(Key(run.BotId, run.UserId), out _);
                run.Cts.Dispose();
            }
        });
    }

    /// <summary>
    /// Best-effort delivery to the current watcher (§4.2): no watcher, or a dead
    /// one, never affects the run.
    /// </summary>
    private static void Relay(Run run, string method, object payload)
    {
        var client = run.Client;
        if (client is null) return;
        try
        {
            _ = client.SendAsync(method, payload)
                .ContinueWith(t => _ = t.Exception); // observed; a dead watcher is expected
        }
        catch
        {
            // Watcher gone — the run continues.
        }
    }
}
