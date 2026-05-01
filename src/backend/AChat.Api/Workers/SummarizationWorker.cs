using AChat.Infrastructure;
using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;

namespace AChat.Api.Workers;

public class SummarizationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SummarizationWorker> _logger;
    private readonly EvolutionOptions _opts;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public SummarizationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<SummarizationWorker> logger,
        IOptions<EvolutionOptions> opts)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _opts = opts.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in SummarizationWorker.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<ILLMProviderFactory>();

        // Find (bot, user, conversation) groups whose total message count exceeds the
        // threshold. SummarizeAsync validates the unsummarized count before proceeding,
        // so over-triggering here is safe.
        var pairs = await db.Messages
            .Where(m => m.Role != MessageRole.System)
            .GroupBy(m => new { m.BotId, m.UserId, m.ConversationId })
            .Select(g => new { g.Key.BotId, g.Key.UserId, g.Key.ConversationId, Count = g.Count() })
            .Where(x => x.Count > _opts.SummarizationThreshold)
            .ToListAsync(ct);

        foreach (var pair in pairs)
        {
            try
            {
                await SummarizeAsync(db, factory, pair.BotId, pair.UserId, pair.ConversationId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to summarize for bot {BotId}, user {UserId}, conversation {ConversationId}.",
                    pair.BotId, pair.UserId, pair.ConversationId);
            }
        }
    }

    private async Task SummarizeAsync(
        AppDbContext db,
        ILLMProviderFactory factory,
        Guid botId,
        Guid userId,
        Guid conversationId,
        CancellationToken ct)
    {
        var bot = await db.Bots
            .Include(b => b.LLMProviderPreset)
            .Include(b => b.EmbeddingPreset)
            .FirstOrDefaultAsync(b => b.Id == botId, ct);

        if (bot?.LLMProviderPreset is null) return;

        // Find the creation time of the last summarized message so we only fetch messages
        // that come after it. MaxAsync on a nullable projection returns null when there are
        // no summaries yet, meaning all messages are candidates.
        var latestSummaryEndTime = await db.BotMemorySummaries
            .Where(s => s.BotId == botId && s.UserId == userId && s.ConversationId == conversationId)
            .Join(db.Messages, s => s.MessageRangeEnd, m => m.Id, (s, m) => (DateTime?)m.CreatedAt)
            .MaxAsync(ct);

        var messages = await db.Messages
            .Where(m => m.BotId == botId && m.UserId == userId
                        && m.ConversationId == conversationId
                        && (latestSummaryEndTime == null || m.CreatedAt > latestSummaryEndTime)
                        && m.Role != MessageRole.System)
            .OrderBy(m => m.CreatedAt)
            .Take(_opts.SummarizationBatchSize)
            .ToListAsync(ct);

        if (messages.Count < _opts.SummarizationBatchSize) return;

        var conversation = string.Join("\n", messages
            .Select(m => $"{(m.Role == MessageRole.User ? "User" : "Bot")}: {m.Content}"));

        var summaryRequest = new LLMChatRequest
        {
            SystemPrompt = "You are a helpful assistant that summarizes conversations concisely. " +
                           "Preserve important facts, preferences, and context.",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = $"Summarize this conversation:\n\n{conversation}"
                }
            ]
        };

        var chatProvider = factory.GetChatProvider(bot.LLMProviderPreset);
        var summaryText = await chatProvider.GenerateChatAsync(summaryRequest, ct);

        var summary = new BotMemorySummary
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            UserId = userId,
            ConversationId = conversationId,
            SummaryText = summaryText,
            MessageRangeStart = messages.First().Id,
            MessageRangeEnd = messages.Last().Id,
            CreatedAt = DateTime.UtcNow
        };

        // Embed summary
        if (bot.EmbeddingPreset is not null)
        {
            try
            {
                var embProvider = factory.GetEmbeddingProvider(bot.EmbeddingPreset);
                var emb = await embProvider.GenerateEmbeddingAsync(summaryText, ct);
                summary.Embedding = EmbeddingVectorCompatibility.ToVectorOrNull(emb);
            }
            catch { /* non-fatal */ }
        }

        db.BotMemorySummaries.Add(summary);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Summarized {Count} messages for bot {BotId}, user {UserId}, conversation {ConversationId}.",
            messages.Count, botId, userId, conversationId);
    }
}
