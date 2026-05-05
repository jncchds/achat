using AChat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AChat.Infrastructure.Data.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedOnAdd();
        builder.Property(m => m.Role).HasConversion<string>();
        builder.Property(m => m.Content).IsRequired();
        builder.Property(m => m.CreatedAt).IsRequired();

        builder.HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Dimensionless vector column: supports any embedding model regardless of output dimension.
        // EmbeddingDimension column records the actual dimension so cosine distance queries
        // can filter to matching-dimension vectors and avoid cross-dimension comparison errors.
        builder.Property(m => m.Embedding).HasColumnType("vector");

        builder.HasIndex(m => new { m.ConversationId, m.CreatedAt });
    }
}
