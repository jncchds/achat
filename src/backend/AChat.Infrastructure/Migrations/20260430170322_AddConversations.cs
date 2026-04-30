using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_BotId_UserId_CreatedAt",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_BotMemorySummaries_BotId",
                table: "BotMemorySummaries");

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "Messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "BotMemorySummaries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "BotConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BotConversations_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BotConversations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BotConversationStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    BotId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotConversationStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BotConversationStates_BotConversations_CurrentConversationId",
                        column: x => x.CurrentConversationId,
                        principalTable: "BotConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BotConversationStates_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BotConversationStates_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO "BotConversations" ("Id", "BotId", "UserId", "Title", "CreatedAt", "UpdatedAt", "LastMessageAt")
                SELECT
                    gen_random_uuid(),
                    m."BotId",
                    m."UserId",
                    LEFT(REGEXP_REPLACE(COALESCE(MAX(m."Content"), 'Imported conversation'), E'[\\r\\n]+', ' ', 'g'), 64),
                    now(),
                    now(),
                    COALESCE(MAX(m."CreatedAt"), now())
                FROM "Messages" m
                GROUP BY m."BotId", m."UserId";
                """);

            migrationBuilder.Sql("""
                INSERT INTO "BotConversations" ("Id", "BotId", "UserId", "Title", "CreatedAt", "UpdatedAt", "LastMessageAt")
                SELECT
                    gen_random_uuid(),
                    s."BotId",
                    s."UserId",
                    'Imported conversation',
                    now(),
                    now(),
                    now()
                FROM (
                    SELECT DISTINCT "BotId", "UserId"
                    FROM "BotMemorySummaries"
                ) s
                LEFT JOIN "BotConversations" c
                    ON c."BotId" = s."BotId" AND c."UserId" = s."UserId"
                WHERE c."Id" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "Messages" m
                SET "ConversationId" = c."Id"
                FROM "BotConversations" c
                WHERE m."ConversationId" = '00000000-0000-0000-0000-000000000000'
                  AND c."BotId" = m."BotId"
                  AND c."UserId" = m."UserId";
                """);

            migrationBuilder.Sql("""
                UPDATE "BotMemorySummaries" s
                SET "ConversationId" = c."Id"
                FROM "BotConversations" c
                WHERE s."ConversationId" = '00000000-0000-0000-0000-000000000000'
                  AND c."BotId" = s."BotId"
                  AND c."UserId" = s."UserId";
                """);

            migrationBuilder.Sql("""
                INSERT INTO "BotConversationStates" ("Id", "BotId", "UserId", "CurrentConversationId", "UpdatedAt")
                SELECT gen_random_uuid(), c."BotId", c."UserId", c."Id", now()
                FROM (
                    SELECT DISTINCT ON ("BotId", "UserId")
                        "Id", "BotId", "UserId", "UpdatedAt"
                    FROM "BotConversations"
                    ORDER BY "BotId", "UserId", "UpdatedAt" DESC
                ) c;
                """);

            migrationBuilder.Sql("ALTER TABLE \"Messages\" ALTER COLUMN \"ConversationId\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE \"BotMemorySummaries\" ALTER COLUMN \"ConversationId\" DROP DEFAULT;");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_BotId_UserId_ConversationId_CreatedAt",
                table: "Messages",
                columns: new[] { "BotId", "UserId", "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_BotMemorySummaries_BotId_UserId_ConversationId_CreatedAt",
                table: "BotMemorySummaries",
                columns: new[] { "BotId", "UserId", "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BotMemorySummaries_ConversationId",
                table: "BotMemorySummaries",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_BotConversations_BotId_UserId_UpdatedAt",
                table: "BotConversations",
                columns: new[] { "BotId", "UserId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BotConversations_UserId",
                table: "BotConversations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BotConversationStates_BotId_UserId",
                table: "BotConversationStates",
                columns: new[] { "BotId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotConversationStates_CurrentConversationId",
                table: "BotConversationStates",
                column: "CurrentConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_BotConversationStates_UserId",
                table: "BotConversationStates",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_BotMemorySummaries_BotConversations_ConversationId",
                table: "BotMemorySummaries",
                column: "ConversationId",
                principalTable: "BotConversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_BotConversations_ConversationId",
                table: "Messages",
                column: "ConversationId",
                principalTable: "BotConversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BotMemorySummaries_BotConversations_ConversationId",
                table: "BotMemorySummaries");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_BotConversations_ConversationId",
                table: "Messages");

            migrationBuilder.DropTable(
                name: "BotConversationStates");

            migrationBuilder.DropTable(
                name: "BotConversations");

            migrationBuilder.DropIndex(
                name: "IX_Messages_BotId_UserId_ConversationId_CreatedAt",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_BotMemorySummaries_BotId_UserId_ConversationId_CreatedAt",
                table: "BotMemorySummaries");

            migrationBuilder.DropIndex(
                name: "IX_BotMemorySummaries_ConversationId",
                table: "BotMemorySummaries");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "BotMemorySummaries");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_BotId_UserId_CreatedAt",
                table: "Messages",
                columns: new[] { "BotId", "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BotMemorySummaries_BotId",
                table: "BotMemorySummaries",
                column: "BotId");
        }
    }
}
