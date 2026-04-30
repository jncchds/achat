namespace AChat.Core.LLM;

public class ChatMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public class LLMChatRequest
{
    public string SystemPrompt { get; set; } = string.Empty;
    public IReadOnlyList<ChatMessage> Messages { get; set; } = [];
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}
