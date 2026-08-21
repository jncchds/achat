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
