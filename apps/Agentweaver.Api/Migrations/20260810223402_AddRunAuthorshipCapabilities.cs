using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRunAuthorshipCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "run_authorship_capabilities",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    token_hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_authorship_capabilities", x => x.run_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_run_authorship_capabilities_expires_at",
                table: "run_authorship_capabilities",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_authorship_capabilities");
        }
    }
}
