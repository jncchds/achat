using AChat.Api.Services;
using Xunit;

namespace AChat.Tests;

public class TurnEnvelopeTests
{
    private static string Stream(string raw, int chunk)
    {
        var s = new ReplyStreamer();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < raw.Length; i += chunk)
            sb.Append(s.Feed(raw.Substring(i, Math.Min(chunk, raw.Length - i))));
        return sb.ToString();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(1000)]
    public void Streams_only_the_reply_unescaped(int chunk)
    {
        var raw = "{\"reply\": \"Hi \\\"you\\\"\\nline2 \\u00e9\", \"mood\": \"calm\"}";
        Assert.Equal("Hi \"you\"\nline2 é", Stream(raw, chunk));
    }

    [Fact]
    public void Fenced_json_and_plain_text_are_handled()
    {
        Assert.Equal("yo", Stream("```json\n{\"reply\":\"yo\"}\n```", 2));
        Assert.Equal("just text", Stream("just text", 4));
    }

    [Fact]
    public void Reply_key_inside_nested_value_is_ignored()
    {
        Assert.Equal("a", Stream("{\"x\":{\"reply\":\"no\"},\"reply\":\"a\"}", 1));
    }

    [Fact]
    public void Parse_reads_reply_and_mood()
    {
        var e = TurnEnvelope.Parse("{\"reply\":\"hello\",\"mood\":\" warm \",}");
        Assert.True(e.Valid);
        Assert.Equal("hello", e.Reply);
        Assert.True(e.MoodSet);
        Assert.Equal("warm", e.Mood);
    }

    [Fact]
    public void Null_mood_means_unchanged_and_empty_clears()
    {
        Assert.False(TurnEnvelope.Parse("{\"reply\":\"a\",\"mood\":null}").MoodSet);
        var cleared = TurnEnvelope.Parse("{\"reply\":\"a\",\"mood\":\"\"}");
        Assert.True(cleared.MoodSet);
        Assert.Equal("", cleared.Mood);
    }

    [Fact]
    public void Parse_reads_memories_leniently()
    {
        var e = TurnEnvelope.Parse("{\"reply\":\"ok\",\"memories\":["
            + "{\"content\":\" Her cat is named Pixel \",\"kind\":\"Fact\",\"importance\":99},"
            + "{\"content\":\"\"},{\"kind\":\"event\"},42,"
            + "{\"content\":\"Exam on Friday\",\"kind\":\"nonsense\"}]}");
        Assert.True(e.Valid);
        Assert.NotNull(e.Memories);
        Assert.Equal(2, e.Memories!.Count);
        Assert.Equal("Her cat is named Pixel", e.Memories[0].Content);
        Assert.Equal(5, e.Memories[0].Importance); // clamped to 1..5
        Assert.Equal(AChat.Core.Models.MemoryKind.Fact, e.Memories[1].Kind); // unknown kind → Fact
        Assert.Null(TurnEnvelope.Parse("{\"reply\":\"ok\",\"memories\":[]}").Memories);
    }

    [Fact]
    public void Memories_field_is_offered_only_with_an_embedding_model()
    {
        Assert.Contains("\"memories\"", PromptAssembly.BuildEnvelopeInstructions(true));
        Assert.DoesNotContain("\"memories\"", PromptAssembly.BuildEnvelopeInstructions(false));
    }

    [Fact]
    public void Instructions_tell_the_model_what_not_to_remember_or_turn_into_a_trait()
    {
        // Measured on a real model (nemotron-nano free): without these it recorded nearly every turn
        // ("User thanked me") and proposed a trait after a handful of messages.
        var text = PromptAssembly.BuildEnvelopeInstructions(true, true);
        Assert.Contains("Never record", text);
        Assert.Contains("empty array", text);
        Assert.Contains("is NOT a trait", text);
    }

    [Fact]
    public void Garbled_or_plain_output_is_not_lost_and_has_no_side_effects()
    {
        var plain = TurnEnvelope.Parse("Just a normal answer.");
        Assert.False(plain.Valid);
        Assert.Equal("Just a normal answer.", plain.Reply);

        var truncated = TurnEnvelope.Parse("{\"reply\":\"half a thou", "half a thou");
        Assert.False(truncated.Valid);
        Assert.Equal("half a thou", truncated.Reply);
        Assert.False(truncated.MoodSet);
    }

    [Fact]
    public void Parse_reads_traits_leniently()
    {
        var e = TurnEnvelope.Parse("{\"reply\":\"ok\",\"traits\":{\"add\":[\" teases them about cooking \",\"\",\"Teases them about cooking\",7,null],"
            + "\"retire\":[12,\"x\",3.5,12,null,4]}}");
        Assert.True(e.Valid);
        Assert.NotNull(e.Traits);
        Assert.Equal(new[] { "teases them about cooking" }, e.Traits!.Add);
        Assert.Equal(new[] { 12, 4 }, e.Traits.Retire);
    }

    [Theory]
    [InlineData("{\"reply\":\"a\",\"traits\":{\"add\":[],\"retire\":[]}}")]
    [InlineData("{\"reply\":\"a\",\"traits\":[\"x\"]}")]
    [InlineData("{\"reply\":\"a\",\"traits\":{\"add\":\"x\",\"retire\":\"1\"}}")]
    [InlineData("{\"reply\":\"a\"}")]
    public void Empty_or_malformed_traits_are_null(string raw)
    {
        Assert.Null(TurnEnvelope.Parse(raw).Traits);
    }

    [Fact]
    public void Traits_field_is_offered_only_when_asked_for()
    {
        Assert.Contains("\"traits\"", PromptAssembly.BuildEnvelopeInstructions(true, true));
        Assert.DoesNotContain("\"traits\"", PromptAssembly.BuildEnvelopeInstructions(true));
        Assert.DoesNotContain("\"traits\"", PromptAssembly.BuildEnvelopeInstructions(false, false));
    }

    [Fact]
    public void System_block_offers_traits_with_ids_only_to_the_creator_with_evolution_on()
    {
        var bot = new AChat.Core.Models.Bot { Id = 1, OwnerId = 5, Name = "M", EvolutionEnabled = true };
        var items = new[] { new TraitItem(12, "teases them about cooking") };
        string Block(int userId)
        {
            var offer = PersonaTraits.CanChange(bot, userId);
            return PromptAssembly.BuildSystemBlock(bot, null, DateTime.UtcNow, null,
                evolvedTraits: PersonaTraits.RenderForPrompt(items, offer), envelope: true, envelopeTraits: offer);
        }
        var creator = Block(5);
        Assert.Contains("[12] teases them about cooking", creator);
        Assert.Contains("\"traits\"", creator);

        var other = Block(6);
        Assert.Contains("- teases them about cooking", other);
        Assert.DoesNotContain("[12]", other);
        Assert.DoesNotContain("\"traits\"", other);

        bot.EvolutionEnabled = false;
        Assert.DoesNotContain("\"traits\"", Block(5));
    }
}
