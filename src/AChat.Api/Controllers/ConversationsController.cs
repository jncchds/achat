using System.Security.Claims;
using System.Text.Json;
using AChat.Core.DTOs.Conversations;
using AChat.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AChat.Api.Controllers;

[ApiController]
[Authorize]
public class ConversationsController(
    IConversationService conversationService,
    IChatService chatService,
    IConversationNotifier notifier) : ControllerBase
{
    [HttpGet("api/bots/{botId:guid}/conversations")]
    public async Task<IActionResult> GetAll(Guid botId, CancellationToken ct) =>
        Ok(await conversationService.GetConversationsAsync(botId, GetUserId(), ct));

    [HttpPost("api/bots/{botId:guid}/conversations")]
    public async Task<IActionResult> Create(Guid botId, [FromBody] CreateConversationRequest request, CancellationToken ct)
    {
        var result = await conversationService.CreateConversationAsync(botId, GetUserId(), request.Title, ct);
        return CreatedAtAction(nameof(GetMessages), new { id = result.Id }, result);
    }

    [HttpGet("api/conversations/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await conversationService.GetConversationAsync(id, GetUserId(), ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("api/conversations/{id:guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid id, CancellationToken ct) =>
        Ok(await conversationService.GetMessagesAsync(id, GetUserId(), ct));

    [HttpDelete("api/conversations/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var success = await conversationService.DeleteConversationAsync(id, GetUserId(), ct);
        return success ? NoContent() : NotFound();
    }

    [HttpPost("api/conversations/{id:guid}/chat")]
    public async Task Chat(Guid id, [FromBody] ChatRequest request, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        var userId = GetUserId();

        await foreach (var chunk in chatService.StreamAsync(id, userId, request.Content, ct))
        {
            var data = chunk.Replace("\n", "\\n");
            await Response.WriteAsync($"data: {data}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        await Response.WriteAsync("data: [DONE]\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    [HttpGet("api/conversations/{id:guid}/events")]
    public async Task Events(Guid id, CancellationToken ct)
    {
        // Verify the user has access to this conversation
        var conv = await conversationService.GetConversationAsync(id, GetUserId(), ct);
        if (conv is null) { Response.StatusCode = 404; return; }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        // Send a comment to establish the connection
        await Response.WriteAsync(": connected\n\n", ct);
        await Response.Body.FlushAsync(ct);

        await foreach (var msg in notifier.SubscribeAsync(id, ct))
        {
            var json = JsonSerializer.Serialize(msg, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
