namespace AChat.Core.Options;

public class BotEvolutionOptions
{
    public const string Section = "BotEvolution";

    public int IntervalHours { get; set; } = 24;
    public int MinOwnerMessagesRequired { get; set; } = 10;
    public int MaxMessagesContext { get; set; } = 50;
}
