using AChat.Core.Enums;

namespace AChat.Core.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
    public long? TelegramId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<LlmPreset> Presets { get; set; } = [];
    public ICollection<Bot> OwnedBots { get; set; } = [];
    public ICollection<Conversation> Conversations { get; set; } = [];
    public ICollection<BotAccessRequest> AccessRequests { get; set; } = [];
    public ICollection<LlmInteraction> LlmInteractions { get; set; } = [];
}
