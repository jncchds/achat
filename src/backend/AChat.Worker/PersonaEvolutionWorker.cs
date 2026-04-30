using AChat.Infrastructure;
using AChat.Core.Entities;
using AChat.Core.LLM;
using AChat.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AChat.Worker;

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

        // Find bots with enough new messages since the last persona snapshot
        var bots = await db.Bots
            .Include(b => b.LLMProviderPreset)
            .Where(b => b.LLMProviderPresetId != null)
            .ToListAsync(ct);

        foreach (var bot in bots)
        {
            try
            {
                await MaybeEvolveAsync(db, factory, bot, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed persona evolution for bot {BotId}.", bot.Id);
            }
        }
    }

    private async Task MaybeEvolveAsync(
        AppDbContext db,
        ILLMProviderFactory factory,
        Bot bot,
        CancellationToken ct)
    {
        if (bot.LLMProviderPreset is null) return;

        // Get the CreatedAt of the latest snapshot, if any
        var lastSnapshot = await db.BotPersonaSnapshots
            .Where(s => s.BotId == bot.Id)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => (DateTime?)s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // Count messages since last evolution
        var query = db.Messages.Where(m => m.BotId == bot.Id);
        if (lastSnapshot.HasValue)
            query = query.Where(m => m.CreatedAt > lastSnapshot.Value);

        var newMessageCount = await query.CountAsync(ct);
        if (newMessageCount < _opts.PersonaEvolutionMessageInterval) return;

        // Gather recent messages for analysis
        var recentMessages = await db.Messages
            .Where(m => m.BotId == bot.Id && m.Role != MessageRole.System)
            .OrderByDescending(m => m.CreatedAt)
            .Take(_opts.RecentMessageWindowSize)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var conversation = string.Join("\n", recentMessages
            .Select(m => $"{(m.Role == MessageRole.User ? "User" : "Bot")}: {m.Content}"));

        var evolveRequest = new LLMChatRequest
        {
            SystemPrompt = "You are a persona writer. Based on recent conversations, " +
                           "update and refine the chatbot's personality description. " +
                           "Return only the updated persona description. Keep it concise (2-4 paragraphs).",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = $"Current persona:\n{bot.EvolvingPersonaPrompt}\n\n" +
                              $"Recent conversations:\n{conversation}\n\n" +
                              "Write an updated persona that reflects the patterns in these conversations."
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

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Evolved persona for bot {BotId} ({BotName}).", bot.Id, bot.Name);
    }
}
