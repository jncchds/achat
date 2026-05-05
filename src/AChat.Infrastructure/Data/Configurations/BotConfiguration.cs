using AChat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AChat.Infrastructure.Data.Configurations;

public class BotConfiguration : IEntityTypeConfiguration<Bot>
{
    public void Configure(EntityTypeBuilder<Bot> builder)
    {
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedOnAdd();
        builder.Property(b => b.Name).IsRequired().HasMaxLength(200);
        builder.Property(b => b.Personality).IsRequired();
        builder.Property(b => b.TelegramToken).HasMaxLength(200);
        builder.Property(b => b.UnknownUserReply).IsRequired().HasMaxLength(1000);
        builder.Property(b => b.CreatedAt).IsRequired();
        builder.Property(b => b.UpdatedAt).IsRequired();

        builder.HasOne(b => b.Owner)
            .WithMany(u => u.OwnedBots)
            .HasForeignKey(b => b.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(b => b.Preset)
            .WithMany(p => p.Bots)
            .HasForeignKey(b => b.PresetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
