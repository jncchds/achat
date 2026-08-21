using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// The persona block of every prompt (§2.4, §5.5). Phase 1 carries the core layer
/// only; directives and evolved traits join in Phase 5, in the fixed order
/// core → evolved → directives (directives last so they win ties).
/// </summary>
public static class PromptAssembly
{
    public static string BuildPersonaBlock(Bot bot)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("You are ").Append(bot.Name)
          .Append(", a real, persistent friend in the user's life. ")
          .Append("You remember them, you have a continuous relationship, and you speak " +
                  "as yourself — never as an assistant, model, or program.\n\n");

        if (!string.IsNullOrWhiteSpace(bot.CorePersona))
        {
            sb.Append("Who you are:\n").Append(bot.CorePersona.Trim()).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(bot.SpeechStyle))
        {
            sb.Append('\n').Append("How you speak:\n").Append(bot.SpeechStyle.Trim());
        }
        return sb.ToString();
    }
}

/// <summary>
/// One turn of conversation: persist the user's message, assemble the prompt, stream
/// the reply, persist it, and log the interaction (§4.4). All provider errors are
/// captured — the log is the debugging surface.
/// </summary>
public sealed class ChatService
{
    private readonly AppDbContext _db;
    private readonly ILlmProviderFactory _factory;
    private readonly IApiKeyProtector _keys;
    private readonly int _historyTurns;

    public sealed record ChatRunResult(
        int UserMessageId,
        int? AssistantMessageId,
        string AssistantContent,
        bool Canceled,
        bool Failed,
        string? Error,
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens);

    public ChatService(AppDbContext db, ILlmProviderFactory factory, IApiKeyProtector keys, IConfiguration config)
    {
        _db = db;
        _factory = factory;
        _keys = keys;
        _historyTurns = config.GetValue("Chat:HistoryTurns", 20);
    }

    public async Task<ChatRunResult> RunAsync(
        int botId,
        int userId,
        string content,
        Func<int, Task> onStart,
        Func<string, Task> onContentDelta,
        Func<string, Task> onReasoningDelta,
        CancellationToken ct)
    {
        content = content.Trim();
        if (content.Length == 0)
            throw new ArgumentException("Message is empty.");

        var bot = await _db.Bots.Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == botId && b.OwnerId == userId)
            ?? throw new KeyNotFoundException($"Bot {botId} not found for user {userId}.");
        var preset = bot.Preset;

        // ── Timeline: one continuous conversation per (bot, user) (§2.3) ────────
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.BotId == botId && c.UserId == userId);
        if (conversation is null)
        {
            conversation = new Conversation
            {
                BotId = botId,
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                LastMessageAt = DateTime.UtcNow,
            };
            _db.Conversations.Add(conversation);
        }

        var userMessage = new Message
        {
            Conversation = conversation,
            Role = MessageRole.User,
            Content = content,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Messages.Add(userMessage);
        conversation.LastMessageAt = userMessage.CreatedAt;
        await _db.SaveChangesAsync();

        // The user's turn is on the record — the client can now show the run as started.
        if (onStart is not null) await onStart(userMessage.Id);

        // ── Prompt: persona + recent-history window (Phase 1 scope) ─────────────
        var history = await _db.Messages
            .Where(m => m.ConversationId == conversation.Id && m.Id < userMessage.Id)
            .OrderByDescending(m => m.Id)
            .Take(_historyTurns)
            .ToListAsync();
        history.Reverse();

        var requestMessages = new List<LlmChatMessage>
        {
            new(MessageRole.System, PromptAssembly.BuildPersonaBlock(bot)),
        };
        requestMessages.AddRange(history.Select(m => new LlmChatMessage(m.Role, m.Content)));
        requestMessages.Add(new LlmChatMessage(MessageRole.User, content));

        // ── Provider ─────────────────────────────────────────────────────────────
        if (!_factory.IsSupported(preset.Provider))
            throw new NotSupportedException(
                $"Provider '{preset.Provider}' is not available in this build yet (v1: OpenAICompatible).");

        string? apiKey = null;
        if (preset.ApiKeyEncrypted is not null)
        {
            try
            {
                apiKey = _keys.Unprotect(Convert.ToBase64String(preset.ApiKeyEncrypted));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                throw new LlmProviderException(
                    "The stored API key could not be decrypted — the Data Protection key ring " +
                    "is unavailable (e.g. the database was restored without its key material). " +
                    "Re-enter the API key in the preset editor.", null, null);
            }
        }

        var provider = _factory.Create(new LlmPresetConfig
        {
            Provider = preset.Provider,
            Endpoint = preset.BaseEndpoint,
            ApiKey = apiKey,
            GenerationModel = preset.GenerationModel,
            UtilityModel = preset.UtilityModel,
            EmbeddingModel = preset.EmbeddingModel,
            Temperature = preset.Temperature,
            MaxTokens = preset.MaxTokens,
            TimeoutMs = preset.TimeoutMs ?? 120_000,
        });

        var request = new LlmChatRequest
        {
            Messages = requestMessages,
            Model = preset.GenerationModel,
            Temperature = preset.Temperature,
            MaxTokens = preset.MaxTokens,
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var contentBuilder = new System.Text.StringBuilder();
        var reasoningBuilder = new System.Text.StringBuilder();
        string? finishReason = null;
        int? promptTokens = null, completionTokens = null, totalTokens = null;

        bool canceled = false, failed = false;
        string? error = null;

        try
        {
            await foreach (var chunk in provider.StreamChatAsync(request, ct))
            {
                if (chunk.Content is not null)
                {
                    contentBuilder.Append(chunk.Content);
                    await onContentDelta(chunk.Content);
                }
                if (chunk.Reasoning is not null)
                {
                    reasoningBuilder.Append(chunk.Reasoning);
                    await onReasoningDelta(chunk.Reasoning);
                }
                if (chunk.FinishReason is not null) finishReason = chunk.FinishReason;
                if (chunk.PromptTokens is not null) promptTokens = chunk.PromptTokens;
                if (chunk.CompletionTokens is not null) completionTokens = chunk.CompletionTokens;
                if (chunk.TotalTokens is not null) totalTokens = chunk.TotalTokens;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (finishReason is not null)
            {
                // The model finished generating; the disconnect only cut off the tail
                // (final usage chunk / relay). Count it as a completed call, not a stop.
            }
            else
            {
                canceled = true;
                error = "Stopped by the user.";
            }
        }
        catch (LlmProviderException ex)
        {
            failed = true;
            error = ex.HttpStatus is { } s
                ? $"Provider returned HTTP {s}: {Truncate(ex.ErrorPayload, 2000)}"
                : $"{ex.Message}{(ex.ErrorPayload is not null ? " — " + Truncate(ex.ErrorPayload, 2000) : "")}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed = true;
            error = ex.Message;
        }

        stopwatch.Stop();

        // ── Persist the assistant turn (partial on cancel, per §4.2) ────────────
        int? assistantMessageId = null;
        var fullContent = contentBuilder.ToString();
        if (fullContent.Length > 0)
        {
            var assistant = new Message
            {
                Conversation = conversation,
                Role = MessageRole.Assistant,
                Content = fullContent,
                Reasoning = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
                IsComplete = !canceled && !failed,
                CreatedAt = DateTime.UtcNow,
            };
            _db.Messages.Add(assistant);
            conversation.LastMessageAt = assistant.CreatedAt;
            await _db.SaveChangesAsync();
            assistantMessageId = assistant.Id;
        }
        else if (failed || canceled)
        {
            await _db.SaveChangesAsync();
        }

        // ── Interaction log — every call, success or failure (§4.4) ─────────────
        var requestBody = System.Text.Json.JsonSerializer.Serialize(
            requestMessages
                .Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content })
                .ToList());

        var interaction = new LlmInteraction
        {
            Purpose = InteractionPurpose.Chat,
            BotId = bot.Id,
            UserId = userId,
            PresetId = preset.Id,
            Provider = preset.Provider,
            Model = preset.GenerationModel,
            Endpoint = string.IsNullOrWhiteSpace(preset.BaseEndpoint) ? "(default)" : preset.BaseEndpoint,
            RequestBody = requestBody,
            Reasoning = reasoningBuilder.Length > 0 ? reasoningBuilder.ToString() : null,
            ResponseContent = fullContent.Length > 0 ? fullContent : error,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = totalTokens,
            FinishReason = finishReason,
            Failed = failed || canceled,
            FailureReason = error,
            CreatedAt = DateTime.UtcNow,
        };
        _db.LlmInteractions.Add(interaction);
        await _db.SaveChangesAsync();

        return new ChatRunResult(
            userMessage.Id,
            assistantMessageId,
            fullContent,
            canceled,
            failed,
            error,
            promptTokens,
            completionTokens,
            totalTokens);
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
