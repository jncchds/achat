using AChat.Core.Models;

namespace AChat.Api.Dtos;

// ── Auth ────────────────────────────────────────────────────────────────────────
/// <summary>
/// <see cref="Timezone"/> is the IANA zone of the device (e.g. "Europe/Moscow"),
/// captured from the browser and stored on the user (§5.9). Invalid values are
/// ignored; the user can set it explicitly later.
/// </summary>
public sealed record LoginRequest(string Username, string Password, string? Timezone = null);
public sealed record LoginResponse(string Token, UserDto User);
public sealed record TimezoneRequest(string? Timezone);
public sealed record UserDto
{
    public int Id { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsAdmin { get; init; }
    public bool IsActive { get; init; } = true;
    public string? Timezone { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }

    public static UserDto From(User u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        IsAdmin = u.IsAdmin,
        IsActive = u.IsActive,
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
    public bool EvolutionEnabled { get; init; }
    /// <summary>Creator's choice: show the model's chain of thought in chat (§2.5). Default off.</summary>
    public bool ShowThinking { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? LastMessageAt { get; init; }
    public string? LastMessagePreview { get; init; }
    /// <summary>True when the caller is the creator (bot admin, §2.5).</summary>
    public bool IsOwner { get; init; }
    /// <summary>The creator's display name when the bot was shared with the caller.</summary>
    public string? SharedBy { get; init; }
    /// <summary>Kill switch — the creator has paused this bot (§12a, §2.5).</summary>
    public bool Suspended { get; init; }
    /// <summary>Usage quota cap in interactions; null/0 = unlimited (§12a, §2.5).</summary>
    public int? MaxInteractions { get; init; }
}

/// <summary>Owner's settings for usage controls (partial update — null = unchanged).</summary>
public sealed record BotSettingsRequest
{
    public bool? Suspended { get; init; }
    /// <summary>New quota cap in interactions; 0 = unlimited. Null = unchanged.</summary>
    public int? MaxInteractions { get; init; }
    /// <summary>Show the model's chain of thought in chat (§2.5). Null = unchanged.</summary>
    public bool? ShowThinking { get; init; }
}

/// <summary>Per-user usage breakdown within a bot's total (§2.5 — who is spending the creator's key).</summary>
public sealed record BotUserUsageDto
{
    public int UserId { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsOwner { get; init; }
    public int Interactions { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
}

/// <summary>
/// A bot's usage summary for its creator (§12a, §2.5): totals, the quota cap, the
/// kill-switch state, and a per-user breakdown of who is consuming the key.
/// </summary>
public sealed record BotUsageDto
{
    public int BotId { get; init; }
    public string BotName { get; init; } = "";
    public int Interactions { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long TotalTokens { get; init; }
    public int? MaxInteractions { get; init; }
    public bool Suspended { get; init; }
    public DateTime? FirstInteractionAt { get; init; }
    public DateTime? LastInteractionAt { get; init; }
    public IReadOnlyList<BotUserUsageDto> ByUser { get; init; } = Array.Empty<BotUserUsageDto>();
}

public sealed record BotRequest
{
    public string Name { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public string CorePersona { get; init; } = "";
    public string? SpeechStyle { get; init; }
    public string? Greeting { get; init; }
    public int PresetId { get; init; }
    /// <summary>Only applied on update (create defaults to on).</summary>
    public bool? EvolutionEnabled { get; init; }
}

public sealed record DirectiveDto
{
    public int Id { get; init; }
    public string Text { get; init; } = "";
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record DirectiveRequest
{
    public string Text { get; init; } = "";
    public bool? IsActive { get; init; }
}

public sealed record PersonaVersionDto
{
    public int Id { get; init; }
    public string Content { get; init; } = "";
    public PersonaVersionSource Source { get; init; }
    public int? InteractionId { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record PersonaEditRequest
{
    public string Content { get; init; } = "";
}

public sealed record MemoryDto
{
    public int Id { get; init; }
    public string Content { get; init; } = "";
    public MemoryKind Kind { get; init; }
    public int Importance { get; init; }
    public bool IsPinned { get; init; }
    public MemorySource Source { get; init; }
    public int? SourceMessageId { get; init; }
    public int? SupersededByMemoryId { get; init; }
    public bool IsDeleted { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastRecalledAt { get; init; }
    public int RecallCount { get; init; }

    public static MemoryDto From(Memory m) => new()
    {
        Id = m.Id,
        Content = m.Content,
        Kind = m.Kind,
        Importance = m.Importance,
        IsPinned = m.IsPinned,
        Source = m.Source,
        SourceMessageId = m.SourceMessageId,
        SupersededByMemoryId = m.SupersededByMemoryId,
        IsDeleted = m.IsDeleted,
        CreatedAt = m.CreatedAt,
        LastRecalledAt = m.LastRecalledAt,
        RecallCount = m.RecallCount,
    };
}

public sealed record MemoryUpdateRequest
{
    public string? Content { get; init; }
    public MemoryKind? Kind { get; init; }
    public int? Importance { get; init; }
}

public sealed record MemoryPinRequest(bool Pinned);

// ── Sharing (§2.5) ─────────────────────────────────────────────────────────────
/// <summary>
/// The owner's share view of a bot: the invite code, the member roster (owner +
/// invitees), and the access requests in the approval queue.
/// </summary>
public sealed record BotShareDto
{
    public string InviteCode { get; init; } = "";
    public ShareMemberDto Owner { get; init; } = null!;
    public List<ShareMemberDto> Members { get; init; } = new();
    public List<AccessRequestDto> Requests { get; init; } = new();
}

public sealed record ShareMemberDto
{
    public int UserId { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public AccessRole Role { get; init; }
    public DateTime GrantedAt { get; init; }
}

public sealed record AccessRequestDto
{
    public int Id { get; init; }
    public int BotId { get; init; }
    public string? BotName { get; init; }
    public int RequesterId { get; init; }
    public string RequesterUsername { get; init; } = "";
    public string RequesterDisplayName { get; init; } = "";
    public AccessRequestStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? DecidedAt { get; init; }
}

public sealed record AccessRequestByCode(string Code);

/// <summary>
/// The result of presenting an invite code (<c>POST /api/access/request</c>).
/// <see cref="State"/> is "pending" (new request), "reopened" (a rejected request was
/// re-opened), or "already" (the caller already has access — no request row).
/// </summary>
public sealed record AccessRequestResponseDto
{
    public string State { get; init; } = "";
    public int BotId { get; init; }
    public string BotName { get; init; } = "";
    public AccessRequestDto? Request { get; init; }
}

/// <summary>Admin creates a user (§7.2 — no self-service registration).</summary>
public sealed record CreateUserRequest
{
    public string Username { get; init; } = "";
    public string? DisplayName { get; init; }
    public string Password { get; init; } = "";
}

/// <summary>Admin sets a new password for a user (§7.4).</summary>
public sealed record ResetPasswordRequest
{
    public string Password { get; init; } = "";
}

/// <summary>Admin enables or disables a user account (§7.4).</summary>
public sealed record SetActiveRequest
{
    public bool IsActive { get; init; }
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
    /// <summary>JSON array of recalled memory ids ("[3,7]"), null when retrieval ran nothing.</summary>
    public string? RecalledMemoryIds { get; init; }
    public int? BotId { get; init; }
    public int UserId { get; init; }
    public int? PresetId { get; init; }
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

// ── Durable work queue (§12b) ──────────────────────────────────────────────────
/// <summary>
/// A background work item in the durable queue (§12b), for the admin "background
/// work" panel. The bot is resolved from the item's conversation.
/// </summary>
public sealed record WorkItemDto
{
    public int Id { get; init; }
    /// <summary>Extraction | Evolution | Summary.</summary>
    public string Kind { get; init; } = "";
    public int ConversationId { get; init; }
    public int BotId { get; init; }
    public string BotName { get; init; } = "";
    /// <summary>Pending | Running | Dead.</summary>
    public string Status { get; init; } = "";
    public int Attempts { get; init; }
    public int MaxAttempts { get; init; }
    public DateTime NextTryAt { get; init; }
    public string? LastFailure { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
