using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace AChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UseFlexibleEmbeddingDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Clear existing dimensioned embeddings before altering column type.
            // vector(1536) cannot be cast to the dimensionless vector type in-place.
            // Embeddings will be regenerated automatically on subsequent chat requests.
            migrationBuilder.Sql("UPDATE \"Messages\" SET \"Embedding\" = NULL WHERE \"Embedding\" IS NOT NULL;");

            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "Messages",
                type: "vector",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector(1536)",
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmbeddingDimension",
                table: "Messages",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmbeddingDimension",
                table: "Messages");

            migrationBuilder.AlterColumn<Vector>(
                name: "Embedding",
                table: "Messages",
                type: "vector(1536)",
                nullable: true,
                oldClrType: typeof(Vector),
                oldType: "vector",
                oldNullable: true);
        }
    }
}
