using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBotAccessRequestLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BotAccessRequests_BotId",
                table: "BotAccessRequests");

            migrationBuilder.CreateIndex(
                name: "IX_BotAccessRequests_BotId_SubjectType_SubjectId_Status",
                table: "BotAccessRequests",
                columns: new[] { "BotId", "SubjectType", "SubjectId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BotAccessRequests_BotId_SubjectType_SubjectId_Status",
                table: "BotAccessRequests");

            migrationBuilder.CreateIndex(
                name: "IX_BotAccessRequests_BotId",
                table: "BotAccessRequests",
                column: "BotId");
        }
    }
}
