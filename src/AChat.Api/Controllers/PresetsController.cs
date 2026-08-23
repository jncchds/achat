using AChat.Api.Dtos;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Controllers;

/// <summary>
/// Preset CRUD + load-models + test connection (§3). API keys are write-only:
/// never returned in plaintext; at most a masked tail (§7.3).
/// </summary>
[ApiController]
[Authorize]
[Route("api/presets")]
public class PresetsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IApiKeyProtector _keys;
    private readonly ILlmProviderFactory _factory;

    public PresetsController(AppDbContext db, IApiKeyProtector keys, ILlmProviderFactory factory)
    {
        _db = db;
        _keys = keys;
        _factory = factory;
    }

    private int _userId => int.Parse(User.FindFirst("sub")!.Value);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PresetDto>>> List()
    {
        var presets = await _db.LlmPresets
            .Where(p => p.OwnerId == _userId)
            .OrderBy(p => p.Name)
            .ToListAsync();
        return Ok(presets.Select(ToDto).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<PresetDto>> Get(int id)
    {
        var preset = await _db.LlmPresets.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == _userId);
        return preset is null ? NotFound() : Ok(ToDto(preset));
    }

    [HttpPost]
    public async Task<ActionResult<PresetDto>> Create(PresetRequest req)
    {
        var errors = Validate(req, creating: true);
        if (errors is not null) return BadRequest(new { message = errors });

        var preset = new LlmPreset
        {
            OwnerId = _userId,
            Name = req.Name.Trim(),
            Provider = req.Provider,
            BaseEndpoint = req.BaseEndpoint.Trim(),
            ApiKeyEncrypted = null,
            GenerationModel = req.GenerationModel.Trim(),
            UtilityModel = BlankToNull(req.UtilityModel),
            EmbeddingModel = BlankToNull(req.EmbeddingModel),
            Temperature = req.Temperature,
            MaxTokens = req.MaxTokens,
            TimeoutMs = req.TimeoutMs,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        StoreKey(preset, req.ApiKey);
        _db.LlmPresets.Add(preset);
        await _db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = preset.Id }, ToDto(preset));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<PresetDto>> Update(int id, PresetRequest req)
    {
        var preset = await _db.LlmPresets.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == _userId);
        if (preset is null) return NotFound();

        // §5.7 — the embedding model of a preset cannot be changed while any bot uses
        // that preset. The field is locked in the editor; enforce it here too.
        var botsUsing = await _db.Bots.CountAsync(b => b.PresetId == id);
        if (botsUsing > 0 &&
            !string.Equals(BlankToNull(req.EmbeddingModel), BlankToNull(preset.EmbeddingModel), StringComparison.Ordinal))
        {
            return Conflict(new
            {
                message = $"This preset's embedding model is locked while {botsUsing} bot(s) use it. " +
                          "Detach or delete those bots first (§5.7).",
            });
        }

        var errors = Validate(req, creating: false);
        if (errors is not null) return BadRequest(new { message = errors });

        preset.Name = req.Name.Trim();
        preset.Provider = req.Provider;
        preset.BaseEndpoint = req.BaseEndpoint.Trim();
        preset.GenerationModel = req.GenerationModel.Trim();
        preset.UtilityModel = BlankToNull(req.UtilityModel);
        preset.EmbeddingModel = BlankToNull(req.EmbeddingModel);
        preset.Temperature = req.Temperature;
        preset.MaxTokens = req.MaxTokens;
        preset.TimeoutMs = req.TimeoutMs;
        preset.UpdatedAt = DateTime.UtcNow;

        if (req.ClearApiKey)
            preset.ApiKeyEncrypted = null;
        else if (!string.IsNullOrWhiteSpace(req.ApiKey))
            StoreKey(preset, req.ApiKey);
        // blank + no clear flag → leave unchanged (§7.3)

        await _db.SaveChangesAsync();
        return Ok(ToDto(preset));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var preset = await _db.LlmPresets.FirstOrDefaultAsync(p => p.Id == id && p.OwnerId == _userId);
        if (preset is null) return NotFound();

        var botsUsing = await _db.Bots.CountAsync(b => b.PresetId == id);
        if (botsUsing > 0)
            return Conflict(new { message = $"Cannot delete: {botsUsing} bot(s) use this preset." });

        _db.LlmPresets.Remove(preset);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>"Load models" action (§3) — queries the provider with the given (draft) config.</summary>
    [HttpPost("load-models")]
    public async Task<ActionResult<IReadOnlyList<ModelInfoDto>>> LoadModels(PresetTestRequest req)
    {
        try
        {
            var provider = CreateDraftProvider(req);
            var models = await provider.ListModelsAsync();
            return Ok(models.Select(m => new ModelInfoDto(m.Id, m.Name)).ToList());
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = Describe(ex) });
        }
    }

    /// <summary>"Test connection" action (§3) — list models as a cheap round-trip.</summary>
    [HttpPost("test")]
    public async Task<ActionResult<PresetTestResult>> Test(PresetTestRequest req)
    {
        try
        {
            var provider = CreateDraftProvider(req);
            var models = await provider.ListModelsAsync();
            var hasModel = !string.IsNullOrWhiteSpace(req.GenerationModel)
                && models.Any(m => m.Id == req.GenerationModel);
            return Ok(new PresetTestResult(true,
                hasModel
                    ? $"Connected. Found {models.Count} model(s); '{req.GenerationModel}' is listed."
                    : $"Connected. Found {models.Count} model(s)" +
                      (string.IsNullOrWhiteSpace(req.GenerationModel) ? "." : $", but '{req.GenerationModel}' is not among them.")));
        }
        catch (Exception ex)
        {
            return Ok(new PresetTestResult(false, Describe(ex)));
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────
    private Core.Interfaces.ILlmProvider CreateDraftProvider(PresetTestRequest req)
    {
        if (!_factory.IsSupported(req.Provider))
            throw new NotSupportedException($"Provider '{req.Provider}' has no registered strategy in this build.");

        // If the key was left blank on an existing preset, fall back to the stored one.
        string? apiKey = req.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var stored = _db.LlmPresets
                .Where(p => p.OwnerId == _userId && p.ApiKeyEncrypted != null)
                .Select(p => p.ApiKeyEncrypted)
                .FirstOrDefault();
            if (stored is not null)
                try { apiKey = _keys.Unprotect(Convert.ToBase64String(stored)); } catch { apiKey = null; }
        }

        return _factory.Create(new LlmPresetConfig
        {
            Provider = req.Provider,
            Endpoint = req.BaseEndpoint,
            ApiKey = apiKey,
            GenerationModel = req.GenerationModel,
            TimeoutMs = 15_000,
        });
    }

    private static string Describe(Exception ex) =>
        ex switch
        {
            Core.LlmProviderException lpex => lpex.HttpStatus is { } s
                ? $"Provider returned HTTP {s}: {lpex.Message}"
                : $"{lpex.Message}: {lpex.ErrorPayload}",
            NotSupportedException => ex.Message,
            _ => ex.Message,
        };

    private void StoreKey(LlmPreset preset, string? apiKey)
    {
        preset.ApiKeyEncrypted = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : Convert.FromBase64String(_keys.Protect(apiKey)!);
    }

    private static string? Validate(PresetRequest req, bool creating)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return "Name is required.";
        if (string.IsNullOrWhiteSpace(req.GenerationModel)) return "Generation model is required.";
        if (req.Temperature is < 0f or > 2f) return "Temperature must be between 0 and 2.";
        return null;
    }

    private static string? BlankToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private PresetDto ToDto(LlmPreset p)
    {
        string? masked = null;
        if (p.ApiKeyEncrypted is not null)
        {
            try
            {
                var plain = _keys.Unprotect(Convert.ToBase64String(p.ApiKeyEncrypted));
                masked = IApiKeyProtector.Mask(plain);
            }
            catch
            {
                masked = "…(key ring unavailable)";
            }
        }
        return new PresetDto
        {
            Id = p.Id,
            Name = p.Name,
            Provider = p.Provider,
            BaseEndpoint = p.BaseEndpoint,
            HasApiKey = p.ApiKeyEncrypted is not null,
            MaskedApiKey = masked,
            GenerationModel = p.GenerationModel,
            UtilityModel = p.UtilityModel,
            EmbeddingModel = p.EmbeddingModel,
            Temperature = p.Temperature,
            MaxTokens = p.MaxTokens,
            TimeoutMs = p.TimeoutMs,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        };
    }
}
