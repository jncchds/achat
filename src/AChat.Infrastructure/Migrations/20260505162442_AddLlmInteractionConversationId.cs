using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmInteractionConversationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "LlmInteractions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlmInteractions_ConversationId",
                table: "LlmInteractions",
                column: "ConversationId");

            migrationBuilder.AddForeignKey(
                name: "FK_LlmInteractions_Conversations_ConversationId",
                table: "LlmInteractions",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LlmInteractions_Conversations_ConversationId",
                table: "LlmInteractions");

            migrationBuilder.DropIndex(
                name: "IX_LlmInteractions_ConversationId",
                table: "LlmInteractions");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "LlmInteractions");
        }
    }
}
