using System.Collections.Concurrent;
using AChat.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AChat.Api.Hubs;

/// <summary>
/// The web transport for chat (§4.2): tokens are relayed to the client as they arrive.
/// Generation is tied to the connection lifetime — a disconnect cancels the run and
/// the partial is persisted (§4.2).
///
/// Client events (client-side names):
///   chat.started   { botId, messageId }
///   chat.delta     { delta }
///   chat.reasoning { delta }
///   chat.completed { assistantMessageId, content, complete, promptTokens, completionTokens, totalTokens }
///   chat.failed    { message }
/// </summary>
[Authorize]
public class ChatHub(ChatService chat) : Hub
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Active = new();

    private int _userId => int.Parse(Context.User!.FindFirst("sub")!.Value);

    public Task SendAsync(int botId, string content)
    {
        if (Active.TryRemove(Context.ConnectionId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        Active[Context.ConnectionId] = cts;
        return Run(botId, content, cts);
    }

    /// <summary>"Stop generating" (§4.2).</summary>
    public Task StopAsync()
    {
        if (Active.TryGetValue(Context.ConnectionId, out var cts))
            cts.Cancel();
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // A disconnect cancels the in-flight generation; the partial is persisted.
        if (Active.TryRemove(Context.ConnectionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
        return base.OnDisconnectedAsync(exception);
    }

    private async Task Run(int botId, string content, CancellationTokenSource cts)
    {
        int userMessageId = 0;
        try
        {
            // Relay deltas as they arrive; a client disconnect throws here and the
            // service treats it as a failed/cancelled run.
            var result = await chat.RunAsync(
                botId,
                _userId,
                content,
                onStart: async messageId =>
                {
                    userMessageId = messageId;
                    try { await Clients.Caller.SendAsync("chat.started", new { botId, messageId }); }
                    catch { /* client already gone; the run continues and persists */ }
                },
                onContentDelta: async delta => await Clients.Caller.SendAsync("chat.delta", new { delta }),
                onReasoningDelta: async delta => await Clients.Caller.SendAsync("chat.reasoning", new { delta }),
                cts.Token);

            if (result.Failed)
            {
                await Clients.Caller.SendAsync("chat.failed", new { message = result.Error ?? "Generation failed." });
            }
            else
            {
                await Clients.Caller.SendAsync("chat.completed", new
                {
                    assistantMessageId = result.AssistantMessageId,
                    content = result.AssistantContent,
                    complete = !result.Canceled,
                    promptTokens = result.PromptTokens,
                    completionTokens = result.CompletionTokens,
                    totalTokens = result.TotalTokens,
                    recalledMemoryIds = result.RecalledMemoryIds,
                });
            }
        }
        // A provider timeout is a TaskCanceledException without a user cancel —
        // it must reach the client as chat.failed, not as a raw invoke error.
        catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested)
        {
            try
            {
                await Clients.Caller.SendAsync("chat.failed",
                    new { message = ex is OperationCanceledException ? "The model request timed out." : ex.Message });
            }
            catch
            {
                // Client is gone — nothing else to do; the service already logged the call.
            }
        }
        finally
        {
            Active.TryRemove(Context.ConnectionId, out _);
        }
    }
}
