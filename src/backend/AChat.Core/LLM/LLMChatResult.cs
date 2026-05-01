namespace AChat.Core.LLM;

public sealed class LLMTokenUsageStats
{
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
}

public sealed class LLMChatCompletionResult
{
    public string Content { get; init; } = string.Empty;
    public LLMTokenUsageStats? Usage { get; init; }
}

public sealed class LLMChatStreamUpdate
{
    public string? Content { get; init; }
    public LLMTokenUsageStats? Usage { get; init; }
}
