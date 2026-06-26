using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixMissingSchemaFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DecisionInbox_ProjectId_AgentName_Slug",
                table: "DecisionInbox");

            migrationBuilder.AddColumn<string>(
                name: "SerializedState",
                table: "SessionContexts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupersededById",
                table: "Decisions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DecisionId",
                table: "DecisionInbox",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "AgentMemory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_SupersededById",
                table: "Decisions",
                column: "SupersededById");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionInbox_DecisionId",
                table: "DecisionInbox",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionInbox_ProjectId_Slug",
                table: "DecisionInbox",
                columns: new[] { "ProjectId", "Slug" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_DecisionInbox_Decisions_DecisionId",
                table: "DecisionInbox",
                column: "DecisionId",
                principalTable: "Decisions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Decisions_Decisions_SupersededById",
                table: "Decisions",
                column: "SupersededById",
                principalTable: "Decisions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DecisionInbox_Decisions_DecisionId",
                table: "DecisionInbox");

            migrationBuilder.DropForeignKey(
                name: "FK_Decisions_Decisions_SupersededById",
                table: "Decisions");

            migrationBuilder.DropIndex(
                name: "IX_Decisions_SupersededById",
                table: "Decisions");

            migrationBuilder.DropIndex(
                name: "IX_DecisionInbox_DecisionId",
                table: "DecisionInbox");

            migrationBuilder.DropIndex(
                name: "IX_DecisionInbox_ProjectId_Slug",
                table: "DecisionInbox");

            migrationBuilder.DropColumn(
                name: "SerializedState",
                table: "SessionContexts");

            migrationBuilder.DropColumn(
                name: "SupersededById",
                table: "Decisions");

            migrationBuilder.DropColumn(
                name: "DecisionId",
                table: "DecisionInbox");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "AgentMemory");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionInbox_ProjectId_AgentName_Slug",
                table: "DecisionInbox",
                columns: new[] { "ProjectId", "AgentName", "Slug" },
                unique: true);
        }
    }
}
