using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

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
    private readonly IConfiguration _config;
    private readonly int _historyTurns;
    private readonly TimeSpan _gapThreshold;
    private readonly PromptBudget _budget;

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
        _config = config;
        _historyTurns = config.GetValue("Chat:HistoryTurns", 20);
        _gapThreshold = TimeSpan.FromMinutes(config.GetValue("Chat:GapThresholdMinutes", 360));
        _budget = PromptBudget.FromConfiguration(config);
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

        // ── Prompt assembly (§5.5, §5.9) ─────────────────────────────────────────
        // System block: persona → current time in the user's zone → rolling summary.
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        var summary = await _db.ConversationSummaries.AsNoTracking()
            .FirstOrDefaultAsync(s => s.BotId == bot.Id && s.UserId == userId);

        var now = DateTime.UtcNow;
        var systemBlock = PromptAssembly.BuildSystemBlock(bot, user?.Timezone, now, summary, _budget);

        // Recent history: last N turns verbatim, with gap markers where the silence
        // was long enough to be noticed (§5.9.2).
        var historyRaw = await _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Id < userMessage.Id)
            .OrderByDescending(m => m.Id)
            .Take(_historyTurns)
            .ToListAsync();
        historyRaw.Reverse();

        var history = PromptAssembly.BuildHistoryMessages(historyRaw, _gapThreshold);

        // Budget: history drops oldest first (§5.5).
        history = DropOldestOverBudget(history, _budget.HistoryTokens);

        var requestMessages = new List<LlmChatMessage>
        {
            new(MessageRole.System, systemBlock),
        };
        requestMessages.AddRange(history);

        // The user came back after a long absence — the gap between the window's end
        // and this new message gets a marker, prefixed to the new message (§5.9.2).
        var returnGap = historyRaw.Count > 0
            ? PromptAssembly.FormatReturnGap(historyRaw[^1].CreatedAt, now, _gapThreshold)
            : null;
        requestMessages.Add(new LlmChatMessage(MessageRole.User,
            (returnGap is null ? "" : returnGap + "\n") + content));

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

    /// <summary>History budget: keep the newest messages, drop oldest first (§5.5).</summary>
    private static List<LlmChatMessage> DropOldestOverBudget(List<LlmChatMessage> history, int maxTokens)
    {
        if (maxTokens <= 0) return history;
        var used = 0;
        var kept = new List<LlmChatMessage>();
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var tokens = PromptAssembly.EstimateTokens(history[i].Content);
            if (used + tokens > maxTokens && kept.Count > 0) break;
            used += tokens;
            kept.Insert(0, history[i]);
        }
        return kept;
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
