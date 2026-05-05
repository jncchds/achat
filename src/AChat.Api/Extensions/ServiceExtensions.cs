using AChat.Core.Interfaces.Services;
using AChat.Core.Options;
using AChat.Infrastructure.BackgroundJobs;
using AChat.Infrastructure.Data;
using AChat.Infrastructure.LLM;
using AChat.Infrastructure.Services;
using AChat.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;

namespace AChat.Api.Extensions;

public static class ServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsql => npgsql.UseVector()));

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IPresetService, PresetService>();
        services.AddScoped<BotService>();
        services.AddScoped<IBotService>(sp => sp.GetRequiredService<BotService>());
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ILlmUsageService, LlmUsageService>();
        services.AddScoped<IModelListService, ModelListService>();
        services.AddSingleton<IConversationNotifier, ConversationNotifier>();

        services.Configure<ChatOptions>(configuration.GetSection(ChatOptions.Section));

        var telegramOptions = configuration.GetSection(TelegramOptions.Section).Get<TelegramOptions>() ?? new();
        services.AddSingleton(new TelegramRateLimiter(
            telegramOptions.GlobalRateLimitPerMinute,
            telegramOptions.PerBotRateLimitPerMinute));

        services.AddHostedService<TelegramHostedService>();
        services.AddHostedService<PersonalityEvolutionJob>();

        return services;
    }

    public static async Task ApplyMigrationsAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    public static async Task SeedInitialUserAsync(this IServiceProvider services, IConfiguration configuration)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var opts = configuration.GetSection(InitialUserOptions.Section).Get<InitialUserOptions>() ?? new();

        var existing = await db.Users.FirstOrDefaultAsync(u => u.Username == opts.Username);
        if (existing is null)
        {
            db.Users.Add(new Core.Entities.User
            {
                Username = opts.Username,
                Email = string.Empty,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(opts.Password),
                Role = Core.Enums.UserRole.Admin,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        else if (opts.ForceChange)
        {
            existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(opts.Password);
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }
}
