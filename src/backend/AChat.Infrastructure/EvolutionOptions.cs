namespace AChat.Infrastructure;

public class EvolutionOptions
{
    public int SummarizationThreshold { get; set; } = 50;
    public int SummarizationBatchSize { get; set; } = 30;
    public int PersonaEvolutionMessageInterval { get; set; } = 20;
    public int RecentMessageWindowSize { get; set; } = 20;
    public int RagTopK { get; set; } = 5;
    public int PersonaPushDecayCycles { get; set; } = 3;

    /// <summary>
    /// When true, the bot sends an unsolicited message to online users after each persona evolution.
    /// The prompt used is defined by <see cref="BotInitiationPrompt"/>.
    /// </summary>
    public bool BotInitiatesAfterEvolution { get; set; } = false;

    public string BotInitiationPrompt { get; set; } =
        "You've just experienced a shift in your personality. Share a brief, natural thought or feeling that reflects this change — don't explain it, just express it.";
}
