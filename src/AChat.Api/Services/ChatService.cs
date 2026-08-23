using AChat.Core;
using AChat.Core.Interfaces;
using AChat.Core.Models;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Services;

/// <summary>
/// One turn of conversation: persist the user's message, retrieve memories, assemble
/// the prompt, stream the reply, persist it, and log the interaction (§4.4). All
/// provider errors are captured — the log is the debugging surface.
/// </summary>
public sealed class ChatService
{
    private readonly AppDbContext _db;
    private readonly ILlmProviderFactory _factory;
    private readonly IApiKeyProtector _keys;
    private readonly MemoryRetriever _memories;
    private readonly IConfiguration _config;
    private readonly ILogger<ChatService> _log;
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
        int? TotalTokens,
        IReadOnlyList<int> RecalledMemoryIds);

    public ChatService(AppDbContext db, ILlmProviderFactory factory, IApiKeyProtector keys,
        MemoryRetriever memories, IConfiguration config, ILogger<ChatService> log)
    {
        _db = db;
        _factory = factory;
        _keys = keys;
        _memories = memories;
        _config = config;
        _log = log;
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

        var bot = await ResolveBotAsync(botId, userId);
        await EnsureUsableAsync(bot);

        // The chain of thought is relayed only when the creator enabled it (§2.5).
        // It is always persisted and logged (§4.4) — this controls display only.
        var reasoning = bot.ShowThinking ? onReasoningDelta : _ => Task.CompletedTask;

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

        return await RunGenerationAsync(bot, userId, conversation, userMessage,
            onContentDelta, reasoning, ct);
    }

    /// <summary>
    /// "Try again" (§12b): re-run generation for the last user message that has no
    /// complete reply — a failed turn is no longer a dead end.
    ///
    /// - last message is a user turn (no reply at all) → retry it;
    /// - last message is a partial assistant turn (<c>IsComplete = false</c>, a stop
    ///   or a failure) → the partial is deleted and the user turn before it is
    ///   retried;
    /// - the last turn already completed → <see cref="NothingToRetryException"/>.
    ///
    /// No new user message is appended — the timeline keeps its shape. The kill
    /// switch and the usage quota apply exactly as to a fresh turn: a retry spends
    /// the key.
    /// </summary>
    public async Task<ChatRunResult> RetryLastAsync(
        int botId,
        int userId,
        Func<int, Task> onStart,
        Func<string, Task> onContentDelta,
        Func<string, Task> onReasoningDelta,
        CancellationToken ct)
    {
        var bot = await ResolveBotAsync(botId, userId);
        await EnsureUsableAsync(bot);

        // Same display rule as a fresh turn (§2.5).
        var reasoning = bot.ShowThinking ? onReasoningDelta : _ => Task.CompletedTask;

        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(c => c.BotId == botId && c.UserId == userId)
            ?? throw new KeyNotFoundException($"No conversation with bot {botId} yet.");

        var last = await _db.Messages
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new NothingToRetryException("The conversation is empty — nothing to retry.");

        Message target;
        if (last.Role == MessageRole.User)
        {
            target = last; // the turn went unanswered — retry it
        }
        else if (!last.IsComplete)
        {
            // The last reply is a partial (stopped or failed) — drop it and retry
            // the user turn it was answering.
            var previous = await _db.Messages
                .Where(m => m.ConversationId == conversation.Id && m.Id < last.Id)
                .OrderByDescending(m => m.Id)
                .FirstOrDefaultAsync(ct)
                ?? throw new NothingToRetryException("Nothing to retry.");
            if (previous.Role != MessageRole.User)
                throw new NothingToRetryException("Nothing to retry.");
            _db.Messages.Remove(last);
            await _db.SaveChangesAsync(ct);
            target = previous;
        }
        else
        {
            throw new NothingToRetryException("The last turn already completed — nothing to retry.");
        }

        if (onStart is not null) await onStart(target.Id);

        return await RunGenerationAsync(bot, userId, conversation, target,
            onContentDelta, reasoning, ct);
    }

    /// <summary>
    /// Resolve the bot for this user — owner or approved invitee (§2.5); everyone
    /// else gets not-found, not forbidden.
    /// </summary>
    private async Task<Bot> ResolveBotAsync(int botId, int userId)
    {
        return await _db.Bots.Include(b => b.Preset)
            .FirstOrDefaultAsync(b => b.Id == botId
                && (b.OwnerId == userId
                    || _db.BotAccesses.Any(a => a.BotId == b.Id && a.UserId == userId)))
            ?? throw new KeyNotFoundException($"Bot {botId} not found for user {userId}.");
    }

    /// <summary>
    /// Kill switch + usage quota (§12a, §2.5). Checked before any persistence or
    /// LLM call: a suspended / quota-exceeded bot spends no key and logs no
    /// interaction — a retry is subject to the same check.
    /// </summary>
    private async Task EnsureUsableAsync(Bot bot)
    {
        if (bot.Suspended)
            throw new BotUnavailableException(
                "This bot has been paused by its creator. Please try again later.")
            {
                Suspended = true,
            };
        if (bot.MaxInteractions is > 0)
        {
            var used = await _db.LlmInteractions.CountAsync(i => i.BotId == bot.Id);
            if (used >= bot.MaxInteractions)
                throw new BotUnavailableException(
                    $"This bot has reached its usage limit ({bot.MaxInteractions} interactions). " +
                    "Ask its creator to raise the limit in the bot settings.")
                {
                    QuotaExceeded = true,
                };
        }
    }

    /// <summary>
    /// The generation half of a turn: provider, memory retrieval, prompt assembly,
    /// streaming, persistence, the interaction log, and recall stats. Shared by a
    /// fresh turn and a retry (§12b).
    /// </summary>
    private async Task<ChatRunResult> RunGenerationAsync(
        Bot bot, int userId, Conversation conversation, Message userMessage,
        Func<string, Task> onContentDelta,
        Func<string, Task> onReasoningDelta,
        CancellationToken ct)
    {
        var preset = bot.Preset;

        // ── Provider ─────────────────────────────────────────────────────────────

        // ── Provider ─────────────────────────────────────────────────────────────
        if (!_factory.IsSupported(preset.Provider))
            throw new NotSupportedException(
                $"Provider '{preset.Provider}' has no registered strategy in this build.");

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

        // ── Memory retrieval (§5.4) — best-effort: a retrieval failure degrades to
        //    a memoryless turn, it never blocks the conversation ─────────────────
        var now = DateTime.UtcNow;
        var recalled = new List<ScoredMemory>();
        if (!string.IsNullOrWhiteSpace(preset.EmbeddingModel))
        {
            try
            {
                var queryVectors = await provider.EmbedAsync(preset.EmbeddingModel, [userMessage.Content], ct);
                var candidates = await _memories.SearchAsync(
                    _db, preset.EmbeddingModel, bot.Id, userId,
                    queryVectors[0], _memories.TopK * 4, ct);
                recalled = MemoryRetriever.Select(candidates, now, _memories);
            }
            // A provider timeout is also a TaskCanceledException — swallow it only
            // when it is not the user's stop/disconnect, so a slow embedding call
            // degrades to a memoryless turn instead of killing the run.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "Memory retrieval failed for bot {BotId} — continuing without memories", bot.Id);
            }
        }

        // ── Prompt assembly (§5.5, §5.9, §2.4) ─────────────────────────────────
        // System block: persona (core → evolved traits → directives) →
        // current time in the user's zone → memories → summary.
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        var summary = await _db.ConversationSummaries.AsNoTracking()
            .FirstOrDefaultAsync(s => s.BotId == bot.Id && s.UserId == userId);

        // The bot's current personality layers (§2.4): the latest evolved-traits
        // version and the active directives — directives last, so they win ties.
        var evolvedTraits = await _db.PersonaVersions.AsNoTracking()
            .Where(v => v.BotId == bot.Id)
            .OrderByDescending(v => v.Id)
            .Select(v => v.Content)
            .FirstOrDefaultAsync(ct);
        var directives = await _db.BotDirectives.AsNoTracking()
            .Where(d => d.BotId == bot.Id && d.IsActive)
            .OrderBy(d => d.Id)
            .Select(d => d.Text)
            .ToListAsync(ct);

        recalled = _budget.TrimMemories(recalled);
        var memoryBlock = recalled.Count > 0
            ? MemoryRetriever.BuildMemoryBlock(recalled, now)
            : null;
        var systemBlock = PromptAssembly.BuildSystemBlock(
            bot, user?.Timezone, now, summary, _budget, memoryBlock,
            evolvedTraits, directives);

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
            (returnGap is null ? "" : returnGap + "\n") + userMessage.Content));

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
        // A provider timeout surfaces as a TaskCanceledException even though the
        // user did not cancel — treat it as a failed call, not a stop.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            failed = true;
            error = ex is OperationCanceledException
                ? "The model request timed out."
                : ex.Message;
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
            RecalledMemoryIds = recalled.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(recalled.Select(m => m.Memory.Id).ToArray())
                : null,
            CreatedAt = DateTime.UtcNow,
        };
        _db.LlmInteractions.Add(interaction);

        // Recall stats (§5.8): LastRecalledAt / RecallCount feed the ranking's recency
        // signal and the viewer's "last recalled" column.
        if (!failed && recalled.Count > 0)
        {
            var recalledIds = recalled.Select(m => m.Memory.Id).ToArray();
            var rows = await _db.Memories.Where(m => recalledIds.Contains(m.Id)).ToListAsync();
            foreach (var m in rows)
            {
                m.LastRecalledAt = interaction.CreatedAt;
                m.RecallCount += 1;
            }
        }

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
            totalTokens,
            recalled.Select(m => m.Memory.Id).ToArray());
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
