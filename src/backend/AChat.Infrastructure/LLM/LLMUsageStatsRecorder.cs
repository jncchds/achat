using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Core.Services;
using AChat.Infrastructure.Data;

namespace AChat.Infrastructure.LLM;

public sealed class LLMUsageStatsRecorder : ILLMUsageStatsRecorder
{
    private readonly AppDbContext _db;

    public LLMUsageStatsRecorder(AppDbContext db)
    {
        _db = db;
    }

    public async Task RecordAsync(
        Guid userId,
        Guid? botId,
        LLMProviderPreset preset,
        LLMTokenUsageStats? usage,
        CancellationToken ct = default)
    {
        var totalTokens = usage?.TotalTokens;
        if (!totalTokens.HasValue && usage?.PromptTokens is int promptTokens && usage.CompletionTokens is int completionTokens)
            totalTokens = promptTokens + completionTokens;

        _db.LLMProviderUsageStats.Add(new LLMProviderUsageStat
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BotId = botId,
            LLMProviderPresetId = preset.Id,
            Provider = preset.Provider,
            ProviderUrl = ResolveProviderUrl(preset),
            PromptModel = preset.ModelName,
            PromptTokens = usage?.PromptTokens,
            CompletionTokens = usage?.CompletionTokens,
            TotalTokens = totalTokens,
            CreatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
    }

    private static string ResolveProviderUrl(LLMProviderPreset preset)
    {
        if (!string.IsNullOrWhiteSpace(preset.BaseUrl))
            return preset.BaseUrl.Trim();

        return preset.Provider switch
        {
            LLMProvider.OpenAI => "https://api.openai.com/v1/",
            LLMProvider.GoogleAIStudio => "https://generativelanguage.googleapis.com/v1beta/",
            LLMProvider.Ollama => "http://localhost:11434/",
            _ => string.Empty
        };
    }
}
