namespace AChat.Core.Options;

public class ChatOptions
{
    public const string Section = "Chat";

    /// <summary>
    /// Maximum number of past messages (user + assistant pairs) to include in the LLM context window.
    /// Older messages are dropped to keep token usage bounded.
    /// </summary>
    public int ContextWindowMessages { get; set; } = 50;

    /// <summary>
    /// Number of semantically similar older messages (outside the context window) to retrieve
    /// via vector search and inject into the prompt. Requires EmbeddingModel to be set on the preset.
    /// </summary>
    public int SemanticContextMessages { get; set; } = 5;
}
