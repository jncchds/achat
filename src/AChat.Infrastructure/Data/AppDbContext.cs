using AChat.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AChat.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<LlmPreset> LlmPresets => Set<LlmPreset>();
    public DbSet<Bot> Bots => Set<Bot>();
    public DbSet<BotUserMemory> BotUserMemories => Set<BotUserMemory>();
    public DbSet<BotAccessRequest> BotAccessRequests => Set<BotAccessRequest>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<LlmInteraction> LlmInteractions => Set<LlmInteraction>();
    public DbSet<BotEvolutionLog> BotEvolutionLogs => Set<BotEvolutionLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
