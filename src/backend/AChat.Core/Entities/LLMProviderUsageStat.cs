namespace AChat.Core.Entities;

public class LLMProviderUsageStat
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? BotId { get; set; }
    public Guid? LLMProviderPresetId { get; set; }
    public LLMProvider Provider { get; set; }
    public string? ProviderUrl { get; set; }
    public string PromptModel { get; set; } = string.Empty;
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public Bot? Bot { get; set; }
    public LLMProviderPreset? LLMProviderPreset { get; set; }
}
