namespace AChat.Core.Entities;

public class LLMProviderPreset
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public LLMProvider Provider { get; set; }
    public string? EncryptedApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string? EmbeddingModel { get; set; }
    public string? ParametersJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
