namespace AChat.Core.Models;

/// <summary>
/// Per-user configuration object describing how to reach a model (§3).
/// The API key is never stored in plaintext — it is encrypted at rest via
/// <see cref="IApiKeyProtector"/> (§7.3) and only ever returned to the browser
/// as a masked tail.
/// </summary>
public class LlmPreset
{
    public int Id { get; set; }

    /// <summary>Owner. Presets are private to their owner (§2.2).</summary>
    public int OwnerId { get; set; }

    public string Name { get; set; } = "";

    public LlmProvider Provider { get; set; } = LlmProvider.OpenAICompatible;

    /// <summary>
    /// Base endpoint. Blank permitted for providers with a well-known default
    /// (e.g. OpenAI → https://api.openai.com/v1).
    /// </summary>
    public string BaseEndpoint { get; set; } = "";

    /// <summary>
    /// Encrypted API key (Data Protection, §7.3). Null = no key.
    /// Never deserialized to the browser in plaintext.
    /// </summary>
    public byte[]? ApiKeyEncrypted { get; set; }

    /// <summary>The chat/completion model — the bot's actual voice.</summary>
    public string GenerationModel { get; set; } = "";

    /// <summary>
    /// A second generation model for background chores (extraction, summarisation,
    /// persona evolution). Defaults to <see cref="GenerationModel"/> when blank (§3).
    /// </summary>
    public string? UtilityModel { get; set; }

    /// <summary>
    /// Text → vector model for memory search (§5.4). <b>Locked while a bot uses this
    /// preset</b> (§5.7) — embeddings from different models are not comparable.
    /// </summary>
    public string? EmbeddingModel { get; set; }

    /// <summary>Sampling temperature. Null = provider default.</summary>
    public float? Temperature { get; set; }

    /// <summary>Maximum output tokens. Null = provider default.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Request timeout in ms. Null = factory default.</summary>
    public int? TimeoutMs { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Owner { get; set; } = null!;
}
