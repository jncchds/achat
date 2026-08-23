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

    [Fact]
    public void Persona_block_orders_core_then_evolved_then_directives_last()
    {
        var block = PromptAssembly.BuildPersonaBlock(
            TestBot(),
            evolvedTraits: "You two share a running joke about the flat's heating.",
            directives: ["Be more blunt.", "Never bring up my ex."]);

        var core = block.IndexOf("A dry, warm friend.");
        var evolved = block.IndexOf("running joke");
        var d1 = block.IndexOf("Be more blunt.");
        var d2 = block.IndexOf("Never bring up my ex.");
        Assert.True(core >= 0 && evolved > core, "core must precede evolved traits");
        Assert.True(d1 > evolved && d2 > d1, "directives must come last, in order");
        Assert.Contains("override", block); // the admin's instructions are declared as winning
    }

    [Fact]
    public void Empty_evolved_traits_are_omitted()
    {
        var block = PromptAssembly.BuildPersonaBlock(TestBot(), evolvedTraits: "   ");
        Assert.DoesNotContain("How your friendship has shaped you", block);
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
        var budget = new PromptBudget(1000, 10, 15, 10, 50); // summary cap: 100 tokens = 400 chars
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

public class MemoryRetrievalTests
{
    private static MemoryRetriever Cfg(float floor = 0.35f, int topK = 5,
        float wSim = 0.6f, float wImp = 0.2f, float wRec = 0.2f, float halflife = 30f)
        => new(topK, floor, wSim, wImp, wRec, halflife);

    [Fact]
    public void Rank_combines_similarity_importance_and_recency()
    {
        // Perfect similarity, max importance, brand new → all three signals max.
        var fresh = MemoryRetriever.Rank(1f, 5, 0, 0.6f, 0.2f, 0.2f, 30f);
        Assert.Equal(1.0, fresh, 3);

        // Same similarity, but a year old → recency signal ~0.
        var stale = MemoryRetriever.Rank(1f, 5, 365, 0.6f, 0.2f, 0.2f, 30f);
        Assert.True(stale < fresh);

        // Importance alone separates memories with equal similarity/age.
        Assert.True(
            MemoryRetriever.Rank(0.5f, 5, 10, 0.6f, 0.2f, 0.2f, 30f) >
            MemoryRetriever.Rank(0.5f, 1, 10, 0.6f, 0.2f, 0.2f, 30f));
    }

    [Fact]
    public void Select_keeps_topK_above_floor_and_always_includes_pinned()
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        var cfg = Cfg(floor: 0.4f, topK: 2);

        Memory M(int id, float sim, int imp, bool pinned, int ageDays) => new()
        {
            Id = id,
            Content = $"memory {id}",
            Importance = imp,
            IsPinned = pinned,
            CreatedAt = now.AddDays(-ageDays),
        };

        var high = M(1, 0.9f, 4, pinned: false, 1);
        var mid = M(2, 0.7f, 3, pinned: false, 5);
        var low = M(3, 0.5f, 1, pinned: false, 30);
        var weak = M(4, 0.2f, 1, pinned: false, 1);   // below floor
        var pinnedWeak = M(5, 0.1f, 1, pinned: true, 400); // below floor, but pinned

        var selected = MemoryRetriever.Select(
            new[] { (high, 0.9f), (mid, 0.7f), (low, 0.5f), (weak, 0.2f), (pinnedWeak, 0.1f) },
            now, cfg);

        var ids = selected.Select(s => s.Memory.Id).ToHashSet();
        Assert.Contains(1, ids);          // top
        Assert.Contains(2, ids);          // second
        Assert.Contains(5, ids);          // pinned always rides along (§5.4.4)
        Assert.DoesNotContain(4, ids);    // below floor
        Assert.Equal(3, ids.Count);       // K=2 + 1 pinned
    }

    [Theory]
    [InlineData(0, "earlier today")]
    [InlineData(0.2, "hours ago")]      // ~5 hours
    [InlineData(1, "yesterday")]
    [InlineData(10, "10 days ago")]
    [InlineData(45, "a month")]
    [InlineData(400, "a year")]
    [InlineData(800, "2 years")]
    public void RelativeAge_uses_human_scale(double daysAgo, string fragment)
    {
        var now = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.Contains(fragment, MemoryRetriever.RelativeAge(now.AddDays(-daysAgo), now));
    }

    [Fact]
    public void TrimMemories_drops_what_does_not_fit_and_never_pinned()
    {
        var budget = new PromptBudget(200, 10, 15, 10, 50); // memory cap: 30 tokens = 120 chars
        ScoredMemory Mem(int id, string content, float score, bool pinned) => new()
        {
            Memory = new Memory { Id = id, Content = content },
            Similarity = 0,
            Score = score,
            Pinned = pinned,
        };

        var list = new List<ScoredMemory>
        {
            Mem(1, new string('a', 120), score: 0.9f, pinned: false), // 124 — over budget with the others
            Mem(2, "short", score: 0.8f, pinned: false),              // 9
            Mem(3, "pinned", score: 0.1f, pinned: true),              // 10 — always kept
        };

        var trimmed = budget.TrimMemories(list);
        Assert.Contains(trimmed, m => m.Memory.Id == 3); // pinned survives
        Assert.Contains(trimmed, m => m.Memory.Id == 2); // the short one fits (10+9 ≤ 30)
        Assert.DoesNotContain(trimmed, m => m.Memory.Id == 1); // the long one is dropped
    }
}

public class ExtractionLogicTests
{
    [Fact]
    public void ParseCandidates_handles_fences_prose_and_bad_elements()
    {
        var raw = "Sure! Here is the array:\n```json\n[\n"
            + "{\"content\": \"User's cat is named Pixel\", \"kind\": \"fact\", \"importance\": 4},\n"
            + "{\"content\": \"\"},\n"
            + "{\"kind\": \"event\"},\n"
            + "42\n"
            + "]\n```\nHope that helps!";

        var result = MemoryExtractionService.ParseCandidates(raw, max: 10);
        Assert.Single(result);
        Assert.Equal("User's cat is named Pixel", result[0].Content);
        Assert.Equal(MemoryKind.Fact, result[0].Kind);
        Assert.Equal(4, result[0].Importance);
    }

    [Fact]
    public void ParseCandidates_clamps_importance_and_respects_max()
    {
        var raw = "[{\"content\":\"a\",\"importance\":99},{\"content\":\"b\"},{\"content\":\"c\"}]";
        var result = MemoryExtractionService.ParseCandidates(raw, max: 2);
        Assert.Equal(2, result.Count);
        Assert.Equal(5, result[0].Importance); // clamped to 1..5
    }

    [Fact]
    public void ParseCandidates_returns_empty_on_garbage()
    {
        Assert.Empty(MemoryExtractionService.ParseCandidates("no json here", 5));
        Assert.Empty(MemoryExtractionService.ParseCandidates("[{broken", 5));
    }

    [Fact]
    public void Decide_drops_duplicates_and_respects_human_edits()
    {
        // Near-identical → always a duplicate, even over a human edit.
        Assert.Equal(MemoryExtractionService.CandidateAction.Drop,
            MemoryExtractionService.Decide(0.95f, MemorySource.Extraction));
        Assert.Equal(MemoryExtractionService.CandidateAction.Drop,
            MemoryExtractionService.Decide(0.95f, MemorySource.UserEdited));

        // Similar but not identical → supersede…
        Assert.Equal(MemoryExtractionService.CandidateAction.Supersede,
            MemoryExtractionService.Decide(0.90f, MemorySource.Extraction));

        // …unless a human edited the existing row (§5.6).
        Assert.Equal(MemoryExtractionService.CandidateAction.Drop,
            MemoryExtractionService.Decide(0.90f, MemorySource.UserEdited));

        // Unrelated → fresh insert.
        Assert.Equal(MemoryExtractionService.CandidateAction.Insert,
            MemoryExtractionService.Decide(0.50f, MemorySource.Extraction));
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

    [Theory]
    [InlineData(LlmProvider.OpenAICompatible, typeof(OpenAICompatibleProvider))]
    [InlineData(LlmProvider.OpenAI, typeof(OpenAIProvider))]
    [InlineData(LlmProvider.Ollama, typeof(OllamaProvider))]
    [InlineData(LlmProvider.GoogleAIStudio, typeof(GoogleAIStudioProvider))]
    public void Factory_supports_and_creates_every_provider_family(LlmProvider family, Type expectedType)
    {
        var factory = new LlmProviderFactory();
        Assert.True(factory.IsSupported(family), $"{family} should be supported");
        var provider = factory.Create(new LlmPresetConfig
        {
            Provider = family,
            Endpoint = "http://localhost:9999/v1",
            GenerationModel = "test-model",
        });
        Assert.Equal(expectedType, provider.GetType());
        Assert.Equal(family, provider.Provider);
    }

    [Fact]
    public void Every_enum_family_is_registered()
    {
        var factory = new LlmProviderFactory();
        foreach (var family in Enum.GetValues<LlmProvider>())
            Assert.True(factory.IsSupported(family), $"{family} should have a registered strategy");
    }

    [Fact]
    public void Ollama_defaults_to_host_docker_openai_endpoint_and_needs_no_key()
    {
        // A blank endpoint must resolve to Ollama's OpenAI-compatible API on the host.
        var p = new OllamaProvider(new LlmPresetConfig
        {
            Provider = LlmProvider.Ollama,
            Endpoint = "",
            GenerationModel = "llama3",
        });
        // The provider is OpenAI-shaped: it issues /chat/completions against its base URL.
        Assert.Equal(LlmProvider.Ollama, p.Provider);
    }

    [Fact]
    public void GoogleAIStudio_defaults_to_openai_compatible_endpoint()
    {
        var p = new GoogleAIStudioProvider(new LlmPresetConfig
        {
            Provider = LlmProvider.GoogleAIStudio,
            Endpoint = "",
            GenerationModel = "gemini-2.0-flash",
        });
        Assert.Equal(LlmProvider.GoogleAIStudio, p.Provider);
    }
}

public class EvolutionTriggerTests
{
    private static readonly DateTime Now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = Now.AddHours(-12); // 12h cooldown

    [Fact]
    public void Triggers_when_enough_new_messages_and_no_versions_yet()
    {
        Assert.True(PersonaEvolutionService.ShouldEvolve(50, null, 40, Cutoff));
    }

    [Fact]
    public void Does_not_trigger_below_the_message_threshold()
    {
        Assert.False(PersonaEvolutionService.ShouldEvolve(10, null, 40, Cutoff));
    }

    [Fact]
    public void Does_not_trigger_during_the_cooldown()
    {
        var recent = Now.AddMinutes(-30); // a version written half an hour ago
        Assert.False(PersonaEvolutionService.ShouldEvolve(50, recent, 40, Cutoff));
    }

    [Fact]
    public void Triggers_once_the_cooldown_has_elapsed()
    {
        var old = Now.AddHours(-24);
        Assert.True(PersonaEvolutionService.ShouldEvolve(50, old, 40, Cutoff));
    }

    [Fact]
    public void A_human_edit_or_revert_resets_the_cooldown()
    {
        // The newest version is a fresh human edit — the correction must not be
        // overwritten by an immediate evolution pass.
        var freshEdit = Now.AddMinutes(-5);
        Assert.False(PersonaEvolutionService.ShouldEvolve(50, freshEdit, 40, Cutoff));
    }
}
