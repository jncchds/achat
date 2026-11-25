using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationMoodAndInteractionNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "LlmInteractions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mood",
                table: "Conversations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MoodChangedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MoodChangedAtMessageId",
                table: "Conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Notes",
                table: "LlmInteractions");

            migrationBuilder.DropColumn(
                name: "Mood",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "MoodChangedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "MoodChangedAtMessageId",
                table: "Conversations");
        }
    }
}
