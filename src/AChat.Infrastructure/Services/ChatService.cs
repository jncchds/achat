using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AChat.Core.DTOs.Conversations;
using AChat.Core.Entities;
using AChat.Core.Enums;
using AChat.Core.Interfaces.Services;
using AChat.Core.Options;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using ChatOptions = AChat.Core.Options.ChatOptions;

namespace AChat.Infrastructure.Services;

public partial class ChatService(
    AppDbContext db,
    IConversationNotifier notifier,
    IOptions<ChatOptions> chatOptions,
    ILogger<ChatService> logger) : IChatService
{
    public async IAsyncEnumerable<string> StreamAsync(
        Guid conversationId,
        Guid userId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var conv = await db.Conversations
            .Include(c => c.Bot).ThenInclude(b => b.Preset)
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct);

        if (conv is null)
        {
            LogConversationNotFound(logger, conversationId);
            yield break;
        }

        var bot = conv.Bot;
        var preset = bot.Preset;

        // Load user memory for this (bot, user) pair
        var memory = await db.BotUserMemories
            .FirstOrDefaultAsync(m => m.BotId == bot.Id && m.UserId == userId, ct);

        var contextWindow = chatOptions.Value.ContextWindowMessages;
        var recentMessages = await db.Messages
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(contextWindow)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        // Semantic retrieval: surface relevant older messages outside the context window
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null;
        Vector? userEmbeddingVector = null;
        List<Message> semanticMessages = [];
        if (!string.IsNullOrEmpty(preset.EmbeddingModel))
        {
            try
            {
                var embKernel = SemanticKernelFactory.BuildWithEmbedding(preset);
                embeddingGenerator = embKernel
                    .GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

                var embResult = await embeddingGenerator
                    .GenerateAsync([userMessage], cancellationToken: ct);
                var actualDim = embResult[0].Vector.Length;
                userEmbeddingVector = new Vector(embResult[0].Vector.ToArray());

                var recentIds = recentMessages.Select(m => m.Id).ToHashSet();
                var topIds = await db.Messages
                    .Where(m => m.ConversationId == conversationId
                             && !recentIds.Contains(m.Id)
                             && m.Embedding != null
                             && m.EmbeddingDimension == actualDim)
                    .OrderBy(m => m.Embedding!.CosineDistance(userEmbeddingVector))
                    .Take(chatOptions.Value.SemanticContextMessages)
                    .Select(m => m.Id)
                    .ToListAsync(ct);

                if (topIds.Count > 0)
                {
                    semanticMessages = await db.Messages
                        .Where(m => topIds.Contains(m.Id))
                        .OrderBy(m => m.CreatedAt)
                        .ToListAsync(ct);
                }
            }
            catch (Exception ex)
            {
                LogEmbeddingError(logger, ex);
            }
        }

        var chatHistory = BuildChatHistory(bot, memory, semanticMessages, recentMessages, userMessage);

        // Persist user message
        var userMsg = new Message
        {
            ConversationId = conversationId,
            Role = MessageRole.User,
            Content = userMessage,
            CreatedAt = DateTime.UtcNow
        };
        db.Messages.Add(userMsg);
        await db.SaveChangesAsync(ct);
        notifier.Notify(conversationId, new MessageDto(userMsg.Id, "user", userMsg.Content, userMsg.CreatedAt));

        var kernel = SemanticKernelFactory.Build(preset);

        // Register memory plugin scoped to this (bot, user)
        var memoryPlugin = new BotMemoryPlugin(db, bot.Id, userId, memory);
        kernel.Plugins.AddFromObject(memoryPlugin, "memory");

#pragma warning disable SKEXP0001
        var executionSettings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        };
#pragma warning restore SKEXP0001

        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var fullResponse = new System.Text.StringBuilder();
        int inputTokens = 0, outputTokens = 0;

        try
        {
            await foreach (var chunk in chatService.GetStreamingChatMessageContentsAsync(
                chatHistory, executionSettings, kernel, ct))
            {
                var text = chunk.Content;
                if (!string.IsNullOrEmpty(text))
                {
                    fullResponse.Append(text);
                    yield return text;
                }

                if (chunk.Metadata?.TryGetValue("Usage", out var usage) == true && usage is not null)
                {
                    // Extract token usage from metadata if available
                    try
                    {
                        var json = JsonSerializer.Serialize(usage);
                        var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("InputTokenCount", out var inp))
                            inputTokens = inp.GetInt32();
                        if (doc.RootElement.TryGetProperty("OutputTokenCount", out var out_))
                            outputTokens = out_.GetInt32();
                    }
                    catch { /* best-effort token count */ }
                }
            }
        }
        finally
        {
            // Always save assistant message and interaction log
            if (fullResponse.Length > 0)
            {
                var assistantMsg = new Message
                {
                    ConversationId = conversationId,
                    Role = MessageRole.Assistant,
                    Content = fullResponse.ToString(),
                    CreatedAt = DateTime.UtcNow
                };
                db.Messages.Add(assistantMsg);

                // Store embeddings for future semantic retrieval
                if (embeddingGenerator is not null && userEmbeddingVector is not null)
                {
                    try
                    {
                        var dim = userEmbeddingVector.ToArray().Length;
                        userMsg.Embedding = userEmbeddingVector;
                        userMsg.EmbeddingDimension = dim;
                        var assistantEmbResult = await embeddingGenerator
                            .GenerateAsync([assistantMsg.Content], cancellationToken: CancellationToken.None);
                        assistantMsg.Embedding = new Vector(assistantEmbResult[0].Vector.ToArray());
                        assistantMsg.EmbeddingDimension = dim;
                    }
                    catch (Exception ex)
                    {
                        LogEmbeddingError(logger, ex);
                    }
                }

                var interaction = new LlmInteraction
                {
                    BotId = bot.Id,
                    UserId = userId,
                    PresetId = preset.Id,
                    ConversationId = conversationId,
                    Endpoint = preset.ProviderUrl,
                    ModelName = preset.GenerationModel,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CreatedAt = DateTime.UtcNow
                };
                db.LlmInteractions.Add(interaction);

                conv.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                notifier.Notify(conversationId, new MessageDto(assistantMsg.Id, "assistant", assistantMsg.Content, assistantMsg.CreatedAt));
                LogChatCompleted(logger, conversationId, inputTokens, outputTokens);
            }
        }
    }

    public async Task<string> GenerateAsync(
        Guid botId,
        Guid userId,
        IReadOnlyList<(string Role, string Content)> messages,
        CancellationToken ct = default)
    {
        var bot = await db.Bots.Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == botId, ct);
        if (bot is null) return string.Empty;

        var kernel = SemanticKernelFactory.Build(bot.Preset);
        var chatService = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();

        foreach (var (role, content) in messages)
        {
            if (role == "system") history.AddSystemMessage(content);
            else if (role == "user") history.AddUserMessage(content);
            else history.AddAssistantMessage(content);
        }

        var result = await chatService.GetChatMessageContentAsync(history, cancellationToken: ct);

        var interaction = new LlmInteraction
        {
            BotId = botId,
            UserId = userId,
            PresetId = bot.PresetId,
            Endpoint = bot.Preset.ProviderUrl,
            ModelName = bot.Preset.GenerationModel,
            CreatedAt = DateTime.UtcNow
        };
        db.LlmInteractions.Add(interaction);
        await db.SaveChangesAsync(ct);

        return result.Content ?? string.Empty;
    }

    private static ChatHistory BuildChatHistory(
        Bot bot,
        BotUserMemory? memory,
        IReadOnlyList<Message> semanticContext,
        IEnumerable<Message> recentHistory,
        string newUserMessage)
    {
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(bot.Personality);

        if (memory?.Facts.Count > 0)
        {
            var factsText = string.Join("\n- ", memory.Facts);
            chatHistory.AddSystemMessage($"What you remember about this user:\n- {factsText}");
        }

        if (semanticContext.Count > 0)
        {
            var lines = semanticContext.Select(m => $"[{m.Role}]: {m.Content}");
            chatHistory.AddSystemMessage(
                $"Relevant earlier context from this conversation:\n{string.Join("\n", lines)}");
        }

        foreach (var msg in recentHistory)
        {
            if (msg.Role == MessageRole.User) chatHistory.AddUserMessage(msg.Content);
            else if (msg.Role == MessageRole.Assistant) chatHistory.AddAssistantMessage(msg.Content);
        }

        chatHistory.AddUserMessage(newUserMessage);
        return chatHistory;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId} not found")]
    private static partial void LogConversationNotFound(ILogger logger, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding generation failed")]
    private static partial void LogEmbeddingError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Chat completed for conversation {ConversationId}. Tokens: in={InputTokens} out={OutputTokens}")]
    private static partial void LogChatCompleted(ILogger logger, Guid conversationId, int inputTokens, int outputTokens);
}

internal sealed class BotMemoryPlugin(
    AppDbContext db,
    Guid botId,
    Guid userId,
    BotUserMemory? existingMemory)
{
    [KernelFunction("remember_fact")]
    [Description("Remember an important fact about the user for future conversations. Call this when the user reveals something worth remembering long-term.")]
    public async Task RememberFact([Description("The fact to remember about this user")] string fact)
    {
        if (string.IsNullOrWhiteSpace(fact)) return;

        if (existingMemory is not null)
        {
            if (!existingMemory.Facts.Contains(fact))
            {
                existingMemory.Facts.Add(fact);
                existingMemory.UpdatedAt = DateTime.UtcNow;
            }
        }
        else
        {
            existingMemory = new BotUserMemory
            {
                BotId = botId,
                UserId = userId,
                Facts = [fact],
                UpdatedAt = DateTime.UtcNow
            };
            db.BotUserMemories.Add(existingMemory);
        }

        await db.SaveChangesAsync();
    }
}
