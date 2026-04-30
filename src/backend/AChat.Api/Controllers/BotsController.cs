using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AChat.Api.Models.Bots;
using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Core.Services;
using AChat.Infrastructure;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.Telegram;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/bots")]
[Authorize]
public class BotsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEncryptionService _encryption;
    private readonly EvolutionOptions _evolutionOptions;
    private readonly ILLMProviderFactory _llmFactory;

    public BotsController(
        AppDbContext db,
        IEncryptionService encryption,
        IOptions<EvolutionOptions> evolutionOptions,
        ILLMProviderFactory llmFactory)
    {
        _db = db;
        _encryption = encryption;
        _evolutionOptions = evolutionOptions.Value;
        _llmFactory = llmFactory;
    }

    [HttpPost("randomize-persona")]
    public async Task<ActionResult<RandomizePersonaResponse>> RandomizePersona(
        RandomizePersonaRequest req,
        CancellationToken ct)
    {
        var userId = GetUserId();

        if (!req.PresetId.HasValue)
            return BadRequest("PresetId is required for LLM-generated personas.");

        var preset = await _db.LLMProviderPresets
            .FirstOrDefaultAsync(p => p.Id == req.PresetId && p.UserId == userId, ct);
        if (preset is null)
            return BadRequest("Preset not found or does not belong to this user.");

        // Use a random archetype seed to ensure variety across calls
        var seeds = new[]
        {
            "an eccentric scientist obsessed with a niche field",
            "a weathered traveller who has lived in many cultures",
            "a retired performer with a flair for drama",
            "a quiet introvert with surprisingly deep opinions",
            "a mischievous trickster who enjoys wordplay",
            "a stoic philosopher with dry wit",
            "an enthusiastic collector of obscure knowledge",
            "a warm mentor who speaks in metaphors",
            "a cynical realist with a hidden soft side",
            "a dreamer who romanticises the mundane",
            "a sharp-tongued critic who secretly craves connection",
            "an over-eager helper prone to tangents",
            "a gruff but fair old sailor",
            "a whimsical poet with a chaotic worldview",
            "a pragmatic engineer who sees everything as a system",
        };
        var seed = seeds[Random.Shared.Next(seeds.Length)];

        var chatRequest = new LLMChatRequest
        {
            SystemPrompt = "You are a creative character writer. Generate a unique, vivid chatbot personality description. " +
                           "Include: distinctive traits, quirks, communication style, background hints, and what topics interest them. " +
                           "Write in second-person-free prose (do not use 'you'). " +
                           "Keep it 2–3 paragraphs. Return only the description — no titles, no meta-commentary.",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = $"Create a chatbot character inspired by this seed concept: {seed}. " +
                               "Make it original — the seed is just a starting spark, not a constraint."
                }
            ]
        };

        var provider = _llmFactory.GetChatProvider(preset);
        var description = await provider.GenerateChatAsync(chatRequest, ct);

        return Ok(new RandomizePersonaResponse(description.Trim()));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<BotResponse>>> GetAll(CancellationToken ct)
    {
        var userId = GetUserId();
        var bots = await _db.Bots
            .Where(b => b.OwnerId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        return Ok(bots.Select(ToResponse));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BotResponse>> GetById(Guid id, CancellationToken ct)
    {
        var bot = await _db.Bots.FindAsync([id], ct);
        if (bot is null || bot.OwnerId != GetUserId()) return NotFound();
        return Ok(ToResponse(bot));
    }

    [HttpPost]
    public async Task<ActionResult<BotResponse>> Create(
        CreateBotRequest req,
        [FromServices] ITelegramWebhookService webhookService,
        CancellationToken ct)
    {
        var userId = GetUserId();

        // Validate preset ownership
        if (req.LLMProviderPresetId.HasValue &&
            !await _db.LLMProviderPresets.AnyAsync(p => p.Id == req.LLMProviderPresetId && p.UserId == userId, ct))
            return BadRequest("LLMProviderPresetId does not belong to this user.");

        if (req.EmbeddingPresetId.HasValue &&
            !await _db.LLMProviderPresets.AnyAsync(p => p.Id == req.EmbeddingPresetId && p.UserId == userId, ct))
            return BadRequest("EmbeddingPresetId does not belong to this user.");

        var bot = new Bot
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            Name = req.Name,
            Age = req.Age,
            Gender = req.Gender,
            CharacterDescription = req.CharacterDescription,
            EvolvingPersonaPrompt = req.CharacterDescription,
            PreferredLanguage = req.PreferredLanguage,
            LLMProviderPresetId = req.LLMProviderPresetId,
            EmbeddingPresetId = req.EmbeddingPresetId,
            EncryptedTelegramBotToken = req.TelegramBotToken is not null
                ? _encryption.Encrypt(req.TelegramBotToken) : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Bots.Add(bot);

        // Auto-add owner to access list when Telegram token is set
        if (bot.EncryptedTelegramBotToken is not null)
        {
            var owner = await _db.Users.FindAsync([userId], ct);
            if (owner?.TelegramId is not null)
                AddOwnerToAccessList(bot, owner.TelegramId.Value);
        }

        await _db.SaveChangesAsync(ct);

        // Register Telegram webhook if token provided
        if (req.TelegramBotToken is not null)
            await webhookService.RegisterWebhookAsync(bot.Id, req.TelegramBotToken, ct);

        return CreatedAtAction(nameof(GetById), new { id = bot.Id }, ToResponse(bot));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<BotResponse>> Update(
        Guid id,
        UpdateBotRequest req,
        [FromServices] ITelegramWebhookService webhookService,
        CancellationToken ct)
    {
        var userId = GetUserId();
        var bot = await _db.Bots.FindAsync([id], ct);
        if (bot is null || bot.OwnerId != userId) return NotFound();

        if (req.LLMProviderPresetId.HasValue &&
            !await _db.LLMProviderPresets.AnyAsync(p => p.Id == req.LLMProviderPresetId && p.UserId == userId, ct))
            return BadRequest("LLMProviderPresetId does not belong to this user.");

        if (req.EmbeddingPresetId.HasValue &&
            !await _db.LLMProviderPresets.AnyAsync(p => p.Id == req.EmbeddingPresetId && p.UserId == userId, ct))
            return BadRequest("EmbeddingPresetId does not belong to this user.");

        if (req.Name is not null) bot.Name = req.Name;
        if (req.Age.HasValue) bot.Age = req.Age;
        if (req.Gender is not null) bot.Gender = req.Gender;
        // Allow clearing PreferredLanguage by passing empty string
        if (req.PreferredLanguage is not null)
            bot.PreferredLanguage = string.IsNullOrWhiteSpace(req.PreferredLanguage) ? null : req.PreferredLanguage;
        if (req.CharacterDescription is not null && req.CharacterDescription != bot.CharacterDescription)
        {
            bot.CharacterDescription = req.CharacterDescription;
            // Changing the foundation reseeds the evolved persona and clears any active push
            bot.EvolvingPersonaPrompt = req.CharacterDescription;
            bot.PersonaPushText = null;
            bot.PersonaPushRemainingCycles = 0;
        }
        if (req.LLMProviderPresetId.HasValue) bot.LLMProviderPresetId = req.LLMProviderPresetId;
        if (req.EmbeddingPresetId.HasValue) bot.EmbeddingPresetId = req.EmbeddingPresetId;

        bool tokenChanged = false;
        string? newRawToken = null;
        if (req.TelegramBotToken is not null)
        {
            var encryptedNew = _encryption.Encrypt(req.TelegramBotToken);
            if (encryptedNew != bot.EncryptedTelegramBotToken)
            {
                bot.EncryptedTelegramBotToken = encryptedNew;
                newRawToken = req.TelegramBotToken;
                tokenChanged = true;

                // Ensure owner is in access list when token is first set
                var existingEntry = await _db.BotAccessLists
                    .AnyAsync(a => a.BotId == id && a.SubjectType == AccessSubjectType.AchatUser
                                                  && a.SubjectId == userId.ToString(), ct);
                if (!existingEntry)
                {
                    var owner = await _db.Users.FindAsync([userId], ct);
                    if (owner?.TelegramId is not null)
                        AddOwnerToAccessList(bot, owner.TelegramId.Value);
                    else
                    {
                        _db.BotAccessLists.Add(new BotAccessList
                        {
                            Id = Guid.NewGuid(),
                            BotId = bot.Id,
                            SubjectType = AccessSubjectType.AchatUser,
                            SubjectId = userId.ToString(),
                            Status = AccessStatus.Allowed,
                            AddedAt = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        bot.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (tokenChanged && newRawToken is not null)
            await webhookService.RegisterWebhookAsync(bot.Id, newRawToken, ct);

        return Ok(ToResponse(bot));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var bot = await _db.Bots.FindAsync([id], ct);
        if (bot is null || bot.OwnerId != GetUserId()) return NotFound();
        _db.Bots.Remove(bot);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/persona-push")]
    public async Task<IActionResult> PersonaPush(Guid id, PersonaPushRequest req, CancellationToken ct)
    {
        var bot = await _db.Bots.FindAsync([id], ct);
        if (bot is null || bot.OwnerId != GetUserId()) return NotFound();

        bot.PersonaPushText = req.Direction;
        bot.PersonaPushRemainingCycles = _evolutionOptions.PersonaPushDecayCycles;
        bot.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(ToResponse(bot));
    }

    [HttpDelete("{id:guid}/persona-push")]
    public async Task<IActionResult> ClearPersonaPush(Guid id, CancellationToken ct)
    {
        var bot = await _db.Bots.FindAsync([id], ct);
        if (bot is null || bot.OwnerId != GetUserId()) return NotFound();

        bot.PersonaPushText = null;
        bot.PersonaPushRemainingCycles = 0;
        bot.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpGet("{id:guid}/persona-history")]
    public async Task<IActionResult> GetPersonaHistory(Guid id, CancellationToken ct)
    {
        var bot = await _db.Bots.FindAsync([id], ct);
        if (bot is null || bot.OwnerId != GetUserId()) return NotFound();

        var snapshots = await _db.BotPersonaSnapshots
            .Where(s => s.BotId == id)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.SnapshotText, s.CreatedAt })
            .ToListAsync(ct);

        return Ok(snapshots);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void AddOwnerToAccessList(Bot bot, long telegramId)
    {
        _db.BotAccessLists.Add(new BotAccessList
        {
            Id = Guid.NewGuid(),
            BotId = bot.Id,
            SubjectType = AccessSubjectType.TelegramUser,
            SubjectId = telegramId.ToString(),
            Status = AccessStatus.Allowed,
            AddedAt = DateTime.UtcNow
        });
    }

    private static BotResponse ToResponse(Bot b) => new(
        b.Id, b.Name, b.Age, b.Gender,
        b.CharacterDescription, b.EvolvingPersonaPrompt,
        b.PersonaPushText, b.PersonaPushRemainingCycles,
        b.PreferredLanguage,
        b.LLMProviderPresetId, b.EmbeddingPresetId,
        HasTelegramToken: b.EncryptedTelegramBotToken is not null,
        b.CreatedAt, b.UpdatedAt);

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
