using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddRunEventIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventIdentity",
                table: "RunEvents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunEvents_RunId_EventIdentity",
                table: "RunEvents",
                columns: new[] { "RunId", "EventIdentity" },
                unique: true,
                filter: "\"EventIdentity\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RunEvents_RunId_EventIdentity",
                table: "RunEvents");

            migrationBuilder.DropColumn(
                name: "EventIdentity",
                table: "RunEvents");
        }
    }
}
