using AChat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AChat.Infrastructure.Data.Configurations;

public class BotUserMemoryConfiguration : IEntityTypeConfiguration<BotUserMemory>
{
    public void Configure(EntityTypeBuilder<BotUserMemory> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedOnAdd();
        builder.Property(m => m.Facts)
            .HasColumnType("jsonb")
            .IsRequired();
        builder.Property(m => m.UpdatedAt).IsRequired();

        builder.HasOne(m => m.Bot)
            .WithMany(b => b.UserMemories)
            .HasForeignKey(m => m.BotId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(m => new { m.BotId, m.UserId }).IsUnique();
    }
}
