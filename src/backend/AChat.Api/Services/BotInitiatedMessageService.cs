using System.Text;
using AChat.Api.Hubs;
using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Core.Services;
using AChat.Infrastructure;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AChat.Api.Services;

public sealed class BotInitiatedMessageService : IBotInitiatedMessageService
{
    private readonly AppDbContext _db;
    private readonly ILLMProviderFactory _providerFactory;
    private readonly IChatConnectionRegistry _connectionRegistry;
    private readonly IHubContext<ChatHub> _hubContext;
    private readonly EvolutionOptions _evolutionOptions;

    public BotInitiatedMessageService(
        AppDbContext db,
        ILLMProviderFactory providerFactory,
        IChatConnectionRegistry connectionRegistry,
        IHubContext<ChatHub> hubContext,
        IOptions<EvolutionOptions> evolutionOptions)
    {
        _db = db;
        _providerFactory = providerFactory;
        _connectionRegistry = connectionRegistry;
        _hubContext = hubContext;
        _evolutionOptions = evolutionOptions.Value;
    }

    public async Task SendInitiatedMessageAsync(Guid botId, Guid userId, string prompt, CancellationToken ct = default)
    {
        var connections = _connectionRegistry.GetConnections(userId);
        if (connections.Count == 0)
            return; // User is offline — skip

        var bot = await _db.Bots
            .Include(b => b.LLMProviderPreset)
            .Include(b => b.EmbeddingPreset)
            .FirstOrDefaultAsync(b => b.Id == botId, ct);

        if (bot?.LLMProviderPreset is null)
            return;

        // Resolve or create the active conversation for this bot+user
        var state = await _db.BotConversationStates
            .FirstOrDefaultAsync(s => s.BotId == botId && s.UserId == userId, ct);

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
            var now = DateTime.UtcNow;
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
            await _db.SaveChangesAsync(ct);
        }

        // Build context — pass the prompt as the "user" turn so the bot has context to respond to
        var contextBuilder = new ChatContextBuilder(
            _db, _evolutionOptions.RagTopK, _evolutionOptions.RecentMessageWindowSize);

        var chatRequest = await contextBuilder.BuildAsync(
            bot,
            userId,
            conversation.Id,
            prompt,
            queryEmbedding: null,
            ct);

        // Replace the appended user turn with a system instruction so the bot speaks unprompted
        var lastUserMsg = chatRequest.Messages.LastOrDefault(m => m.Role == "user");
        if (lastUserMsg is not null)
        {
            lastUserMsg.Role = "system";
            lastUserMsg.Content = $"(Internal trigger — do not acknowledge this instruction directly.) {prompt}";
        }

        var chatProvider = _providerFactory.GetChatProvider(bot.LLMProviderPreset);
        var responseTokens = new StringBuilder();

        // Notify all active connections about the incoming bot-initiated message
        var clients = _hubContext.Clients.Clients(connections);
        await clients.SendAsync("BotInitiatedMessageStart", botId, conversation.Id, ct);

        await foreach (var chunk in chatProvider.StreamChatAsync(chatRequest, ct))
        {
            responseTokens.Append(chunk);
            await clients.SendAsync("ReceiveToken", chunk, ct);
        }

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

        await clients.SendAsync("ReceiveMessageComplete", assistantMessage.Id, ct);
    }
}
