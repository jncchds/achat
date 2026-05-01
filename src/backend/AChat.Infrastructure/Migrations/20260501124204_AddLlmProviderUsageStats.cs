using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmProviderUsageStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LLMProviderUsageStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BotId = table.Column<Guid>(type: "uuid", nullable: true),
                    LLMProviderPresetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    ProviderUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PromptModel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    TotalTokens = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LLMProviderUsageStats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LLMProviderUsageStats_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LLMProviderUsageStats_LLMProviderPresets_LLMProviderPresetId",
                        column: x => x.LLMProviderPresetId,
                        principalTable: "LLMProviderPresets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_LLMProviderUsageStats_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LLMProviderUsageStats_BotId",
                table: "LLMProviderUsageStats",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_LLMProviderUsageStats_LLMProviderPresetId",
                table: "LLMProviderUsageStats",
                column: "LLMProviderPresetId");

            migrationBuilder.CreateIndex(
                name: "IX_LLMProviderUsageStats_UserId_CreatedAt",
                table: "LLMProviderUsageStats",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LLMProviderUsageStats");
        }
    }
}
