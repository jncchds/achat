namespace AChat.Core.Entities;

public class Bot
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Age { get; set; }
    public string? Gender { get; set; }
    public string CharacterDescription { get; set; } = string.Empty;
    public string EvolvingPersonaPrompt { get; set; } = string.Empty;
    public Guid? LLMProviderPresetId { get; set; }
    public Guid? EmbeddingPresetId { get; set; }
    public string? EncryptedTelegramBotToken { get; set; }
    public string? PersonaPushText { get; set; }
    public int PersonaPushRemainingCycles { get; set; }
    public string? PreferredLanguage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Owner { get; set; } = null!;
    public LLMProviderPreset? LLMProviderPreset { get; set; }
    public LLMProviderPreset? EmbeddingPreset { get; set; }
    public ICollection<BotConversation> Conversations { get; set; } = [];
    public ICollection<BotConversationState> ConversationStates { get; set; } = [];
    public ICollection<Message> Messages { get; set; } = [];
    public ICollection<BotMemorySummary> MemorySummaries { get; set; } = [];
    public ICollection<BotPersonaSnapshot> PersonaSnapshots { get; set; } = [];
    public ICollection<BotAccessList> AccessList { get; set; } = [];
    public ICollection<BotAccessRequest> AccessRequests { get; set; } = [];
    public ICollection<LLMProviderUsageStat> LLMProviderUsageStats { get; set; } = [];
}
