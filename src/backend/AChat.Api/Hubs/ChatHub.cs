using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Core.Services;
using AChat.Infrastructure;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace AChat.Api.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private readonly ILLMProviderFactory _providerFactory;
    private readonly ILLMUsageStatsRecorder _usageStatsRecorder;
    private readonly EvolutionOptions _evolutionOptions;
    private readonly IChatConnectionRegistry _connectionRegistry;

    // Per-conversation semaphores to prevent concurrent LLM calls on the same conversation
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _conversationLocks = new();

    public ChatHub(
        AppDbContext db,
        ILLMProviderFactory providerFactory,
        ILLMUsageStatsRecorder usageStatsRecorder,
        IOptions<EvolutionOptions> evolutionOptions,
        IChatConnectionRegistry connectionRegistry)
    {
        _db = db;
        _providerFactory = providerFactory;
        _usageStatsRecorder = usageStatsRecorder;
        _evolutionOptions = evolutionOptions.Value;
        _connectionRegistry = connectionRegistry;
    }

    public override Task OnConnectedAsync()
    {
        _connectionRegistry.Register(GetUserId(), Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _connectionRegistry.Unregister(GetUserId(), Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task SendMessage(Guid botId, string content, Guid? conversationId = null)
    {
        var userId = GetUserId();
        var ct = Context.ConnectionAborted;

        // Load bot and verify access
        var bot = await _db.Bots
            .Include(b => b.LLMProviderPreset)
            .Include(b => b.EmbeddingPreset)
            .FirstOrDefaultAsync(b => b.Id == botId, ct);

        if (bot is null)
        {
            await Clients.Caller.SendAsync("Error", "Bot not found.", ct);
            return;
        }

        // Access check: owner is always allowed; others need BotAccessList(Allowed)
        bool canAccess = bot.OwnerId == userId
            || await _db.BotAccessLists.AnyAsync(a =>
                a.BotId == botId
                && a.SubjectType == AccessSubjectType.AchatUser
                && a.SubjectId == userId.ToString()
                && a.Status == AccessStatus.Allowed, ct);

        if (!canAccess)
        {
            await Clients.Caller.SendAsync("Error", "Access denied.", ct);
            return;
        }

        var conversation = await ResolveConversationAsync(botId, userId, conversationId, ct);
        if (conversation is null)
        {
            await Clients.Caller.SendAsync("Error", "Conversation not found.", ct);
            return;
        }

        await Clients.Caller.SendAsync("ConversationResolved", conversation.Id, conversation.Title, ct);

        // Persist user message
        var userMessage = new Message
        {
            Id = Guid.NewGuid(),
            BotId = botId,
            UserId = userId,
            ConversationId = conversation.Id,
            Role = MessageRole.User,
            Content = content,
            Source = MessageSource.Web,
            CreatedAt = DateTime.UtcNow
        };
        _db.Messages.Add(userMessage);

        if (conversation.Title == "New conversation")
            conversation.Title = BuildConversationTitle(content);
        conversation.UpdatedAt = DateTime.UtcNow;
        conversation.LastMessageAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // Generate query embedding if embedding preset is configured
        float[]? queryEmbedding = null;
        if (bot.EmbeddingPreset is not null)
        {
            try
            {
                var embeddingProvider = _providerFactory.GetEmbeddingProvider(bot.EmbeddingPreset);
                queryEmbedding = await embeddingProvider.GenerateEmbeddingAsync(content, ct);
                var userEmbeddingVector = EmbeddingVectorCompatibility.ToVectorOrNull(queryEmbedding);
                if (userEmbeddingVector is not null)
                {
                    userMessage.Embedding = userEmbeddingVector;
                    await _db.SaveChangesAsync(ct);
                }
                else
                {
                    queryEmbedding = null;
                }
            }
            catch
            {
                // Embedding failure is non-fatal; proceed without RAG
            }
        }

        // Build context
        if (bot.LLMProviderPreset is null)
        {
            await Clients.Caller.SendAsync("Error", "Bot has no LLM preset configured.", ct);
            return;
        }

        // Acquire per-conversation lock to prevent concurrent LLM calls on the same conversation.
        // If already processing, reject immediately rather than queuing indefinitely.
        var convLock = _conversationLocks.GetOrAdd(conversation.Id, _ => new SemaphoreSlim(1, 1));
        if (!await convLock.WaitAsync(0, ct))
        {
            await Clients.Caller.SendAsync("Error", "Still processing previous message. Please wait.", ct);
            return;
        }

        try
        {
            var contextBuilder = new ChatContextBuilder(
                _db, _evolutionOptions.RagTopK, _evolutionOptions.RecentMessageWindowSize);

            var chatRequest = await contextBuilder.BuildAsync(
                bot,
                userId,
                conversation.Id,
                content,
                queryEmbedding,
                ct);

            // Stream response
            var chatProvider = _providerFactory.GetChatProvider(bot.LLMProviderPreset);
            var responseTokens = new StringBuilder();
            LLMTokenUsageStats? usage = null;

            await foreach (var update in chatProvider.StreamChatCompletionAsync(chatRequest, ct))
            {
                if (!string.IsNullOrEmpty(update.Content))
                {
                    responseTokens.Append(update.Content);
                    await Clients.Caller.SendAsync("ReceiveToken", update.Content, ct);
                }

                if (update.Usage is not null)
                    usage = update.Usage;
            }

            // Persist assistant message
            var assistantMessage = new Message
            {
                Id = Guid.NewGuid(),
                BotId = botId,
                UserId = userId,
                ConversationId = conversation.Id,
                Role = MessageRole.Assistant,
                Content = responseTokens.ToString(),
                Source = MessageSource.Web,
                CreatedAt = DateTime.UtcNow
            };
            _db.Messages.Add(assistantMessage);

            conversation.UpdatedAt = DateTime.UtcNow;
            conversation.LastMessageAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _usageStatsRecorder.RecordAsync(
                userId,
                botId,
                bot.LLMProviderPreset,
                usage,
                ct);

            // Generate embedding for assistant message
            if (bot.EmbeddingPreset is not null && queryEmbedding is not null)
            {
                try
                {
                    var embeddingProvider = _providerFactory.GetEmbeddingProvider(bot.EmbeddingPreset);
                    var assistantEmbedding = await embeddingProvider
                        .GenerateEmbeddingAsync(assistantMessage.Content, ct);

                    var assistantEmbeddingVector = EmbeddingVectorCompatibility.ToVectorOrNull(assistantEmbedding);
                    if (assistantEmbeddingVector is not null)
                    {
                        assistantMessage.Embedding = assistantEmbeddingVector;
                        await _db.SaveChangesAsync(ct);
                    }
                }
                catch
                {
                    // Non-fatal
                }
            }

            await Clients.Caller.SendAsync("ReceiveMessageComplete", assistantMessage.Id, ct);
        }
        finally
        {
            convLock.Release();
        }
    }

    private async Task<BotConversation?> ResolveConversationAsync(
        Guid botId,
        Guid userId,
        Guid? requestedConversationId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if (requestedConversationId.HasValue)
        {
            var requested = await _db.BotConversations.FirstOrDefaultAsync(c =>
                c.Id == requestedConversationId.Value
                && c.BotId == botId
                && c.UserId == userId, ct);

            if (requested is null) return null;

            await UpsertConversationStateAsync(botId, userId, requested.Id, now, ct);
            return requested;
        }

        var state = await _db.BotConversationStates.FirstOrDefaultAsync(
            s => s.BotId == botId && s.UserId == userId, ct);

        BotConversation? conversation = null;
        if (state is not null)
        {
            conversation = await _db.BotConversations.FirstOrDefaultAsync(c =>
                c.Id == state.CurrentConversationId
                && c.BotId == botId
                && c.UserId == userId, ct);
        }

        conversation ??= await _db.BotConversations
            .Where(c => c.BotId == botId && c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (conversation is null)
        {
            conversation = new BotConversation
            {
                Id = Guid.NewGuid(),
                BotId = botId,
                UserId = userId,
                Title = "New conversation",
                CreatedAt = now,
                UpdatedAt = now,
                LastMessageAt = now
            };
            _db.BotConversations.Add(conversation);
        }

        await UpsertConversationStateAsync(botId, userId, conversation.Id, now, ct);
        await _db.SaveChangesAsync(ct);

        return conversation;
    }

    private async Task UpsertConversationStateAsync(
        Guid botId,
        Guid userId,
        Guid conversationId,
        DateTime now,
        CancellationToken ct)
    {
        var state = await _db.BotConversationStates.FirstOrDefaultAsync(
            s => s.BotId == botId && s.UserId == userId, ct);

        if (state is null)
        {
            _db.BotConversationStates.Add(new BotConversationState
            {
                Id = Guid.NewGuid(),
                BotId = botId,
                UserId = userId,
                CurrentConversationId = conversationId,
                UpdatedAt = now
            });
        }
        else
        {
            state.CurrentConversationId = conversationId;
            state.UpdatedAt = now;
        }
    }

    private static string BuildConversationTitle(string content)
    {
        var normalized = content.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "New conversation";

        var firstLine = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        const int maxLen = 64;
        return firstLine.Length <= maxLen
            ? firstLine
            : $"{firstLine[..(maxLen - 1)].TrimEnd()}…";
    }

    private Guid GetUserId() =>
        Guid.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User!.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
}
