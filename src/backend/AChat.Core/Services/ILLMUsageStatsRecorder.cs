using AChat.Core.Entities;
using AChat.Core.LLM;

namespace AChat.Core.Services;

public interface ILLMUsageStatsRecorder
{
    Task RecordAsync(
        Guid userId,
        Guid? botId,
        LLMProviderPreset preset,
        LLMTokenUsageStats? usage,
        CancellationToken ct = default);
}
