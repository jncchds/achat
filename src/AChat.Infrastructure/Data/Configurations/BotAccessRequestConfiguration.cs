using AChat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AChat.Infrastructure.Data.Configurations;

public class BotAccessRequestConfiguration : IEntityTypeConfiguration<BotAccessRequest>
{
    public void Configure(EntityTypeBuilder<BotAccessRequest> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();
        builder.Property(r => r.Status).HasConversion<string>();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        builder.HasOne(r => r.Bot)
            .WithMany(b => b.AccessRequests)
            .HasForeignKey(r => r.BotId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.Requester)
            .WithMany(u => u.AccessRequests)
            .HasForeignKey(r => r.RequesterId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.BotId, r.RequesterId }).IsUnique();
    }
}
