using AChat.Core.Services;
using AChat.Infrastructure;
using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AChat.Api.Workers;

public class PersonaEvolutionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PersonaEvolutionWorker> _logger;
    private readonly EvolutionOptions _opts;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    public PersonaEvolutionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PersonaEvolutionWorker> logger,
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
                _logger.LogError(ex, "Error in PersonaEvolutionWorker.");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<ILLMProviderFactory>();
        var initiatedMessageService = scope.ServiceProvider.GetRequiredService<IBotInitiatedMessageService>();

        // Find bots with enough new messages since the last persona snapshot
        var bots = await db.Bots
            .Include(b => b.LLMProviderPreset)
            .Where(b => b.LLMProviderPresetId != null)
            .ToListAsync(ct);

        foreach (var bot in bots)
        {
            try
            {
                var evolved = await MaybeEvolveAsync(db, factory, bot, ct);
                if (evolved && _opts.BotInitiatesAfterEvolution)
                {
                    try
                    {
                        await initiatedMessageService.SendInitiatedMessageAsync(
                            bot.Id, bot.OwnerId, _opts.BotInitiationPrompt, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Bot-initiated message failed for bot {BotId} after evolution.", bot.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed persona evolution for bot {BotId}.", bot.Id);
            }
        }
    }

    private async Task<bool> MaybeEvolveAsync(
        AppDbContext db,
        ILLMProviderFactory factory,
        Bot bot,
        CancellationToken ct)
    {
        if (bot.LLMProviderPreset is null) return false;

        // Resolve the owner's internal user record
        var ownerUser = await db.Users.FirstOrDefaultAsync(u => u.Id == bot.OwnerId, ct);
        if (ownerUser is null) return false;

        // Get the CreatedAt of the latest snapshot, if any
        var lastSnapshot = await db.BotPersonaSnapshots
            .Where(s => s.BotId == bot.Id)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => (DateTime?)s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // Count owner messages since last evolution (only owner drives drift)
        var query = db.Messages
            .Where(m => m.BotId == bot.Id && m.UserId == ownerUser.Id && m.Role == MessageRole.User);
        if (lastSnapshot.HasValue)
            query = query.Where(m => m.CreatedAt > lastSnapshot.Value);

        var newMessageCount = await query.CountAsync(ct);
        if (newMessageCount < _opts.PersonaEvolutionMessageInterval) return false;

        // Gather recent owner messages (both sides of the conversation, but owner-scoped)
        var recentMessages = await db.Messages
            .Where(m => m.BotId == bot.Id && m.UserId == ownerUser.Id && m.Role != MessageRole.System)
            .OrderByDescending(m => m.CreatedAt)
            .Take(_opts.RecentMessageWindowSize)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var conversation = string.Join("\n", recentMessages
            .Select(m => $"{(m.Role == MessageRole.User ? "Owner" : "Bot")}: {m.Content}"));

        // Build push clause if active
        var pushClause = string.Empty;
        if (!string.IsNullOrWhiteSpace(bot.PersonaPushText))
            pushClause = $"\n\nThe bot owner has requested that the bot lean toward the following direction (apply this meaningfully, not literally): {bot.PersonaPushText}";

        var evolveRequest = new LLMChatRequest
        {
            SystemPrompt = "You are a persona writer for a self-evolving chatbot. " +
                           "Your task is to update the bot's personality description based on its recent interactions with its owner. " +
                           "Rules:\n" +
                           "- The core character traits described in [Character Foundation] are stable ground — preserve their spirit.\n" +
                           "- Drift the bot's style, vocabulary, recurring topics, and emotional tone naturally toward the patterns observed in the owner's messages.\n" +
                           "- Do NOT make the bot blindly mimic the owner. The bot must keep its own voice.\n" +
                           "- Do NOT add traits that contradict the foundation without strong evidence in the conversations.\n" +
                           "- Return only the updated persona description (2–4 paragraphs). No headings, no meta-commentary.",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = $"[Character Foundation]\n{bot.CharacterDescription}\n\n" +
                              $"[Current Evolved Persona]\n{bot.EvolvingPersonaPrompt}\n\n" +
                              $"[Recent Owner Conversations]\n{conversation}" +
                              pushClause +
                              "\n\nWrite the updated persona."
                }
            ]
        };

        var chatProvider = factory.GetChatProvider(bot.LLMProviderPreset);
        var newPersona = await chatProvider.GenerateChatAsync(evolveRequest, ct);

        // Save snapshot of old persona
        db.BotPersonaSnapshots.Add(new BotPersonaSnapshot
        {
            Id = Guid.NewGuid(),
            BotId = bot.Id,
            SnapshotText = bot.EvolvingPersonaPrompt,
            CreatedAt = DateTime.UtcNow
        });

        // Update bot's evolving persona
        bot.EvolvingPersonaPrompt = newPersona;
        bot.UpdatedAt = DateTime.UtcNow;

        // Decay the persona push
        if (bot.PersonaPushRemainingCycles > 0)
        {
            bot.PersonaPushRemainingCycles--;
            if (bot.PersonaPushRemainingCycles == 0)
                bot.PersonaPushText = null;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Evolved persona for bot {BotId} ({BotName}).", bot.Id, bot.Name);

        return true;
    }
}
