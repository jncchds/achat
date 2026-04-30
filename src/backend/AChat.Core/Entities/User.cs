namespace AChat.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    public long? TelegramId { get; set; }
    public bool IsStubAccount { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<LLMProviderPreset> Presets { get; set; } = [];
    public ICollection<Bot> Bots { get; set; } = [];
}
