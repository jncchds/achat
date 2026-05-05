using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AChat.Core.DTOs.Conversations;
using AChat.Core.Entities;
using AChat.Core.Enums;
using AChat.Core.Interfaces.Services;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace AChat.Infrastructure.Services;

public partial class ChatService(
    AppDbContext db,
    IConversationNotifier notifier,
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
            .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
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

        var chatHistory = BuildChatHistory(bot, memory, conv.Messages, userMessage);

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

                var interaction = new LlmInteraction
                {
                    BotId = bot.Id,
                    UserId = userId,
                    PresetId = preset.Id,
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
        IEnumerable<Message> history,
        string newUserMessage)
    {
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(bot.Personality);

        if (memory?.Facts.Count > 0)
        {
            var factsText = string.Join("\n- ", memory.Facts);
            chatHistory.AddSystemMessage($"What you remember about this user:\n- {factsText}");
        }

        foreach (var msg in history)
        {
            if (msg.Role == MessageRole.User) chatHistory.AddUserMessage(msg.Content);
            else if (msg.Role == MessageRole.Assistant) chatHistory.AddAssistantMessage(msg.Content);
        }

        chatHistory.AddUserMessage(newUserMessage);
        return chatHistory;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation {ConversationId} not found")]
    private static partial void LogConversationNotFound(ILogger logger, Guid conversationId);

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
