using System.Text.Json.Nodes;

namespace AChat.Core.Entities;

public class LlmInteraction
{
    public Guid Id { get; set; }
    public Guid? BotId { get; set; }
    public Guid UserId { get; set; }
    public Guid? PresetId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public string? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }

    public Bot? Bot { get; set; }
    public User User { get; set; } = null!;
    public LlmPreset? Preset { get; set; }
}
