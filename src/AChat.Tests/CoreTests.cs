using AChat.Api.Auth;
using AChat.Api.Services;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Llm;
using Xunit;

namespace AChat.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_and_verify_roundtrip()
    {
        var hasher = new PasswordHasher();
        var hash = hasher.Hash("correct horse battery staple");
        Assert.True(hasher.Verify("correct horse battery staple", hash));
        Assert.False(hasher.Verify("wrong", hash));
        Assert.False(hasher.Verify("wrong", ""));
    }

    [Fact]
    public void Hashes_are_unique_per_salt()
    {
        var hasher = new PasswordHasher();
        Assert.NotEqual(hasher.Hash("same"), hasher.Hash("same"));
    }
}

public class PromptAssemblyTests
{
    private static Bot TestBot(string name = "Mira", string core = "A dry, warm friend.", string? style = "Short sentences.")
        => new()
        {
            Id = 1,
            OwnerId = 1,
            Name = name,
            CorePersona = core,
            SpeechStyle = style,
            PresetId = 1,
        };

    [Fact]
    public void Persona_block_contains_core_and_style()
    {
        var block = PromptAssembly.BuildPersonaBlock(TestBot());
        Assert.Contains("You are Mira", block);
        Assert.Contains("A dry, warm friend.", block);
        Assert.Contains("Short sentences.", block);
    }

    [Fact]
    public void Persona_block_without_style()
    {
        var block = PromptAssembly.BuildPersonaBlock(TestBot(style: null));
        Assert.DoesNotContain("How you speak", block);
    }
}

public class TemporalAssemblyTests
{
    private static readonly DateTime UtcNow = new(2026, 8, 21, 20, 30, 0, DateTimeKind.Utc); // Friday

    [Fact]
    public void Current_time_renders_in_the_users_timezone()
    {
        var line = PromptAssembly.FormatCurrentTime(UtcNow, "Europe/Moscow");
        Assert.Contains("Europe/Moscow", line);
        Assert.Contains("2026", line);
        Assert.Contains("11:30", line); // 20:30 UTC = 23:30 MSK
    }

    [Fact]
    public void Current_time_falls_back_to_utc_for_unknown_zone()
    {
        var line = PromptAssembly.FormatCurrentTime(UtcNow, "Not/AZone");
        Assert.Contains("UTC", line);
        Assert.Contains("20:30", line);
    }

    [Theory]
    [InlineData(2, "an hour or two")]                 // 2 hours
    [InlineData(1, "an hour or two")]                 // 1 hour
    [InlineData(5, "5 hours")]                        // 5 hours
    [InlineData(24, "a day")]                         // 1 day
    [InlineData(72, "3 days")]                        // 3 days
    [InlineData(30 * 24, "a month")]                  // 30 days
    [InlineData(95 * 24, "3 months")]                 // ~95 days
    public void Gap_markers_use_human_scale(int hours, string fragment)
    {
        Assert.Contains(fragment, PromptAssembly.FormatGapMarker(TimeSpan.FromHours(hours)));
    }

    [Fact]
    public void History_gets_a_marker_only_for_long_gaps()
    {
        var t0 = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
        var msgs = new List<Message>
        {
            new() { Role = MessageRole.User, Content = "hi", CreatedAt = t0 },
            new() { Role = MessageRole.Assistant, Content = "hey", CreatedAt = t0.AddHours(2) },
            new() { Role = MessageRole.User, Content = "back", CreatedAt = t0.AddHours(2 + 3 * 24) },
        };

        var history = PromptAssembly.BuildHistoryMessages(msgs, TimeSpan.FromHours(6));
        Assert.Equal(3, history.Count); // markers are prefixes, not extra messages
        Assert.Contains("3 days later", history[2].Content);   // the 3-day gap is marked
        Assert.StartsWith("—", history[2].Content);
        Assert.Contains("back", history[2].Content);            // …and the message itself survives
        Assert.Equal("hey", history[1].Content);                // the 2-hour gap got no marker
    }

    [Fact]
    public void Return_gap_is_marked_only_above_threshold()
    {
        var last = UtcNow.AddDays(-3);
        Assert.NotNull(PromptAssembly.FormatReturnGap(last, UtcNow, TimeSpan.FromHours(6)));
        Assert.Null(PromptAssembly.FormatReturnGap(UtcNow.AddHours(-1), UtcNow, TimeSpan.FromHours(6)));
    }

    [Fact]
    public void System_block_carries_time_and_anchored_summary()
    {
        var bot = new Bot { Name = "Mira", CorePersona = "dry and warm", SpeechStyle = "short sentences" };
        var summary = new ConversationSummary
        {
            Content = "They discussed her thesis for weeks.",
            UpdatedAt = UtcNow.AddDays(-1),
        };

        var block = PromptAssembly.BuildSystemBlock(bot, "Europe/Moscow", UtcNow, summary);
        Assert.Contains("You are Mira", block);
        Assert.Contains("Europe/Moscow", block);
        Assert.Contains("as of", block);
        Assert.Contains("They discussed her thesis for weeks.", block);
        // Ordering: persona before time, time before summary.
        Assert.True(block.IndexOf("You are Mira") < block.IndexOf("It is now"));
        Assert.True(block.IndexOf("It is now") < block.IndexOf("as of"));
    }

    [Fact]
    public void Summary_block_is_capped_by_budget_and_keeps_its_recent_end()
    {
        var summary = new ConversationSummary
        {
            Content = "OLD-" + new string('x', 4000) + " RECENT-ANCHOR",
            UpdatedAt = UtcNow,
        };
        var budget = new PromptBudget(1000, 10, 10, 50); // summary cap: 100 tokens = 400 chars
        var block = PromptAssembly.BuildSystemBlock(new Bot { Name = "M" }, null, UtcNow, summary, budget);
        Assert.Contains("RECENT-ANCHOR", block);   // the recent end survives
        Assert.DoesNotContain("OLD-", block);       // the ancient head is trimmed away
        Assert.Contains("…", block);
    }

    [Fact]
    public void Token_estimate_is_chars_over_four()
    {
        Assert.Equal(200, PromptAssembly.EstimateTokens(new string('a', 800)));
    }

    [Fact]
    public void Truncation_respects_limit_and_lands_on_word_boundaries()
    {
        var text = string.Join(" ", Enumerable.Range(1, 200).Select(i => $"tok{i}")); // ~1100 chars

        var keepStart = PromptAssembly.TruncateToTokens(text, 10, keepEnd: false);
        Assert.True(keepStart.Length <= 10 * 4 + 1);
        Assert.StartsWith("tok1", keepStart);
        Assert.EndsWith("…", keepStart);
        Assert.True(keepStart.TrimEnd('…').Split(' ').All(w => w.StartsWith("tok")));

        var keepEnd = PromptAssembly.TruncateToTokens(text, 10, keepEnd: true);
        Assert.True(keepEnd.Length <= 10 * 4 + 1);
        Assert.EndsWith("tok200", keepEnd); // the newest words survive
        Assert.StartsWith("…", keepEnd);
        Assert.True(keepEnd[1..].Split(' ').All(w => w.StartsWith("tok")));
    }
}

public class OpenAICompatibleProviderTests
{
    [Fact]
    public void Strategy_reports_its_provider_family()
    {
        var s = new OpenAICompatibleProviderStrategy();
        Assert.Equal(LlmProvider.OpenAICompatible, s.Provider);
    }

    [Fact]
    public void Factory_creates_provider_for_openai_compatible()
    {
        var factory = new LlmProviderFactory();
        var provider = factory.Create(new LlmPresetConfig
        {
            Provider = LlmProvider.OpenAICompatible,
            Endpoint = "http://localhost:9999/v1",
            GenerationModel = "test-model",
        });
        Assert.NotNull(provider);
        Assert.Equal(LlmProvider.OpenAICompatible, provider.Provider);
    }

    [Fact]
    public void Factory_rejects_unsupported_family_with_clear_message()
    {
        var factory = new LlmProviderFactory();
        Assert.False(factory.IsSupported(LlmProvider.Ollama));
        var ex = Assert.Throws<NotSupportedException>(() => factory.Create(new LlmPresetConfig
        {
            Provider = LlmProvider.Ollama,
            Endpoint = "http://localhost",
            GenerationModel = "x",
        }));
        Assert.Contains("Phase 7", ex.Message);
    }
}
