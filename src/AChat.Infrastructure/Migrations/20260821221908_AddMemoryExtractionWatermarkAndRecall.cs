using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryExtractionWatermarkAndRecall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecalledMemoryIds",
                table: "LlmInteractions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExtractionUpToMessageId",
                table: "Conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecalledMemoryIds",
                table: "LlmInteractions");

            migrationBuilder.DropColumn(
                name: "ExtractionUpToMessageId",
                table: "Conversations");
        }
    }
}
