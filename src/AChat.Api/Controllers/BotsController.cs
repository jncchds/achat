using AChat.Api.Dtos;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

/// <summary>
/// Bot CRUD + conversation timeline + sharing (§2.4, §2.3, §2.5). Bots are visible to
/// their owner and to approved invitees; bot-admin operations (edit, delete, directives,
/// persona, sharing) are owner-only. Sharing endpoints live under <c>{id}/share</c>.
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
        var bots = await _BotsVisible().OrderByDescending(b => b.UpdatedAt).ToListAsync();
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

        var bots = await _BotsVisible().Where(b => botIds.Contains(b.Id)).ToListAsync();
        var byId = bots.ToDictionary(b => b.Id);
        return Ok(botIds
            .Where(byId.ContainsKey)
            .Select(id => ToDto(byId[id], last[id]))
            .ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<BotDto>> Get(int id)
    {
        var bot = await _VisibleBotAsync(id);
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
        await db.SaveChangesAsync(); // materialises bot.Id

        // The version history starts with the empty baseline (§2.4) — every later
        // rewrite appends, so the full story of the personality is readable.
        db.PersonaVersions.Add(new PersonaVersion
        {
            BotId = bot.Id,
            Content = "",
            Source = PersonaVersionSource.Initial,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = bot.Id }, ToDto(bot, null));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<BotDto>> Update(int id, BotRequest req)
    {
        var bot = await _OwnedBotAsync(id);
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
        if (req.EvolutionEnabled is { } evolve) bot.EvolutionEnabled = evolve;
        bot.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var last = await LastMessageByBotAsync([id]);
        return Ok(ToDto(bot, last.GetValueOrDefault(id)));
    }

    // ── Usage controls (§12a, §2.5) — owner-only: kill switch + quota + usage ──

    /// <summary>
    /// Update the bot's usage controls (§12a, §2.5): the kill switch
    /// (<c>Suspended</c>) and the interaction quota (<c>MaxInteractions</c>, 0 = unlimited).
    /// Partial update — null fields are left unchanged.
    /// </summary>
    [HttpPatch("{id:int}/settings")]
    public async Task<ActionResult<BotDto>> UpdateSettings(int id, [FromBody] BotSettingsRequest req)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();

        if (req.Suspended is { } s) bot.Suspended = s;
        if (req.MaxInteractions is { } cap)
        {
            if (cap < 0) return BadRequest(new { message = "Quota must be 0 (unlimited) or a positive number of interactions." });
            bot.MaxInteractions = cap == 0 ? null : cap;
        }
        if (req.ShowThinking is { } show) bot.ShowThinking = show;
        bot.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var last = await LastMessageByBotAsync([id]);
        return Ok(ToDto(bot, last.GetValueOrDefault(id)));
    }

    /// <summary>
    /// The bot's usage for its creator (§12a, §2.5): totals, the quota cap, the
    /// kill-switch state, and a per-user breakdown of who is consuming the key.
    /// </summary>
    [HttpGet("{id:int}/usage")]
    public async Task<ActionResult<BotUsageDto>> Usage(int id)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();

        var interactions = await db.LlmInteractions.AsNoTracking()
            .Where(i => i.BotId == id)
            .ToListAsync();

        var promptTokens = interactions.Where(i => i.PromptTokens.HasValue).Sum(i => i.PromptTokens!.Value);
        var completionTokens = interactions.Where(i => i.CompletionTokens.HasValue).Sum(i => i.CompletionTokens!.Value);
        var totalTokens = interactions.Where(i => i.TotalTokens.HasValue).Sum(i => i.TotalTokens!.Value);

        var byUser = interactions
            .GroupBy(i => i.UserId)
            .Select(g => new { g.Key, Count = g.Count(),
                              P = g.Where(i => i.PromptTokens.HasValue).Sum(i => i.PromptTokens!.Value),
                              C = g.Where(i => i.CompletionTokens.HasValue).Sum(i => i.CompletionTokens!.Value) })
            .OrderByDescending(x => x.Count)
            .ToList();

        var userIds = byUser.Select(x => x.Key).ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);

        return Ok(new BotUsageDto
        {
            BotId = id,
            BotName = bot.Name,
            Interactions = interactions.Count,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            MaxInteractions = bot.MaxInteractions,
            Suspended = bot.Suspended,
            FirstInteractionAt = interactions.Count > 0 ? interactions.Min(i => i.CreatedAt) : null,
            LastInteractionAt = interactions.Count > 0 ? interactions.Max(i => i.CreatedAt) : null,
            ByUser = byUser.Select(x => new BotUserUsageDto
            {
                UserId = x.Key,
                Username = users.GetValueOrDefault(x.Key)?.Username ?? "?",
                DisplayName = users.GetValueOrDefault(x.Key)?.DisplayName ?? "?",
                IsOwner = x.Key == bot.OwnerId,
                Interactions = x.Count,
                PromptTokens = x.P,
                CompletionTokens = x.C,
            }).ToList(),
        });
    }

    // ── Directives (§2.4) — discrete, individually editable, admin-only ────────

    [HttpGet("{id:int}/directives")]
    public async Task<ActionResult<IReadOnlyList<DirectiveDto>>> ListDirectives(int id)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();
        var rows = await db.BotDirectives
            .Where(d => d.BotId == id)
            .OrderBy(d => d.Id)
            .ToListAsync();
        return Ok(rows.Select(d => new DirectiveDto
        {
            Id = d.Id, Text = d.Text, IsActive = d.IsActive,
            CreatedAt = d.CreatedAt, UpdatedAt = d.UpdatedAt,
        }).ToList());
    }

    [HttpPost("{id:int}/directives")]
    public async Task<ActionResult<DirectiveDto>> AddDirective(int id, DirectiveRequest req)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { message = "A directive needs text." });

        var row = new BotDirective
        {
            BotId = id,
            Text = req.Text.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.BotDirectives.Add(row);
        await db.SaveChangesAsync();
        return Ok(new DirectiveDto
        {
            Id = row.Id, Text = row.Text, IsActive = row.IsActive,
            CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
        });
    }

    [HttpPut("{id:int}/directives/{did:int}")]
    public async Task<ActionResult<DirectiveDto>> UpdateDirective(int id, int did, DirectiveRequest req)
    {
        var row = await db.BotDirectives.FirstOrDefaultAsync(d => d.Id == did && d.BotId == id);
        if (row is null) return NotFound();
        // Partial update: a toggle sends only isActive, an edit sends only text.
        if (!string.IsNullOrWhiteSpace(req.Text)) row.Text = req.Text.Trim();
        if (req.IsActive is { } active) row.IsActive = active;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new DirectiveDto
        {
            Id = row.Id, Text = row.Text, IsActive = row.IsActive,
            CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
        });
    }

    [HttpDelete("{id:int}/directives/{did:int}")]
    public async Task<IActionResult> DeleteDirective(int id, int did)
    {
        var row = await db.BotDirectives.FirstOrDefaultAsync(d => d.Id == did && d.BotId == id);
        if (row is null) return NotFound();
        db.BotDirectives.Remove(row);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── Evolved traits: versioned layer (§2.4) — history, edit, revert ────────

    [HttpGet("{id:int}/persona/versions")]
    public async Task<ActionResult<IReadOnlyList<PersonaVersionDto>>> ListPersonaVersions(int id)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();
        var rows = await db.PersonaVersions
            .Where(v => v.BotId == id)
            .OrderBy(v => v.Id)
            .ToListAsync();
        return Ok(rows.Select(v => new PersonaVersionDto
        {
            Id = v.Id, Content = v.Content, Source = v.Source,
            InteractionId = v.InteractionId, CreatedAt = v.CreatedAt,
        }).ToList());
    }

    /// <summary>
    /// Edit the current evolved-traits layer. Appends a new version flagged
    /// UserEdited — the next evolution pass treats it as the new baseline and
    /// never overwrites the correction (§2.4). Empty content clears the layer.
    /// </summary>
    [HttpPut("{id:int}/persona")]
    public async Task<ActionResult<PersonaVersionDto>> EditPersona(int id, PersonaEditRequest req)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();

        var version = new PersonaVersion
        {
            BotId = id,
            Content = req.Content.Trim(),
            Source = PersonaVersionSource.UserEdited,
            CreatedAt = DateTime.UtcNow,
        };
        db.PersonaVersions.Add(version);
        await db.SaveChangesAsync();
        return Ok(new PersonaVersionDto
        {
            Id = version.Id, Content = version.Content, Source = version.Source,
            InteractionId = version.InteractionId, CreatedAt = version.CreatedAt,
        });
    }

    /// <summary>
    /// Revert to an earlier version. Appends a new version carrying that version's
    /// content (Source = Revert) — the history stays intact and the revert itself
    /// is visible in it (§2.4).
    /// </summary>
    [HttpPost("{id:int}/persona/versions/{vid:int}/revert")]
    public async Task<ActionResult<PersonaVersionDto>> RevertPersona(int id, int vid)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();

        var target = await db.PersonaVersions.FirstOrDefaultAsync(v => v.Id == vid && v.BotId == id);
        if (target is null) return NotFound();

        var version = new PersonaVersion
        {
            BotId = id,
            Content = target.Content,
            Source = PersonaVersionSource.Revert,
            CreatedAt = DateTime.UtcNow,
        };
        db.PersonaVersions.Add(version);
        await db.SaveChangesAsync();
        return Ok(new PersonaVersionDto
        {
            Id = version.Id, Content = version.Content, Source = version.Source,
            InteractionId = version.InteractionId, CreatedAt = version.CreatedAt,
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();
        db.Bots.Remove(bot);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>The full timeline for this (bot, user) pair, oldest first (§2.3).</summary>
    [HttpGet("{id:int}/messages")]
    public async Task<ActionResult<IReadOnlyList<MessageDto>>> Messages(int id)
    {
        var bot = await _VisibleBotAsync(id);
        if (bot is null) return NotFound();

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.BotId == id && c.UserId == _userId);
        if (conversation is null) return Ok(Array.Empty<MessageDto>());

        var messages = await db.Messages
            .Where(m => m.ConversationId == conversation.Id)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync();

        // The chain of thought is shown only when the creator enabled it (§2.5);
        // the row itself always keeps it (§4.4 — the log is the debugging surface).
        return Ok(messages.Select(m => new MessageDto
        {
            Id = m.Id,
            Role = m.Role,
            Content = m.Content,
            Reasoning = bot.ShowThinking ? m.Reasoning : null,
            IsComplete = m.IsComplete,
            CreatedAt = m.CreatedAt,
        }).ToList());
    }

    // ── Sharing (§2.5) — owner-only: invite code, member roster, approval queue ──

    /// <summary>The owner's share view: invite code, member roster, and the pending queue (§2.5).</summary>
    [HttpGet("{id:int}/share")]
    public async Task<ActionResult<BotShareDto>> Share(int id)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();
        return Ok(await BuildShareDtoAsync(bot));
    }

    /// <summary>Regenerate the invite code — the old one stops working for new requests; existing grants and pending requests are untouched (§2.5).</summary>
    [HttpPost("{id:int}/share/regenerate")]
    public async Task<ActionResult<BotShareDto>> RegenerateInvite(int id)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();
        bot.InviteCode = Guid.NewGuid().ToString("N");
        bot.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Ok(await BuildShareDtoAsync(bot));
    }

    /// <summary>Approve a pending request — creates the <see cref="BotAccess"/> grant (§2.5).</summary>
    [HttpPost("{id:int}/share/requests/{rid:int}/approve")]
    public async Task<ActionResult<BotShareDto>> ApproveRequest(int id, int rid)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();

        var req = await db.AccessRequests.FirstOrDefaultAsync(r => r.Id == rid && r.BotId == id);
        if (req is null) return NotFound();
        if (req.Status != AccessRequestStatus.Pending)
            return BadRequest(new { message = "Only a pending request can be approved." });

        req.Status = AccessRequestStatus.Approved;
        req.DecidedAt = DateTime.UtcNow;
        req.DecidedById = _userId;

        // Grant access (idempotent against the (BotId, UserId) unique index).
        var hasAccess = await db.BotAccesses.AnyAsync(a => a.BotId == id && a.UserId == req.RequesterId);
        if (!hasAccess)
        {
            db.BotAccesses.Add(new BotAccess
            {
                BotId = id,
                UserId = req.RequesterId,
                Role = AccessRole.User,
                GrantedAt = DateTime.UtcNow,
                InviteCode = req.InviteCode,
            });
        }
        await db.SaveChangesAsync();
        return Ok(await BuildShareDtoAsync(bot));
    }

    /// <summary>Reject a pending request (§2.5). The requester may re-request later.</summary>
    [HttpPost("{id:int}/share/requests/{rid:int}/reject")]
    public async Task<ActionResult<BotShareDto>> RejectRequest(int id, int rid)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();

        var req = await db.AccessRequests.FirstOrDefaultAsync(r => r.Id == rid && r.BotId == id);
        if (req is null) return NotFound();
        if (req.Status != AccessRequestStatus.Pending)
            return BadRequest(new { message = "Only a pending request can be rejected." });

        req.Status = AccessRequestStatus.Rejected;
        req.DecidedAt = DateTime.UtcNow;
        req.DecidedById = _userId;
        await db.SaveChangesAsync();
        return Ok(await BuildShareDtoAsync(bot));
    }

    /// <summary>
    /// Revoke an invitee's access. The grant is removed; their conversation and memories
    /// are <b>retained</b>, so re-approval restores the relationship (§2.5).
    /// </summary>
    [HttpDelete("{id:int}/share/users/{uid:int}")]
    public async Task<IActionResult> RevokeAccess(int id, int uid)
    {
        var bot = await _OwnedBotAsync(id);
        if (bot is null) return NotFound();
        if (uid == bot.OwnerId)
            return BadRequest(new { message = "You are the owner — you cannot revoke yourself." });

        var access = await db.BotAccesses.FirstOrDefaultAsync(a => a.BotId == id && a.UserId == uid);
        if (access is null) return NotFound();
        db.BotAccesses.Remove(access);
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Assemble the owner's share view (§2.5).</summary>
    private async Task<BotShareDto> BuildShareDtoAsync(Bot bot)
    {
        var owner = await db.Users.AsNoTracking().FirstAsync(u => u.Id == bot.OwnerId);

        var accessRows = await db.BotAccesses.AsNoTracking()
            .Where(a => a.BotId == bot.Id)
            .ToListAsync();
        var memberIds = accessRows.Select(a => a.UserId).ToList();
        var members = await db.Users.AsNoTracking()
            .Where(u => memberIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);

        var pending = await db.AccessRequests.AsNoTracking()
            .Where(r => r.BotId == bot.Id && r.Status == AccessRequestStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
        var requesterIds = pending.Select(r => r.RequesterId).Distinct().ToList();
        var requesters = await db.Users.AsNoTracking()
            .Where(u => requesterIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id);

        return new BotShareDto
        {
            InviteCode = bot.InviteCode,
            Owner = new ShareMemberDto
            {
                UserId = owner.Id,
                Username = owner.Username,
                DisplayName = owner.DisplayName,
                Role = AccessRole.Admin,
                GrantedAt = bot.CreatedAt,
            },
            Members = accessRows
                .OrderBy(a => a.GrantedAt)
                .Where(a => members.ContainsKey(a.UserId))
                .Select(a => new ShareMemberDto
                {
                    UserId = a.UserId,
                    Username = members[a.UserId].Username,
                    DisplayName = members[a.UserId].DisplayName,
                    Role = a.Role,
                    GrantedAt = a.GrantedAt,
                })
                .ToList(),
            Requests = pending.Select(r => new AccessRequestDto
            {
                Id = r.Id,
                BotId = r.BotId,
                BotName = bot.Name,
                RequesterId = r.RequesterId,
                RequesterUsername = requesters.GetValueOrDefault(r.RequesterId)?.Username ?? "?",
                RequesterDisplayName = requesters.GetValueOrDefault(r.RequesterId)?.DisplayName ?? "?",
                Status = r.Status,
                CreatedAt = r.CreatedAt,
                DecidedAt = r.DecidedAt,
            }).ToList(),
        };
    }

    // ── helpers ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Bots the caller may see: their own plus those shared with them via an approved
    /// <see cref="BotAccess"/> (§2.5). Read endpoints use this; bot-admin endpoints
    /// use <see cref="_BotsOwned"/> instead.
    /// </summary>
    private IQueryable<Bot> _BotsVisible() =>
        db.Bots
            .Include(b => b.Preset)
            .Include(b => b.Owner)
            .Where(b => b.OwnerId == _userId
                || db.BotAccesses.Any(a => a.BotId == b.Id && a.UserId == _userId));

    private Task<Bot?> _VisibleBotAsync(int id) =>
        _BotsVisible().FirstOrDefaultAsync(b => b.Id == id);

    /// <summary>The caller's own bots only — bot-admin operations (§2.5).</summary>
    private IQueryable<Bot> _BotsOwned() =>
        db.Bots.Include(b => b.Preset).Where(b => b.OwnerId == _userId);

    private Task<Bot?> _OwnedBotAsync(int id) =>
        _BotsOwned().FirstOrDefaultAsync(b => b.Id == id);

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

    private BotDto ToDto(Bot b, (DateTime LastAt, string? Preview)? last) => new()
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
        EvolutionEnabled = b.EvolutionEnabled,
        ShowThinking = b.ShowThinking,
        CreatedAt = b.CreatedAt,
        UpdatedAt = b.UpdatedAt,
        LastMessageAt = last?.LastAt,
        LastMessagePreview = last?.Preview,
        IsOwner = b.OwnerId == _userId,
        SharedBy = b.OwnerId == _userId ? null : b.Owner.DisplayName,
        Suspended = b.Suspended,
        MaxInteractions = b.MaxInteractions,
    };
}
