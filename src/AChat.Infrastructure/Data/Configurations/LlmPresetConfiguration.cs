using AChat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AChat.Infrastructure.Data.Configurations;

public class LlmPresetConfiguration : IEntityTypeConfiguration<LlmPreset>
{
    public void Configure(EntityTypeBuilder<LlmPreset> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.Property(p => p.Name).IsRequired().HasMaxLength(200);
        builder.Property(p => p.ProviderType).HasConversion<string>();
        builder.Property(p => p.ProviderUrl).IsRequired().HasMaxLength(500);
        builder.Property(p => p.ApiToken).HasMaxLength(500);
        builder.Property(p => p.GenerationModel).IsRequired().HasMaxLength(200);
        builder.Property(p => p.EmbeddingModel).HasMaxLength(200);
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        builder.HasOne(p => p.User)
            .WithMany(u => u.Presets)
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => new { p.UserId, p.Name }).IsUnique();
    }
}
