using AChat.Api.Dtos;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

/// <summary>
/// Bot CRUD + conversation timeline (§2.4, §2.3). v1: the current user's bots only —
/// shared access lands in Phase 6.
/// </summary>
[ApiController]
[Authorize]
[Route("api/bots")]
public class BotsController(AppDbContext db) : ControllerBase
{
    private int _userId => int.Parse(User.FindFirst("sub")!.Value);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BotDto>>> List()
    {
        var bots = await _BotsBase().OrderByDescending(b => b.UpdatedAt).ToListAsync();
        var last = await LastMessageByBotAsync(bots.Select(b => b.Id).ToList());
        return Ok(bots.Select(b => ToDto(b, last.GetValueOrDefault(b.Id))).ToList());
    }

    /// <summary>
    /// The last N bots by most recent interaction with the current user (§9.1a) —
    /// the primary way back into a conversation.
    /// </summary>
    [HttpGet("recent")]
    public async Task<ActionResult<IReadOnlyList<BotDto>>> Recent([FromQuery] int n = 10)
    {
        n = Math.Clamp(n, 1, 50);
        var last = await LastMessageByBotAsync(null); // all of the user's timelines

        var botIds = last
            .Where(kv => kv.Value is not null)
            .OrderByDescending(kv => kv.Value!.Value.LastAt)
            .Take(n)
            .Select(kv => kv.Key)
            .ToList();

        var bots = await _BotsBase().Where(b => botIds.Contains(b.Id)).ToListAsync();
        var byId = bots.ToDictionary(b => b.Id);
        return Ok(botIds
            .Where(byId.ContainsKey)
            .Select(id => ToDto(byId[id], last[id]))
            .ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<BotDto>> Get(int id)
    {
        var bot = await _BotsBase().FirstOrDefaultAsync(b => b.Id == id);
        if (bot is null) return NotFound();
        var last = await LastMessageByBotAsync([id]);
        return Ok(ToDto(bot, last.GetValueOrDefault(id)));
    }

    [HttpPost]
    public async Task<ActionResult<BotDto>> Create(BotRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Name is required." });
        if (string.IsNullOrWhiteSpace(req.CorePersona))
            return BadRequest(new { message = "A core description of who this friend is is required (§2.4)." });

        var preset = await db.LlmPresets.FirstOrDefaultAsync(p => p.Id == req.PresetId && p.OwnerId == _userId);
        if (preset is null)
            return BadRequest(new { message = "Choose a preset that belongs to you (§2.2)." });

        var bot = new Bot
        {
            OwnerId = _userId,
            Name = req.Name.Trim(),
            AvatarUrl = Blank(req.AvatarUrl),
            CorePersona = req.CorePersona.Trim(),
            SpeechStyle = Blank(req.SpeechStyle),
            Greeting = Blank(req.Greeting),
            // One code per bot, a GUID, assigned once at creation and stable (§2.5).
            InviteCode = Guid.NewGuid().ToString("N"),
            PresetId = preset.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Bots.Add(bot);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = bot.Id }, ToDto(bot, null));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<BotDto>> Update(int id, BotRequest req)
    {
        var bot = await _BotsBase().FirstOrDefaultAsync(b => b.Id == id);
        if (bot is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Name is required." });

        var preset = await db.LlmPresets.FirstOrDefaultAsync(p => p.Id == req.PresetId && p.OwnerId == _userId);
        if (preset is null)
            return BadRequest(new { message = "Choose a preset that belongs to you (§2.2)." });

        bot.Name = req.Name.Trim();
        bot.AvatarUrl = Blank(req.AvatarUrl);
        bot.CorePersona = req.CorePersona.Trim();
        bot.SpeechStyle = Blank(req.SpeechStyle);
        bot.Greeting = Blank(req.Greeting);
        bot.PresetId = preset.Id;
        bot.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var last = await LastMessageByBotAsync([id]);
        return Ok(ToDto(bot, last.GetValueOrDefault(id)));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var bot = await _BotsBase().FirstOrDefaultAsync(b => b.Id == id);
        if (bot is null) return NotFound();
        db.Bots.Remove(bot);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>The full timeline for this (bot, user) pair, oldest first (§2.3).</summary>
    [HttpGet("{id:int}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageDto>>> Messages(int id)
    {
        var bot = await _BotsBase().FirstOrDefaultAsync(b => b.Id == id);
        if (bot is null) return NotFound();

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.BotId == id && c.UserId == _userId);
        if (conversation is null) return Ok(Array.Empty<MessageDto>());

        var messages = await db.Messages
            .Where(m => m.ConversationId == conversation.Id)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync();

        return Ok(messages.Select(m => new MessageDto
        {
            Id = m.Id,
            Role = m.Role,
            Content = m.Content,
            Reasoning = m.Reasoning,
            IsComplete = m.IsComplete,
            CreatedAt = m.CreatedAt,
        }).ToList());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────
    private IQueryable<Bot> _BotsBase() =>
        db.Bots.Include(b => b.Preset).Where(b => b.OwnerId == _userId);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// For each bot: the most recent message in the current user's timeline with it.
    /// <paramref name="botIds"/> limits the set (null = all).
    /// A bot with no messages is absent from the map (value null).
    /// </summary>
    private async Task<Dictionary<int, (DateTime LastAt, string? Preview)?>> LastMessageByBotAsync(IReadOnlyCollection<int>? botIds)
    {
        var result = new Dictionary<int, (DateTime, string?)?>();

        var conversationQuery = db.Conversations.Where(c => c.UserId == _userId);
        if (botIds is not null)
            conversationQuery = conversationQuery.Where(c => botIds.Contains(c.BotId));

        var maxIds = await db.Messages
            .Where(m => conversationQuery.Select(c => c.Id).Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => new { g.Key, MaxId = g.Max(m => m.Id) })
            .ToDictionaryAsync(x => x.Key, x => x.MaxId);

        if (maxIds.Count == 0) return result;

        var latestIds = maxIds.Values.ToList();
        var latestMessages = await db.Messages
            .Where(m => latestIds.Contains(m.Id))
            .ToListAsync();

        var conversationIds = maxIds.Keys.ToList();
        var conversations = await db.Conversations
            .Where(c => conversationIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.BotId);

        foreach (var m in latestMessages)
        {
            if (!conversations.TryGetValue(m.ConversationId, out var botId)) continue;
            var text = m.Content.Trim();
            result[botId] = (m.CreatedAt, text.Length > 80 ? text[..80] + "…" : text);
        }
        return result;
    }

    private static BotDto ToDto(Bot b, (DateTime LastAt, string? Preview)? last) => new()
    {
        Id = b.Id,
        Name = b.Name,
        AvatarUrl = b.AvatarUrl,
        CorePersona = b.CorePersona,
        SpeechStyle = b.SpeechStyle,
        Greeting = b.Greeting,
        PresetId = b.PresetId,
        PresetName = b.Preset.Name,
        Provider = b.Preset.Provider,
        GenerationModel = b.Preset.GenerationModel,
        CreatedAt = b.CreatedAt,
        UpdatedAt = b.UpdatedAt,
        LastMessageAt = last?.LastAt,
        LastMessagePreview = last?.Preview,
    };
}
