using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace AChat.Infrastructure.LLM;

public class ChatContextBuilder
{
    private readonly AppDbContext _db;
    private readonly int _ragTopK;
    private readonly int _recentWindowSize;

    public ChatContextBuilder(AppDbContext db, int ragTopK = 5, int recentWindowSize = 20)
    {
        _db = db;
        _ragTopK = ragTopK;
        _recentWindowSize = recentWindowSize;
    }

    /// <summary>
    /// Builds the full LLMChatRequest from bot persona, memory summaries, RAG recall and recent history.
    /// </summary>
    public async Task<LLMChatRequest> BuildAsync(
        Bot bot,
        Guid userId,
        string userMessage,
        float[]? queryEmbedding,
        CancellationToken ct = default)
    {
        var systemPrompt = BuildSystemPrompt(bot);

        // Latest memory summary for this bot+user
        var summary = await _db.BotMemorySummaries
            .Where(s => s.BotId == bot.Id && s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (summary is not null)
            systemPrompt += $"\n\n## Conversation Summary\n{summary.SummaryText}";

        // RAG: top-K relevant past messages by vector similarity
        if (queryEmbedding is not null)
        {
            var queryVector = new Vector(queryEmbedding);
            var ragMessages = await _db.Messages
                .Where(m => m.BotId == bot.Id && m.UserId == userId && m.Embedding != null)
                .OrderBy(m => m.Embedding!.CosineDistance(queryVector))
                .Take(_ragTopK)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Role, m.Content, m.CreatedAt })
                .ToListAsync(ct);

            if (ragMessages.Count > 0)
            {
                systemPrompt += "\n\n## Relevant Past Messages\n";
                foreach (var msg in ragMessages)
                    systemPrompt += $"[{msg.Role}]: {msg.Content}\n";
            }
        }

        // Recent raw messages (last N, excluding those that are already in a summary range)
        var recentMessages = await _db.Messages
            .Where(m => m.BotId == bot.Id && m.UserId == userId
                        && m.Role != MessageRole.System)
            .OrderByDescending(m => m.CreatedAt)
            .Take(_recentWindowSize)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new ChatMessage
            {
                Role = m.Role == MessageRole.User ? "user" : "assistant",
                Content = m.Content
            })
            .ToListAsync(ct);

        // Append current user message
        recentMessages.Add(new ChatMessage { Role = "user", Content = userMessage });

        return new LLMChatRequest
        {
            SystemPrompt = systemPrompt,
            Messages = recentMessages
        };
    }

    private static string BuildSystemPrompt(Bot bot)
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"You are {bot.Name}.");

        if (bot.Age.HasValue)
            lines.AppendLine($"Age: {bot.Age}");

        if (!string.IsNullOrWhiteSpace(bot.Gender))
            lines.AppendLine($"Gender: {bot.Gender}");

        lines.AppendLine();
        lines.Append(bot.EvolvingPersonaPrompt);

        return lines.ToString().Trim();
    }
}
