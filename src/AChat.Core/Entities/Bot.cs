namespace AChat.Core.Entities;

public class Bot
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid PresetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string? TelegramToken { get; set; }
    public string UnknownUserReply { get; set; } = "I don't know you, go away";
    public string? Gender { get; set; }
    public string? Language { get; set; }
    public int? EvolutionIntervalHours { get; set; }
    public DateTime? LastEvolvedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Owner { get; set; } = null!;
    public LlmPreset Preset { get; set; } = null!;
    public ICollection<Conversation> Conversations { get; set; } = [];
    public ICollection<BotAccessRequest> AccessRequests { get; set; } = [];
    public ICollection<BotUserMemory> UserMemories { get; set; } = [];
    public ICollection<LlmInteraction> LlmInteractions { get; set; } = [];
}
