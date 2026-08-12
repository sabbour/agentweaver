using Agentweaver.Api.Memory;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations;

[DbContext(typeof(MemoryDbContext))]
[Migration("20260810223500_AddRunAuthorshipCapabilities")]
public partial class AddRunAuthorshipCapabilities : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "run_authorship_capabilities",
            columns: table => new
            {
                run_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
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

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "run_authorship_capabilities");
    }
}
