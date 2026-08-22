using AChat.Api.Dtos;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace AChat.Api.Controllers;

/// <summary>
/// The memory viewer/editor (§5.6) — mandatory, not optional: the only way to answer
/// "why did my friend say that" and the only remedy when extraction records something
/// wrong. Per bot, the user can list, search, edit, pin, and delete their own
/// memories, plus "forget everything".
///
/// Scope rule (§2.5.2): a user's memories are visible only to that user — including to
/// the bot admin. v1 is single-user per bot, so every endpoint here is owner-scoped:
/// rows are always filtered by <c>UserId = the caller</c> and never by bot alone.
///
/// Superseded and deleted rows stay inspectable (§5.6): listing includes them with a
/// flag; "forget everything" soft-deletes rather than removing.
/// </summary>
[ApiController]
[Authorize]
[Route("api/memories")]
public class MemoriesController(AppDbContext db, IApiKeyProtector keys, ILlmProviderFactory factory, ILogger<MemoriesController> log) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly IApiKeyProtector _keys = keys;
    private readonly ILlmProviderFactory _factory = factory;
    private readonly ILogger<MemoriesController> _log = log;

    private int _userId => int.Parse(User.FindFirst("sub")!.Value);

    /// <summary>
    /// List the caller's memories for a bot, most recent first. <c>search</c> is a
    /// case-insensitive substring match on content. Superseded/deleted rows are
    /// included and flagged (inspectable history, §5.6).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MemoryDto>>> List(
        [FromQuery] int botId,
        [FromQuery] string? search)
    {
        var query = db.Memories.AsNoTracking()
            .Where(m => m.BotId == botId && m.UserId == _userId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            query = query.Where(m => EF.Functions.ILike(m.Content, $"%{needle}%"));
        }

        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(500)
            .ToListAsync();
        return Ok(rows.Select(MemoryDto.From).ToList());
    }

    /// <summary>
    /// Edit a memory. The row is flagged <see cref="MemorySource.UserEdited"/> so
    /// extraction never silently overwrites a human correction (§5.6).
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<MemoryDto>> Update(int id, MemoryUpdateRequest req)
    {
        var m = await db.Memories.FirstOrDefaultAsync(x => x.Id == id && x.UserId == _userId);
        if (m is null) return NotFound();
        if (m.IsDeleted) return NotFound();

        var contentChanged = false;
        if (req.Content is not null)
        {
            var content = req.Content.Trim();
            if (content.Length == 0)
                return BadRequest(new { message = "Content cannot be empty." });
            if (content != m.Content)
            {
                m.Content = content;
                contentChanged = true;
            }
        }
        if (req.Kind is { } k) m.Kind = k;
        if (req.Importance is { } imp) m.Importance = Math.Clamp(imp, 1, 5);
        m.Source = MemorySource.UserEdited;

        // A changed content changes what must be findable: re-embed with the bot's
        // preset embedding model so retrieval keeps working on the edit (§5.4).
        if (contentChanged)
        {
            var vector = await ReembedAsync(m);
            if (vector is not null)
            {
                m.Embedding = vector;
                m.EmbeddingDimension = vector.Memory.Length;
            }
            // No embedding model available → keep the old vector; the content is
            // still correct, retrieval just ranks on the previous wording.
        }

        await db.SaveChangesAsync();
        return Ok(MemoryDto.From(m));
    }

    private async Task<Pgvector.Vector?> ReembedAsync(Memory m)
    {
        try
        {
            var preset = await db.Bots
                .Where(b => b.Id == m.BotId)
                .Select(b => b.Preset)
                .FirstOrDefaultAsync();
            if (preset is null || string.IsNullOrWhiteSpace(preset.EmbeddingModel))
                return null;
            if (preset.Provider != LlmProvider.OpenAICompatible)
                return null; // v1 provider

            string? apiKey = null;
            if (preset.ApiKeyEncrypted is not null)
            {
                try { apiKey = _keys.Unprotect(Convert.ToBase64String(preset.ApiKeyEncrypted)); }
                catch { apiKey = null; }
            }

            var provider = _factory.Create(new AChat.Core.Interfaces.LlmPresetConfig
            {
                Provider = preset.Provider,
                Endpoint = preset.BaseEndpoint,
                ApiKey = apiKey,
                GenerationModel = preset.GenerationModel,
                UtilityModel = preset.UtilityModel,
                EmbeddingModel = preset.EmbeddingModel,
                Temperature = preset.Temperature,
                MaxTokens = preset.MaxTokens,
                TimeoutMs = preset.TimeoutMs ?? 120_000,
            });

            var vectors = await provider.EmbedAsync(preset.EmbeddingModel, [m.Content]);
            if (vectors.Count == 0) return null;

            // §5.7: the stored model tag must stay in sync with the vector.
            m.EmbeddingModel = preset.EmbeddingModel;
            return new Pgvector.Vector(vectors[0]);
        }
        catch (Exception ex)
        {
            // Best effort — the edit itself still lands.
            _log.LogWarning(ex, "Re-embedding failed for memory {Id}", m.Id);
            return null;
        }
    }

    /// <summary>Pin / unpin. Pinned memories always surface in the prompt (§5.4).</summary>
    [HttpPut("{id:int}/pin")]
    public async Task<ActionResult<MemoryDto>> Pin(int id, MemoryPinRequest req)
    {
        var m = await db.Memories.FirstOrDefaultAsync(x => x.Id == id && x.UserId == _userId);
        if (m is null) return NotFound();
        if (m.IsDeleted) return NotFound();

        m.IsPinned = req.Pinned;
        await db.SaveChangesAsync();
        return Ok(MemoryDto.From(m));
    }

    /// <summary>Soft-delete — the row stays inspectable (§5.6).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Forget(int id)
    {
        var m = await db.Memories.FirstOrDefaultAsync(x => x.Id == id && x.UserId == _userId);
        if (m is null) return NotFound();
        m.IsDeleted = true;
        await db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>"Forget everything" for this bot — soft-deletes all of the caller's rows.</summary>
    [Route("api/bots/{botId:int}/memories/forget-all")]
    [HttpPost]
    public async Task<IActionResult> ForgetAll(int botId)
    {
        var rows = await db.Memories
            .Where(m => m.BotId == botId && m.UserId == _userId && !m.IsDeleted)
            .ToListAsync();
        foreach (var m in rows) m.IsDeleted = true;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
