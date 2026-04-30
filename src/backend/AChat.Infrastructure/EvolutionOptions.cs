namespace AChat.Infrastructure;

public class EvolutionOptions
{
    public int SummarizationThreshold { get; set; } = 50;
    public int SummarizationBatchSize { get; set; } = 30;
    public int PersonaEvolutionMessageInterval { get; set; } = 20;
    public int RecentMessageWindowSize { get; set; } = 20;
    public int RagTopK { get; set; } = 5;
    public int PersonaPushDecayCycles { get; set; } = 3;
}
