namespace AChat.Core.Models;

/// <summary>Provider families. All four are OpenAI-shaped and registered as strategies.</summary>
public enum LlmProvider
{
    Ollama,
    OpenAI,
    OpenAICompatible,
    GoogleAIStudio,
}

/// <summary>
/// Role of a message in the conversation timeline. Kept open enough that ToolCall /
/// ToolResult roles can be added later without migrating existing rows (§4.6).
/// </summary>
public enum MessageRole
{
    User,
    Assistant,
    System,
}

/// <summary>Purpose of a recorded LLM interaction (§4.4).</summary>
public enum InteractionPurpose
{
    Chat,
    Embedding,
    MemoryExtraction,
    Summarisation,
    PersonaEvolution,
    ListModels,
    TestConnection,
}

/// <summary>Kind of a long-term memory (§5.8).</summary>
public enum MemoryKind
{
    Fact,
    Preference,
    Event,
    Relationship,
}

/// <summary>Who wrote a memory row (§5.8).</summary>
public enum MemorySource
{
    Extraction,
    UserEdited,
    BotRemember,
}

/// <summary>Bot access role held through BotAccess (§2.5).</summary>
public enum AccessRole
{
    /// <summary>The creator — everything a user can, plus bot administration.</summary>
    Admin,
    /// <summary>Granted via invite code approval — can talk and manage own memories.</summary>
    User,
}

/// <summary>Status of an invite-code access request (§2.5).</summary>
public enum AccessRequestStatus
{
    Pending,
    Approved,
    Rejected,
}
