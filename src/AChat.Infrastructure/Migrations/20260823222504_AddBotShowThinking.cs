using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AChat.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBotShowThinking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowThinking",
                table: "Bots",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowThinking",
                table: "Bots");
        }
    }
}
