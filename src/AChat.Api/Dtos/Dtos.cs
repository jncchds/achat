using AChat.Core.Models;

namespace AChat.Api.Dtos;

// ── Auth ────────────────────────────────────────────────────────────────────────
public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, UserDto User);
public sealed record UserDto
{
    public int Id { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsAdmin { get; init; }
    public string? Timezone { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }

    public static UserDto From(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        IsAdmin = u.IsAdmin,
        Timezone = u.Timezone,
        CreatedAt = u.CreatedAt,
        LastLoginAt = u.LastLoginAt,
    };
}

// ── Presets ─────────────────────────────────────────────────────────────────────
public sealed record PresetDto
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public LlmProvider Provider { get; init; }
    public string BaseEndpoint { get; init; } = "";
    public bool HasApiKey { get; init; }
    public string? MaskedApiKey { get; init; }
    public string GenerationModel { get; init; } = "";
    public string? UtilityModel { get; init; }
    public string? EmbeddingModel { get; init; }
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int? TimeoutMs { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record PresetRequest
{
    public string Name { get; init; } = "";
    public LlmProvider Provider { get; init; } = LlmProvider.OpenAICompatible;
    public string BaseEndpoint { get; init; } = "";
    /// <summary>
    /// The plaintext key. On create: stored. On update: blank = leave unchanged
    /// (§7.3) — clearing needs <see cref="ClearApiKey"/>.
    /// </summary>
    public string? ApiKey { get; init; }
    public bool ClearApiKey { get; init; }
    public string GenerationModel { get; init; } = "";
    public string? UtilityModel { get; init; }
    public string? EmbeddingModel { get; init; }
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public int? TimeoutMs { get; init; }
}

public sealed record PresetTestRequest
{
    public string BaseEndpoint { get; init; } = "";
    public string? ApiKey { get; init; }
    public string GenerationModel { get; init; } = "";
    public LlmProvider Provider { get; init; } = LlmProvider.OpenAICompatible;
}

public sealed record PresetTestResult(bool Ok, string Message);
public sealed record ModelInfoDto(string Id, string? Name);

// ── Bots ────────────────────────────────────────────────────────────────────────
public sealed record BotDto
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public string CorePersona { get; init; } = "";
    public string? SpeechStyle { get; init; }
    public string? Greeting { get; init; }
    public int PresetId { get; init; }
    public string PresetName { get; init; } = "";
    public LlmProvider Provider { get; init; }
    public string GenerationModel { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? LastMessageAt { get; init; }
    public string? LastMessagePreview { get; init; }
}

public sealed record BotRequest
{
    public string Name { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public string CorePersona { get; init; } = "";
    public string? SpeechStyle { get; init; }
    public string? Greeting { get; init; }
    public int PresetId { get; init; }
}

public sealed record MessageDto
{
    public int Id { get; init; }
    public MessageRole Role { get; init; }
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public bool IsComplete { get; init; } = true;
    public DateTime CreatedAt { get; init; }
}

// ── Interactions (§4.4) ─────────────────────────────────────────────────────────
public sealed record InteractionDto
{
    public int Id { get; init; }
    public InteractionPurpose Purpose { get; init; }
    public int? BotId { get; init; }
    public string? BotName { get; init; }
    public int UserId { get; init; }
    public string Username { get; init; } = "";
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
    public string? FinishReason { get; init; }
    public bool Failed { get; init; }
    public int? HttpStatus { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record InteractionDetailDto
{
    public int Id { get; init; }
    public InteractionPurpose Purpose { get; init; }
    public int? BotId { get; init; }
    public int UserId { get; init; }
    public int PresetId { get; init; }
    public LlmProvider Provider { get; init; }
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string RequestBody { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ResponseContent { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
    public string? FinishReason { get; init; }
    public bool Failed { get; init; }
    public string? FailureReason { get; init; }
    public DateTime CreatedAt { get; init; }
}
