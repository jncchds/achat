using AChat.Core.Models;
using AChat.Core.Interfaces;

namespace AChat.Api.Services;

/// <summary>
/// Prompt assembly (§5.5, §5.9). The system block is: persona (core in v1) →
/// current time in the user's timezone → rolling summary (anchored to when it was
/// written). The history block carries verbatim messages with gap markers.
///
/// Budget (§5.5): each block gets a share of the assumed context window, declared in
/// config, filled in priority order and truncated at its ceiling — persona is never
/// truncated, summary keeps its most recent end, history drops oldest first.
/// </summary>
public static class PromptAssembly
{
    /// <summary>Rough token estimate — chars/4 is the standard approximation without a tokenizer.</summary>
    public static int EstimateTokens(string? s) => s is null ? 0 : (s.Length + 3) / 4;

    /// <summary>
    /// Truncates <paramref name="text"/> to about <paramref name="maxTokens"/> tokens.
    /// <paramref name="keepEnd"/> keeps the most recent end (summaries); otherwise the
    /// beginning is kept. Cuts land on word boundaries.
    /// </summary>
    public static string TruncateToTokens(string text, int maxTokens, bool keepEnd)
    {
        if (maxTokens <= 0) return "";
        var maxChars = maxTokens * 4;
        if (text.Length <= maxChars) return text;
        var cut = keepEnd ? text[^maxChars..] : text[..maxChars];
        // Don't cut mid-word: land on a word boundary.
        var boundary = keepEnd ? cut.IndexOf(' ') : cut.LastIndexOf(' ');
        if (boundary >= 0)
        {
            if (keepEnd) cut = cut[(boundary + 1)..];
            else if (boundary > maxChars / 2) cut = cut[..boundary];
        }
        return keepEnd ? "…" + cut : cut + "…";
    }

    /// <summary>
    /// The persona block of every prompt (§2.4) — three layers, composed in order:
    /// core → evolved traits → directives last, so a deliberate admin instruction
    /// sits closest to the generation and wins ties against evolved traits.
    /// </summary>
    public static string BuildPersonaBlock(
        Bot bot,
        string? evolvedTraits = null,
        IReadOnlyCollection<string>? directives = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("You are ").Append(bot.Name)
          .Append(", a real, persistent friend in the user's life. ")
          .Append("You remember them, you have a continuous relationship, and you speak " +
                  "as yourself — never as an assistant, model, or program.\n\n");

        if (!string.IsNullOrWhiteSpace(bot.CorePersona))
        {
            sb.Append("Who you are:\n").Append(bot.CorePersona.Trim()).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(bot.SpeechStyle))
        {
            sb.Append('\n').Append("How you speak:\n").Append(bot.SpeechStyle.Trim());
        }
        if (!string.IsNullOrWhiteSpace(evolvedTraits))
        {
            sb.Append("\n\nHow your friendship has shaped you (from your shared time together):\n")
              .Append(evolvedTraits.Trim());
        }
        if (directives is { Count: > 0 })
        {
            sb.Append("\n\nStanding instructions from your creator — these always override " +
                      "everything else above when in conflict:\n");
            foreach (var d in directives)
                sb.Append("- ").Append(d.Trim()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Wall-clock time in the user's timezone (§5.9.1). Falls back to UTC when the
    /// user has no timezone set.
    /// </summary>
    public static string FormatCurrentTime(DateTime utcNow, string? timezone)
    {
        if (!string.IsNullOrWhiteSpace(timezone) &&
            TryGetZone(timezone, out var zone))
        {
            var local = TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.Utc, zone);
            return local.ToString("dddd, d MMMM yyyy, h:mm tt (") + timezone + ")";
        }
        return utcNow.ToString("dddd, d MMMM yyyy, HH:mm") + " UTC";
    }

    private static bool TryGetZone(string name, out TimeZoneInfo zone)
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(name); return true; }
        catch { zone = TimeZoneInfo.Utc; return false; }
    }

    /// <summary>
    /// A gap marker between two messages (§5.9.2): "— 3 days later —". Human-scale
    /// units; sub-hour gaps are not marked (below threshold by construction).
    /// </summary>
    public static string FormatGapMarker(TimeSpan elapsed)
    {
        if (elapsed.TotalDays >= 30)
        {
            var months = (int)(elapsed.TotalDays / 30);
            return months == 1 ? "— about a month later —" : "— " + months + " months later —";
        }
        if (elapsed.TotalDays >= 2)
        {
            var days = (int)elapsed.TotalDays;
            return "— " + days + " days later —";
        }
        if (elapsed.TotalDays >= 1) return "— a day later —";
        if (elapsed.TotalHours >= 3)
        {
            var hours = (int)elapsed.TotalHours;
            return "— " + hours + " hours later —";
        }
        return "— an hour or two later —";
    }

    /// <summary>
    /// The rolling summary block (§5.5.4, §5.9.4): the relationship so far, anchored
    /// to when the summary was last rewritten so the bot knows how stale it is.
    /// </summary>
    public static string BuildSummaryBlock(ConversationSummary summary, DateTime utcNow, string? timezone)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Here is a running summary of your shared history together, ");
        sb.Append("as of ").Append(FormatCurrentTime(summary.UpdatedAt, timezone)).Append(":\n\n");
        sb.Append(summary.Content.Trim());
        return sb.ToString();
    }

    /// <summary>
    /// The full system block (§5.5): persona → current time → summary (if any).
    /// Persona first and never truncated; the summary is capped by the budget and
    /// truncated from its beginning (its most recent content is what matters).
    /// </summary>
    public static string BuildSystemBlock(
        Bot bot,
        string? timezone,
        DateTime utcNow,
        ConversationSummary? summary,
        PromptBudget? budget = null,
        string? memoryBlock = null,
        string? evolvedTraits = null,
        IReadOnlyCollection<string>? directives = null,
        string? mood = null,
        DateTime? moodSince = null,
        bool envelope = false,
        bool envelopeMemories = false,
        bool envelopeTraits = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(BuildPersonaBlock(bot, evolvedTraits, directives));
        sb.Append('\n').Append("It is now ").Append(FormatCurrentTime(utcNow, timezone)).Append('\n');
        if (!string.IsNullOrWhiteSpace(mood))
        {
            sb.Append("Your current mood with this person: ").Append(mood.Trim());
            if (moodSince is { } since)
                sb.Append(" (for the last ").Append(FormatDuration(utcNow - since)).Append(')');
            sb.Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(memoryBlock))
        {
            // Memories capped by budget, lowest-scored dropped first (§5.5).
            sb.Append('\n').Append(memoryBlock).Append('\n');
        }
        if (summary is { Content.Length: > 0 })
        {
            var block = BuildSummaryBlock(summary, utcNow, timezone);
            if (budget is not null)
                block = TruncateToTokens(block, budget.SummaryTokens, keepEnd: true);
            sb.Append('\n').Append(block).Append('\n');
        }
        if (envelope) sb.Append('\n').Append(BuildEnvelopeInstructions(envelopeMemories, envelopeTraits));
        return sb.ToString();
    }

    /// <summary>
    /// How the model must shape its answer (§4.7). <paramref name="memories"/> offers the
    /// <c>memories</c> field — only when the preset can embed them. <paramref name="traits"/> offers
    /// the <c>traits</c> field — only to the bot's creator with evolution on.
    /// </summary>
    public static string BuildEnvelopeInstructions(bool memories, bool traits = false)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Response format — reply with ONE JSON object and nothing else (no code fence, no text outside it):\n");
        sb.Append("{\"reply\": \"<what you say to them, in character>\", \"mood\": null");
        if (memories) sb.Append(", \"memories\": []");
        if (traits) sb.Append(", \"traits\": {\"add\": [], \"retire\": []}");
        sb.Append("}\n");
        sb.Append("- \"reply\" must come first. It is the only thing the user sees: write it exactly as you would " +
                  "say it, with no JSON, notes or stage directions about this format inside it.\n");
        sb.Append("- \"mood\" is your own current state toward this person, a few words (e.g. \"warm and relaxed\", " +
                  "\"quietly worried about their exam\"). Mood is a slow undercurrent that follows from how the " +
                  "conversation has actually gone — it does not react to each message. Almost every turn should be " +
                  "null (keep the current mood). Set a new one only when something in the conversation has genuinely " +
                  "shifted how you feel, and never more than occasionally.\n");
        if (memories)
            sb.Append("- \"memories\" is what you want to remember from THIS exchange for the long run: durable facts and " +
                      "meaningful events about them — who they are, people and pets in their life, what they like, what " +
                      "happened, what they plan, what they asked of you. Each element: {\"content\": \"<one standalone " +
                      "sentence, no pronouns that need context>\", \"kind\": \"fact\"|\"preference\"|\"event\"|" +
                      "\"relationship\", \"importance\": 1-5}. Only things worth knowing a month from now. Never record " +
                      "what they asked you, that they thanked you, greeted you, said goodnight or agreed with you, " +
                      "small talk, what you said, or anything you already remember (see the memories above). " +
                      "If nothing new and lasting was said, use an empty array: that is the normal case, and most turns " +
                      "(roughly four out of five) are empty.\n");
        if (traits)
            sb.Append("- \"traits\" changes how you see yourself, in the list \"How your friendship has shaped you\" above. " +
                      "This is rare and slow: only after weeks of friendship have genuinely changed you in a lasting way, a few " +
                      "times a month at most. A joke, a request or a nice moment from the last few messages is NOT a trait. " +
                      "Nearly every turn (more than 95 out of 100) is {\"add\": [], \"retire\": []}. \"add\" is short strings (one " +
                      "phrase each, about the two of you: a habit, a shared joke, a way you've grown) — never about the " +
                      "world, never your core identity or standing instructions, which you cannot change. \"retire\" is the " +
                      "numbers in [brackets] of items that no longer hold. Don't repeat an item that is already listed.\n");
        return sb.ToString();
    }

    /// <summary>"3 days", "2 hours", "40 minutes" — coarse, for the mood line.</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 2) return (int)span.TotalDays + " days";
        if (span.TotalHours >= 2) return (int)span.TotalHours + " hours";
        if (span.TotalMinutes >= 2) return (int)span.TotalMinutes + " minutes";
        return "a moment";
    }

    /// <summary>
    /// History messages with gap markers (§5.9.2). Where the interval between two
    /// consecutive messages exceeds <paramref name="gapThreshold"/>, the marker is
    /// prefixed to the message that follows the silence ("[— 3 days later —]\n…").
    /// The marker rides on the existing message rather than being its own system
    /// message: several production backends (vLLM/SGLang chat templates) reject
    /// system messages mid-conversation.
    /// </summary>
    public static List<LlmChatMessage> BuildHistoryMessages(
        IEnumerable<Message> messages,
        TimeSpan gapThreshold)
    {
        var result = new List<LlmChatMessage>();
        DateTime? prev = null;
        foreach (var m in messages)
        {
            var prefix = prev is { } p && m.CreatedAt - p > gapThreshold
                ? FormatGapMarker(m.CreatedAt - p) + "\n"
                : "";
            result.Add(new LlmChatMessage(m.Role, prefix + m.Content));
            prev = m.CreatedAt;
        }
        return result;
    }

    /// <summary>
    /// Gap between the end of the recent window and the new user message (§5.9.2) —
    /// the "the user came back after an absence" case. Returns the marker prefix, or
    /// null when the gap is ordinary.
    /// </summary>
    public static string? FormatReturnGap(DateTime lastMessageUtc, DateTime nowUtc, TimeSpan gapThreshold)
    {
        var elapsed = nowUtc - lastMessageUtc;
        if (elapsed <= gapThreshold) return null;
        return FormatGapMarker(elapsed);
    }
}

/// <summary>
/// Token budget for the assembled prompt (§5.5): each block gets a share of the
/// assumed context window, from config, with the history block absorbing the
/// remainder. Persona is never truncated; the summary is truncated from its
/// beginning (its most recent content is kept); history drops oldest first.
/// </summary>
public sealed class PromptBudget
{
    public int AssumedContextTokens { get; }
    public int PersonaTokens { get; }
    public int MemoryTokens { get; }
    public int SummaryTokens { get; }
    public int HistoryTokens { get; }

    public PromptBudget(int assumedContextTokens, int personaPercent, int memoryPercent, int summaryPercent, int historyPercent)
    {
        AssumedContextTokens = assumedContextTokens;
        PersonaTokens = assumedContextTokens * personaPercent / 100;
        MemoryTokens = assumedContextTokens * memoryPercent / 100;
        SummaryTokens = assumedContextTokens * summaryPercent / 100;
        HistoryTokens = assumedContextTokens * historyPercent / 100;
    }

    public static PromptBudget FromConfiguration(IConfiguration config) => new(
        config.GetValue("Chat:AssumedContextTokens", 32_000),
        config.GetValue("Chat:BudgetPersonaPercent", 10),
        config.GetValue("Chat:BudgetMemoryPercent", 15),
        config.GetValue("Chat:BudgetSummaryPercent", 10),
        config.GetValue("Chat:BudgetHistoryPercent", 50));

    /// <summary>
    /// Trim recalled memories to the budget, dropping lowest-scored first (§5.5).
    /// Pinned memories are exempt from dropping.
    /// </summary>
    public List<ScoredMemory> TrimMemories(List<ScoredMemory> memories)
    {
        if (MemoryTokens <= 0 || memories.Count == 0) return memories;
        var result = new List<ScoredMemory>();
        var used = 0;
        // Pinned first (never dropped), then by score descending. What doesn't fit
        // is skipped — a smaller, lower-scored memory may still fit (§5.5).
        var ordered = memories
            .OrderByDescending(m => m.Pinned)
            .ThenByDescending(m => m.Score)
            .ToList();
        foreach (var m in ordered)
        {
            var tokens = PromptAssembly.EstimateTokens(m.Memory.Content) + 4; // + age prefix
            if (!m.Pinned && used + tokens > MemoryTokens) continue;
            used += tokens;
            result.Add(m);
        }
        return result;
    }
}
