using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SetInteractionPresetDeleteBehavior : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LlmInteractions_LlmPresets_PresetId",
                table: "LlmInteractions");

            migrationBuilder.AddForeignKey(
                name: "FK_LlmInteractions_LlmPresets_PresetId",
                table: "LlmInteractions",
                column: "PresetId",
                principalTable: "LlmPresets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LlmInteractions_LlmPresets_PresetId",
                table: "LlmInteractions");

            migrationBuilder.AddForeignKey(
                name: "FK_LlmInteractions_LlmPresets_PresetId",
                table: "LlmInteractions",
                column: "PresetId",
                principalTable: "LlmPresets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
