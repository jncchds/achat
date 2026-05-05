using AChat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AChat.Infrastructure.Data.Configurations;

public class LlmInteractionConfiguration : IEntityTypeConfiguration<LlmInteraction>
{
    public void Configure(EntityTypeBuilder<LlmInteraction> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedOnAdd();
        builder.Property(i => i.Endpoint).IsRequired().HasMaxLength(500);
        builder.Property(i => i.ModelName).IsRequired().HasMaxLength(200);
        builder.Property(i => i.Metadata).HasColumnType("jsonb");
        builder.Property(i => i.CreatedAt).IsRequired();

        builder.HasOne(i => i.User)
            .WithMany(u => u.LlmInteractions)
            .HasForeignKey(i => i.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.Bot)
            .WithMany(b => b.LlmInteractions)
            .HasForeignKey(i => i.BotId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(i => i.Preset)
            .WithMany()
            .HasForeignKey(i => i.PresetId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(i => i.Conversation)
            .WithMany()
            .HasForeignKey(i => i.ConversationId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(i => new { i.UserId, i.CreatedAt });
        builder.HasIndex(i => new { i.BotId, i.CreatedAt });
    }
}
