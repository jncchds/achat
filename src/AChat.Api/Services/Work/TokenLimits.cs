using System.Text.RegularExpressions;
using AChat.Core;

namespace AChat.Api.Services;

/// <summary>
/// Pure helpers for the token-limit failures of background passes (consolidation,
/// §2.4): recognising a context-length rejection, stripping inline reasoning, and
/// splitting an over-long list into chunks that fit an input budget. A context-length
/// error or a <c>finish_reason=length</c> truncation is deterministic — the same request
/// fails again — so callers shrink the input once and otherwise fail permanently (§12b).
/// </summary>
public static class TokenLimits
{
    private static readonly Regex ContextError = new(
        @"context[ _-]?(length|window)|maximum context|too many tokens|token limit|exceeds? the (maximum|limit|model)|" +
        @"prompt is too long|input (is )?too long|reduce the length|n_ctx|context_length_exceeded|greater than the context",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the provider rejected the request because the prompt does not fit the model's context window.</summary>
    public static bool IsContextLengthError(LlmProviderException ex)
    {
        if (ex.HttpStatus is not (400 or 413 or 422)) return false;
        return ContextError.IsMatch(ex.ErrorPayload ?? "") || ContextError.IsMatch(ex.Message);
    }

    /// <summary>
    /// Removes inline <c>&lt;think&gt;…&lt;/think&gt;</c> blocks (some servers return the reasoning
    /// in the content). An unclosed block — the model was cut off while thinking — drops the rest.
    /// </summary>
    public static string StripThinking(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var text = s;
        while (true)
        {
            var open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (open < 0) break;
            var close = text.IndexOf("</think>", open, StringComparison.OrdinalIgnoreCase);
            text = close < 0 ? text[..open] : text.Remove(open, close + "</think>".Length - open);
        }
        // A lone closing tag: some templates open the block in the prompt, so only "</think>" shows up.
        var lone = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (lone >= 0) text = text[(lone + "</think>".Length)..];
        return text.Trim();
    }

    /// <summary>
    /// Splits <paramref name="items"/> (kept in order — callers pass oldest first) into chunks whose
    /// estimated tokens (item text + <paramref name="perItemOverhead"/>) each fit
    /// <paramref name="budgetTokens"/>. An item larger than the budget goes in a chunk of its own
    /// (the caller treats that as unfittable). A non-positive budget yields no chunks.
    /// </summary>
    public static List<List<T>> Chunk<T>(IReadOnlyList<T> items, Func<T, int> tokens, int budgetTokens, int perItemOverhead = 2)
    {
        var chunks = new List<List<T>>();
        if (budgetTokens <= 0) return chunks;
        var cur = new List<T>();
        var used = 0;
        foreach (var item in items)
        {
            var t = tokens(item) + perItemOverhead;
            if (cur.Count > 0 && used + t > budgetTokens)
            {
                chunks.Add(cur);
                cur = [];
                used = 0;
            }
            cur.Add(item);
            used += t;
        }
        if (cur.Count > 0) chunks.Add(cur);
        return chunks;
    }
}
