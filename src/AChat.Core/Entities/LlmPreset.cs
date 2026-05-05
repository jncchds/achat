using AChat.Core.Enums;

namespace AChat.Core.Entities;

public class LlmPreset
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProviderType ProviderType { get; set; }
    public string ProviderUrl { get; set; } = string.Empty;
    public string? ApiToken { get; set; }
    public string GenerationModel { get; set; } = string.Empty;
    public string? EmbeddingModel { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
    public ICollection<Bot> Bots { get; set; } = [];
}
