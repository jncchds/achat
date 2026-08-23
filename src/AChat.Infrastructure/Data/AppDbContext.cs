using AChat.Core.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AChat.Infrastructure.Data;

/// <summary>
/// Also hosts the Data Protection key ring (PersistKeysToDbContext, §7.3), so the
/// key material survives container rebuilds and travels with a database backup.
/// </summary>
public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<LlmPreset> LlmPresets => Set<LlmPreset>();
    public DbSet<Bot> Bots => Set<Bot>();
    public DbSet<BotDirective> BotDirectives => Set<BotDirective>();
    public DbSet<PersonaVersion> PersonaVersions => Set<PersonaVersion>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<ConversationSummary> ConversationSummaries => Set<ConversationSummary>();
    public DbSet<LlmInteraction> LlmInteractions => Set<LlmInteraction>();
    public DbSet<BotAccess> BotAccesses => Set<BotAccess>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();

    // Data Protection key ring (added by Microsoft.AspNetCore.DataProtection.EntityFrameworkCore).
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // pgvector extension (§6).
        modelBuilder.HasPostgresExtension("vector");

        // ── User ──────────────────────────────────────────────────────────────
        modelBuilder.Entity<User>(u =>
        {
            u.ToTable("Users");
            u.HasIndex(x => x.Username).IsUnique();
            u.Property(x => x.Username).HasMaxLength(64).IsRequired();
            u.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            u.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            u.Property(x => x.Timezone).HasMaxLength(64);
            u.Property(x => x.IsAdmin).HasDefaultValue(false);
        });

        // ── LlmPreset ─────────────────────────────────────────────────────────
        modelBuilder.Entity<LlmPreset>(p =>
        {
            p.ToTable("LlmPresets");
            p.HasIndex(x => x.OwnerId);
            p.Property(x => x.Name).HasMaxLength(100).IsRequired();
            p.Property(x => x.BaseEndpoint).HasMaxLength(500).IsRequired();
            p.Property(x => x.ApiKeyEncrypted).HasColumnType("bytea");
            p.Property(x => x.GenerationModel).HasMaxLength(200).IsRequired();
            p.Property(x => x.UtilityModel).HasMaxLength(200);
            p.Property(x => x.EmbeddingModel).HasMaxLength(200);
            p.Property(x => x.Temperature).HasDefaultValue(null);
            p.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Bot ───────────────────────────────────────────────────────────────
        modelBuilder.Entity<Bot>(b =>
        {
            b.ToTable("Bots");
            b.HasIndex(x => x.OwnerId);
            b.HasIndex(x => x.PresetId);
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.AvatarUrl).HasMaxLength(2000);
            b.Property(x => x.SpeechStyle).HasMaxLength(2000);
            b.Property(x => x.Greeting).HasMaxLength(2000);
            b.Property(x => x.InviteCode).HasMaxLength(64).IsRequired();
            b.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            // A bot uses exactly one preset, which must belong to the same owner (§2.2).
            b.HasOne(x => x.Preset).WithMany().HasForeignKey(x => x.PresetId).OnDelete(DeleteBehavior.Restrict);
            b.Property(x => x.EvolutionEnabled).HasDefaultValue(true);
        });

        // ── BotDirective: discrete, individually editable directives (§2.4) ────
        modelBuilder.Entity<BotDirective>(d =>
        {
            d.ToTable("BotDirectives");
            d.HasIndex(x => x.BotId);
            d.Property(x => x.Text).HasMaxLength(2000).IsRequired();
            d.Property(x => x.IsActive).HasDefaultValue(true);
            d.HasOne(x => x.Bot).WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── PersonaVersion: versioned evolved-traits layer (§2.4) ────────────
        modelBuilder.Entity<PersonaVersion>(v =>
        {
            v.ToTable("PersonaVersions");
            v.HasIndex(x => x.BotId);
            v.Property(x => x.Content).IsRequired();
            v.Property(x => x.Source).HasDefaultValue(PersonaVersionSource.Initial);
            v.HasOne(x => x.Bot).WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Conversation: one continuous timeline per (BotId, UserId) (§2.3) ──
        modelBuilder.Entity<Conversation>(c =>
        {
            c.ToTable("Conversations");
            c.HasIndex(x => new { x.BotId, x.UserId }).IsUnique();
            c.Property(x => x.LastMessageAt);
            c.Property(x => x.ExtractionUpToMessageId).HasDefaultValue(0);
            c.HasOne(x => x.Bot).WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Cascade);
            c.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Message ───────────────────────────────────────────────────────────
        modelBuilder.Entity<Message>(m =>
        {
            m.ToTable("Messages");
            m.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            m.Property(x => x.Content).IsRequired();
            m.Property(x => x.IsComplete).HasDefaultValue(true);
            m.HasOne(x => x.Conversation).WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Memory (§5.8) ─────────────────────────────────────────────────────
        modelBuilder.Entity<Memory>(mm =>
        {
            mm.ToTable("Memories");
            // Composite scope index narrows the vector scan (§5.1, §6).
            mm.HasIndex(x => new { x.BotId, x.UserId });
            mm.HasIndex(x => x.SupersededByMemoryId);
            mm.Property(x => x.Content).IsRequired();
            // Variable-length vector column — no dimension, so presets with different
            // embedding models/dimensions coexist (§6).
            mm.Property(x => x.Embedding).HasColumnType("vector");
            mm.Property(x => x.EmbeddingModel).HasMaxLength(200).IsRequired();
            mm.Property(x => x.Importance).HasDefaultValue(3);
            mm.Property(x => x.IsPinned).HasDefaultValue(false);
            mm.Property(x => x.IsDeleted).HasDefaultValue(false);
            mm.Property(x => x.RecallCount).HasDefaultValue(0);
        });

        // ── ConversationSummary: one row per (BotId, UserId) (§5.2) ───────────
        modelBuilder.Entity<ConversationSummary>(s =>
        {
            s.ToTable("ConversationSummaries");
            s.HasIndex(x => new { x.BotId, x.UserId }).IsUnique();
            s.Property(x => x.Content).IsRequired();
            s.Property(x => x.TokenCount).HasDefaultValue(0);
        });

        // ── LlmInteraction (§4.4) ─────────────────────────────────────────────
        modelBuilder.Entity<LlmInteraction>(i =>
        {
            i.ToTable("LlmInteractions");
            i.HasIndex(x => new { x.BotId, x.CreatedAt });
            i.HasIndex(x => x.UserId);
            i.HasIndex(x => x.PresetId);
            i.HasIndex(x => new { x.Purpose, x.CreatedAt });
            i.Property(x => x.Model).HasMaxLength(200).IsRequired();
            i.Property(x => x.Endpoint).HasMaxLength(500).IsRequired();
            i.Property(x => x.RequestBody).IsRequired();
            i.Property(x => x.FinishReason).HasMaxLength(50);
            i.Property(x => x.Failed).HasDefaultValue(false);
            i.HasOne(x => x.Bot).WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.SetNull);
            i.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            // The audit log outlives presets: deleting one nulls the reference
            // rather than blocking the deletion (§4.4).
            i.HasOne(x => x.Preset).WithMany().HasForeignKey(x => x.PresetId).OnDelete(DeleteBehavior.SetNull);
        });

        // ── BotAccess (§2.5) ──────────────────────────────────────────────────
        modelBuilder.Entity<BotAccess>(a =>
        {
            a.ToTable("BotAccesses");
            a.HasIndex(x => new { x.BotId, x.UserId }).IsUnique();
            a.HasOne(x => x.Bot).WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Cascade);
            a.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── AccessRequest (§2.5) ──────────────────────────────────────────────
        modelBuilder.Entity<AccessRequest>(r =>
        {
            r.ToTable("AccessRequests");
            r.HasIndex(x => new { x.BotId, x.RequesterId }).IsUnique();
            r.HasIndex(x => new { x.BotId, x.Status });
            r.Property(x => x.InviteCode).HasMaxLength(64);
            r.HasOne(x => x.Bot).WithMany().HasForeignKey(x => x.BotId).OnDelete(DeleteBehavior.Cascade);
            r.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
