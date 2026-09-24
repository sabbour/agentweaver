using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryDecisionIdentityKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdentityKey",
                table: "Decisions",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdentityKey",
                table: "AgentMemory",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_IdentityKey",
                table: "Decisions",
                column: "IdentityKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemory_IdentityKey",
                table: "AgentMemory",
                column: "IdentityKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Decisions_IdentityKey",
                table: "Decisions");

            migrationBuilder.DropIndex(
                name: "IX_AgentMemory_IdentityKey",
                table: "AgentMemory");

            migrationBuilder.DropColumn(
                name: "IdentityKey",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "IdentityKey",
                table: "AgentMemory");
        }
    }
}
