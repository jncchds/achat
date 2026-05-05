namespace AChat.Core.Entities;

public class BotEvolutionLog
{
    public Guid Id { get; set; }
    public Guid BotId { get; set; }
    public string OldPersonality { get; set; } = string.Empty;
    public string NewPersonality { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public string? Direction { get; set; }
    public DateTime EvolvedAt { get; set; }

    public Bot Bot { get; set; } = null!;
}
