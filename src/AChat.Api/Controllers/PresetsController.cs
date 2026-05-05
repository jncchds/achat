using System.Security.Claims;
using AChat.Core.DTOs.Presets;
using AChat.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AChat.Api.Controllers;

[ApiController]
[Route("api/presets")]
[Authorize]
public class PresetsController(IPresetService presetService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) =>
        Ok(await presetService.GetUserPresetsAsync(GetUserId(), ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var result = await presetService.GetPresetAsync(id, GetUserId(), ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePresetRequest request, CancellationToken ct)
    {
        var result = await presetService.CreatePresetAsync(GetUserId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePresetRequest request, CancellationToken ct)
    {
        var result = await presetService.UpdatePresetAsync(id, GetUserId(), request, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var success = await presetService.DeletePresetAsync(id, GetUserId(), ct);
        return success ? NoContent() : NotFound();
    }

    [HttpGet("{id:guid}/models")]
    public async Task<IActionResult> GetModels(Guid id, CancellationToken ct) =>
        Ok(await presetService.GetModelsAsync(id, GetUserId(), ct));

    private Guid GetUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
