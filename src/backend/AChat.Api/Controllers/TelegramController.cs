using AChat.Core.Services;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.Telegram;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Telegram.Bot.Types;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/telegram")]
public class TelegramController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TelegramHandlerService _handler;
    private readonly IEncryptionService _encryption;
    private readonly ITelegramRequestDispatcher _dispatcher;

    public TelegramController(
        AppDbContext db,
        TelegramHandlerService handler,
        IEncryptionService encryption,
        ITelegramRequestDispatcher dispatcher)
    {
        _db = db;
        _handler = handler;
        _encryption = encryption;
        _dispatcher = dispatcher;
    }

    [HttpPost("webhook/{botId:guid}")]
    public async Task<IActionResult> Webhook(Guid botId, CancellationToken ct)
    {
        if (!_dispatcher.TryAcquireInboundToken())
            return StatusCode(StatusCodes.Status429TooManyRequests);

        // Validate the secret token header (last 10 chars of the bot token)
        if (!Request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var secretHeader))
            return Unauthorized();

        var bot = await _db.Bots
            .Where(b => b.Id == botId)
            .Select(b => new { b.EncryptedTelegramBotToken })
            .FirstOrDefaultAsync(ct);

        if (bot?.EncryptedTelegramBotToken is null) return NotFound();

        var rawToken = _encryption.Decrypt(bot.EncryptedTelegramBotToken);
        var expectedSecret = rawToken[^10..];

        if (secretHeader.ToString() != expectedSecret)
            return Unauthorized();

        Update update;
        try
        {
            using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
            update = JsonSerializer.Deserialize<Update>(doc.RootElement.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        catch
        {
            return BadRequest();
        }

        await _handler.HandleUpdateAsync(botId, update, ct);
        return Ok();
    }
}
