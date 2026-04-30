using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AChat.Api.Models.Presets;
using AChat.Core.Entities;
using AChat.Core.Services;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/presets")]
[Authorize]
public class PresetsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IEncryptionService _encryption;

    public PresetsController(AppDbContext db, IEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PresetResponse>>> GetAll(CancellationToken ct)
    {
        var userId = GetUserId();
        var presets = await _db.LLMProviderPresets
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return Ok(presets.Select(ToResponse));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PresetResponse>> GetById(Guid id, CancellationToken ct)
    {
        var preset = await _db.LLMProviderPresets.FindAsync([id], ct);
        if (preset is null || preset.UserId != GetUserId()) return NotFound();
        return Ok(ToResponse(preset));
    }

    [HttpPost]
    public async Task<ActionResult<PresetResponse>> Create(CreatePresetRequest req, CancellationToken ct)
    {
        var userId = GetUserId();
        var preset = new LLMProviderPreset
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = req.Name,
            Provider = req.Provider,
            BaseUrl = req.BaseUrl,
            ModelName = req.ModelName,
            EmbeddingModel = req.EmbeddingModel,
            ParametersJson = req.ParametersJson,
            EncryptedApiKey = req.ApiKey is not null ? _encryption.Encrypt(req.ApiKey) : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.LLMProviderPresets.Add(preset);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetById), new { id = preset.Id }, ToResponse(preset));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PresetResponse>> Update(Guid id, UpdatePresetRequest req, CancellationToken ct)
    {
        var preset = await _db.LLMProviderPresets.FindAsync([id], ct);
        if (preset is null || preset.UserId != GetUserId()) return NotFound();

        if (req.Name is not null) preset.Name = req.Name;
        if (req.BaseUrl is not null) preset.BaseUrl = req.BaseUrl;
        if (req.ModelName is not null) preset.ModelName = req.ModelName;
        if (req.EmbeddingModel is not null) preset.EmbeddingModel = req.EmbeddingModel;
        if (req.ParametersJson is not null) preset.ParametersJson = req.ParametersJson;
        if (req.ApiKey is not null) preset.EncryptedApiKey = _encryption.Encrypt(req.ApiKey);
        preset.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(ToResponse(preset));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var preset = await _db.LLMProviderPresets.FindAsync([id], ct);
        if (preset is null || preset.UserId != GetUserId()) return NotFound();

        _db.LLMProviderPresets.Remove(preset);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static PresetResponse ToResponse(LLMProviderPreset p) => new(
        p.Id, p.Name, p.Provider, p.BaseUrl, p.ModelName,
        p.EmbeddingModel, p.ParametersJson,
        HasApiKey: p.EncryptedApiKey is not null,
        p.CreatedAt, p.UpdatedAt);

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
