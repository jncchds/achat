using Pgvector;

namespace AChat.Core.Models;

/// <summary>
/// An embedded fact/episode owned by a bot, scoped to a (BotId, UserId) relationship
/// (§5.1, §5.8).
/// </summary>
public class Memory
{
    public int Id { get; set; }

    /// <summary>Scope — composite (BotId, UserId) index narrows the vector scan (§6).</summary>
    public int BotId { get; set; }
    public int UserId { get; set; }

    public MemoryKind Kind { get; set; } = MemoryKind.Fact;

    public string Content { get; set; } = "";

    /// <summary>
    /// The embedding vector. Variable-length column so presets with different
    /// embedding models/dimensions can coexist (§6). Uses pgvector's <see cref="Vector"/>
    /// type — the same approach as abook — so EF maps it natively.
    /// </summary>
    public Vector? Embedding { get; set; }

    /// <summary>
    /// Which embedding model produced <see cref="Embedding"/>. Kept on every row from
    /// the first migration so a mismatch is detectable instead of silent (§5.7).
    /// </summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>Dimension of <see cref="Embedding"/> — what any re-embedding path needs (§5.7).</summary>
    public int EmbeddingDimension { get; set; }

    /// <summary>1..5, set by extraction (§5.4).</summary>
    public int Importance { get; set; } = 3;

    /// <summary>Pinned memories always surface in the prompt, regardless of score (§5.4).</summary>
    public bool IsPinned { get; set; }

    public MemorySource Source { get; set; } = MemorySource.Extraction;

    /// <summary>Provenance — the message that produced this row, for the viewer (§5.8).</summary>
    public int? SourceMessageId { get; set; }

    /// <summary>
    /// Set when extraction supersedes this row; the old one is kept inspectable rather
    /// than deleted (§5.6).
    /// </summary>
    public int? SupersededByMemoryId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the memory was last retrieved into a prompt (§5.8).</summary>
    public DateTime? LastRecalledAt { get; set; }

    public int RecallCount { get; set; }

    /// <summary>Soft-delete flag; the viewer's "forget" marks rather than hard-deletes (§5.6).</summary>
    public bool IsDeleted { get; set; }
}
