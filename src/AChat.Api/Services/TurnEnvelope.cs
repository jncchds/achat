using System.Text;
using System.Text.Json;

namespace AChat.Api.Services;

/// <summary>
/// The structured turn envelope (§4.7): every chat turn returns one JSON object,
/// <c>{"reply": "...", "mood": ...}</c> (more fields arrive in later steps).
/// <c>reply</c> is the only field the user sees and always comes first so it can
/// be streamed.
/// </summary>
/// <param name="Reply">What the user sees. Never null — falls back to the raw text.</param>
/// <param name="MoodSet">The envelope carried a string <c>mood</c> field
/// (an empty string means "clear the mood"; null / absent means unchanged).</param>
/// <param name="Mood">New mood text when <paramref name="MoodSet"/>.</param>
/// <param name="Valid">The whole object parsed. When false no side effects are applied.</param>
public sealed record TurnEnvelope(string Reply, bool MoodSet, string? Mood, bool Valid)
{
    public const int MaxMoodLength = 120;

    /// <summary>
    /// Tolerant parse of the model's final text (§4.7): strips a code fence, accepts
    /// trailing commas. On any failure the turn is not lost — the reply recovered
    /// by the streaming reader (or the raw text) is shown and no side effects apply.
    /// </summary>
    public static TurnEnvelope Parse(string raw, string? streamedReply = null)
    {
        var json = StripFence(raw);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("reply", out var r)
                && r.ValueKind == JsonValueKind.String)
            {
                var moodSet = false;
                string? mood = null;
                if (doc.RootElement.TryGetProperty("mood", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    moodSet = true;
                    mood = m.GetString()!.Trim();
                    if (mood.Length > MaxMoodLength) mood = mood[..MaxMoodLength].TrimEnd();
                }
                return new TurnEnvelope(r.GetString()!.Trim(), moodSet, mood, true);
            }
        }
        catch (JsonException) { /* fall through to recovery */ }

        var recovered = !string.IsNullOrWhiteSpace(streamedReply) ? streamedReply!.Trim() : raw.Trim();
        return new TurnEnvelope(recovered, false, null, false);
    }

    private static string StripFence(string raw)
    {
        var s = raw.Trim();
        if (!s.StartsWith("```")) return s;
        var nl = s.IndexOf('\n');
        if (nl < 0) return s;
        s = s[(nl + 1)..];
        var end = s.LastIndexOf("```", StringComparison.Ordinal);
        return (end >= 0 ? s[..end] : s).Trim();
    }
}

/// <summary>
/// Incremental reader for the streamed envelope (§4.7): feed it the raw chunks and it
/// returns the newly available characters of the top-level <c>reply</c> string,
/// unescaped. If the model ignored the format and answers in plain text (first
/// non-blank character is not <c>{</c>) the chunks pass through unchanged.
/// </summary>
public sealed class ReplyStreamer
{
    private enum Mode { Undecided, Plain, Json }

    private Mode _mode = Mode.Undecided;
    private bool _inFence;          // skipping a leading ``` line
    private int _depth;
    private bool _inString, _escape;
    private int _unicodeLeft;
    private int _unicodeValue;
    private bool _expectValue;      // after ':' at depth 1 until ','
    private readonly StringBuilder _key = new();
    private string? _valueKey;      // the depth-1 key whose value is being read
    private bool _streamingReply;   // current string is the reply value
    private bool _replyDone;
    private readonly StringBuilder _reply = new();

    /// <summary>The reply text recovered so far.</summary>
    public string Reply => _reply.ToString();

    /// <summary>True once the response was recognised as a JSON object.</summary>
    public bool IsJson => _mode == Mode.Json;

    public string Feed(string chunk)
    {
        var sb = new StringBuilder();
        foreach (var ch in chunk) Step(ch, sb);
        return sb.ToString();
    }

    private void Emit(char c, StringBuilder sb) { _reply.Append(c); sb.Append(c); }

    private void Step(char c, StringBuilder sb)
    {
        switch (_mode)
        {
            case Mode.Undecided:
                if (_inFence) { if (c == '\n') _inFence = false; return; }
                if (char.IsWhiteSpace(c)) return;
                if (c == '`') { _inFence = true; return; }
                if (c == '{') { _mode = Mode.Json; _depth = 1; return; }
                _mode = Mode.Plain;
                Emit(c, sb);
                return;
            case Mode.Plain:
                Emit(c, sb);
                return;
            default:
                StepJson(c, sb);
                return;
        }
    }

    private void StepJson(char c, StringBuilder sb)
    {
        if (_inString)
        {
            if (_unicodeLeft > 0)
            {
                var d = Uri.IsHexDigit(c) ? Convert.ToInt32(c.ToString(), 16) : 0;
                _unicodeValue = _unicodeValue * 16 + d;
                if (--_unicodeLeft == 0) StringChar((char)_unicodeValue, sb);
                return;
            }
            if (_escape)
            {
                _escape = false;
                switch (c)
                {
                    case 'n': StringChar('\n', sb); break;
                    case 't': StringChar('\t', sb); break;
                    case 'r': StringChar('\r', sb); break;
                    case 'b': StringChar('\b', sb); break;
                    case 'f': StringChar('\f', sb); break;
                    case 'u': _unicodeLeft = 4; _unicodeValue = 0; break;
                    default: StringChar(c, sb); break; // \" \\ \/
                }
                return;
            }
            if (c == '\\') { _escape = true; return; }
            if (c == '"')
            {
                _inString = false;
                if (_streamingReply) { _streamingReply = false; _replyDone = true; }
                else if (_depth == 1 && !_expectValue) _valueKey = _key.ToString();
                return;
            }
            StringChar(c, sb);
            return;
        }

        switch (c)
        {
            case '"':
                _inString = true;
                if (_depth == 1 && _expectValue && _valueKey == "reply" && !_replyDone)
                    _streamingReply = true;
                else if (_depth == 1 && !_expectValue)
                    _key.Clear();
                break;
            case '{': case '[': _depth++; break;
            case '}': case ']': _depth--; break;
            case ':': if (_depth == 1) _expectValue = true; break;
            case ',': if (_depth == 1) { _expectValue = false; _valueKey = null; } break;
        }
    }

    private void StringChar(char c, StringBuilder sb)
    {
        if (_streamingReply) Emit(c, sb);
        else if (_depth == 1 && !_expectValue) _key.Append(c);
    }
}
