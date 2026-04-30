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
    public DbSet<BotConversation> BotConversations => Set<BotConversation>();
    public DbSet<BotConversationState> BotConversationStates => Set<BotConversationState>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<BotMemorySummary> BotMemorySummaries => Set<BotMemorySummary>();
    public DbSet<BotPersonaSnapshot> BotPersonaSnapshots => Set<BotPersonaSnapshot>();
    public DbSet<BotAccessList> BotAccessLists => Set<BotAccessList>();
    public DbSet<BotAccessRequest> BotAccessRequests => Set<BotAccessRequest>();
    public DbSet<TelegramOutboundMessage> TelegramOutboundMessages => Set<TelegramOutboundMessage>();

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

        // BotConversation
        modelBuilder.Entity<BotConversation>(e =>
        {
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(c => c.CreatedAt).HasDefaultValueSql("now()");
            e.Property(c => c.UpdatedAt).HasDefaultValueSql("now()");
            e.Property(c => c.LastMessageAt).HasDefaultValueSql("now()");
            e.HasIndex(c => new { c.BotId, c.UserId, c.UpdatedAt });
            e.HasOne(c => c.Bot)
             .WithMany(b => b.Conversations)
             .HasForeignKey(c => c.BotId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.User)
             .WithMany(u => u.Conversations)
             .HasForeignKey(c => c.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // BotConversationState
        modelBuilder.Entity<BotConversationState>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
            e.HasIndex(s => new { s.BotId, s.UserId }).IsUnique();
            e.HasOne(s => s.Bot)
             .WithMany(b => b.ConversationStates)
             .HasForeignKey(s => s.BotId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.User)
             .WithMany(u => u.ConversationStates)
             .HasForeignKey(s => s.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.CurrentConversation)
             .WithMany()
             .HasForeignKey(s => s.CurrentConversationId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Message
        modelBuilder.Entity<Message>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
            e.Property(m => m.Embedding).HasColumnType("vector(1536)");
            e.HasIndex(m => new { m.BotId, m.UserId, m.ConversationId, m.CreatedAt });
            e.HasOne(m => m.Bot)
             .WithMany(b => b.Messages)
             .HasForeignKey(m => m.BotId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User)
             .WithMany()
             .HasForeignKey(m => m.UserId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Conversation)
             .WithMany(c => c.Messages)
             .HasForeignKey(m => m.ConversationId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // BotMemorySummary
        modelBuilder.Entity<BotMemorySummary>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.Property(s => s.Embedding).HasColumnType("vector(1536)");
                        e.HasIndex(s => new { s.BotId, s.UserId, s.ConversationId, s.CreatedAt });
            e.HasOne(s => s.Bot)
             .WithMany(b => b.MemorySummaries)
             .HasForeignKey(s => s.BotId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.User)
             .WithMany()
             .HasForeignKey(s => s.UserId)
             .OnDelete(DeleteBehavior.Cascade);
                        e.HasOne(s => s.Conversation)
                         .WithMany(c => c.MemorySummaries)
                         .HasForeignKey(s => s.ConversationId)
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

        // TelegramOutboundMessage
        modelBuilder.Entity<TelegramOutboundMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(m => m.Text).HasColumnType("text");
            e.Property(m => m.ParseMode).HasMaxLength(32);
            e.Property(m => m.ReplyMarkupJson).HasColumnType("text");
            e.Property(m => m.ChatAction).HasMaxLength(32);
            e.Property(m => m.CallbackQueryId).HasMaxLength(256);
            e.Property(m => m.LastError).HasColumnType("text");
            e.Property(m => m.AttemptCount).HasDefaultValue(0);
            e.Property(m => m.AvailableAt).HasDefaultValueSql("now()");
            e.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
            e.Property(m => m.UpdatedAt).HasDefaultValueSql("now()");
            e.HasIndex(m => new { m.AvailableAt, m.CreatedAt });
            e.HasIndex(m => m.BotId);
            e.HasOne(m => m.Bot)
             .WithMany()
             .HasForeignKey(m => m.BotId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
