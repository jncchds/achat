using AChat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace AChat.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<LLMProviderPreset> LLMProviderPresets => Set<LLMProviderPreset>();
    public DbSet<Bot> Bots => Set<Bot>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<BotMemorySummary> BotMemorySummaries => Set<BotMemorySummary>();
    public DbSet<BotPersonaSnapshot> BotPersonaSnapshots => Set<BotPersonaSnapshot>();
    public DbSet<BotAccessList> BotAccessLists => Set<BotAccessList>();
    public DbSet<BotAccessRequest> BotAccessRequests => Set<BotAccessRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        // User
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Email).IsUnique().HasFilter("email IS NOT NULL");
            e.HasIndex(u => u.TelegramId).IsUnique().HasFilter("telegram_id IS NOT NULL");
            e.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(u => u.CreatedAt).HasDefaultValueSql("now()");
        });

        // LLMProviderPreset
        modelBuilder.Entity<LLMProviderPreset>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(p => p.CreatedAt).HasDefaultValueSql("now()");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(p => p.User)
             .WithMany(u => u.Presets)
             .HasForeignKey(p => p.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Bot
        modelBuilder.Entity<Bot>(e =>
        {
            e.HasKey(b => b.Id);
            e.Property(b => b.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(b => b.CreatedAt).HasDefaultValueSql("now()");
            e.Property(b => b.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne(b => b.Owner)
             .WithMany(u => u.Bots)
             .HasForeignKey(b => b.OwnerId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(b => b.LLMProviderPreset)
             .WithMany()
             .HasForeignKey(b => b.LLMProviderPresetId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(b => b.EmbeddingPreset)
             .WithMany()
             .HasForeignKey(b => b.EmbeddingPresetId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // Message
        modelBuilder.Entity<Message>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
            e.Property(m => m.Embedding).HasColumnType("vector(1536)");
            e.HasIndex(m => new { m.BotId, m.UserId, m.CreatedAt });
            e.HasOne(m => m.Bot)
             .WithMany(b => b.Messages)
             .HasForeignKey(m => m.BotId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User)
             .WithMany()
             .HasForeignKey(m => m.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // BotMemorySummary
        modelBuilder.Entity<BotMemorySummary>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.Property(s => s.Embedding).HasColumnType("vector(1536)");
            e.HasOne(s => s.Bot)
             .WithMany(b => b.MemorySummaries)
             .HasForeignKey(s => s.BotId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.User)
             .WithMany()
             .HasForeignKey(s => s.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // BotPersonaSnapshot
        modelBuilder.Entity<BotPersonaSnapshot>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(s => s.Bot)
             .WithMany(b => b.PersonaSnapshots)
             .HasForeignKey(s => s.BotId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // BotAccessList
        modelBuilder.Entity<BotAccessList>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(a => a.AddedAt).HasDefaultValueSql("now()");
            e.HasIndex(a => new { a.BotId, a.SubjectType, a.SubjectId }).IsUnique();
            e.HasOne(a => a.Bot)
             .WithMany(b => b.AccessList)
             .HasForeignKey(a => a.BotId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // BotAccessRequest
        modelBuilder.Entity<BotAccessRequest>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(r => r.RequestedAt).HasDefaultValueSql("now()");
            e.HasOne(r => r.Bot)
             .WithMany(b => b.AccessRequests)
             .HasForeignKey(r => r.BotId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.ResolvedByUser)
             .WithMany()
             .HasForeignKey(r => r.ResolvedByUserId)
             .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
