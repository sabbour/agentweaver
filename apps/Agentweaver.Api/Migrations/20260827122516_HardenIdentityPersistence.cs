using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class HardenIdentityPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __identity_delivery_uniqueness_guard (
                    value INTEGER NOT NULL CHECK (value = 1)
                );
                INSERT INTO __identity_delivery_uniqueness_guard (value)
                SELECT 0
                WHERE EXISTS (
                    SELECT delivery_id
                    FROM automation_invocations
                    WHERE delivery_id IS NOT NULL
                    GROUP BY delivery_id
                    HAVING COUNT(*) > 1
                );
                DROP TABLE __identity_delivery_uniqueness_guard;
                """);

            migrationBuilder.DropIndex(
                name: "IX_automation_invocations_delivery_id_event_name",
                table: "automation_invocations");

            migrationBuilder.AddColumn<string>(
                name: "external_transaction_id",
                table: "github_authorizations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE github_authorizations
                SET external_transaction_id = lower(hex(randomblob(32)))
                WHERE external_transaction_id IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "external_transaction_id",
                table: "github_authorizations",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "github_lifecycle_deliveries",
                columns: table => new
                {
                    delivery_id = table.Column<string>(type: "TEXT", nullable: false),
                    event_name = table.Column<string>(type: "TEXT", nullable: false),
                    installation_id = table.Column<long>(type: "INTEGER", nullable: true),
                    repository_id = table.Column<long>(type: "INTEGER", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_lifecycle_deliveries", x => x.delivery_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_github_authorizations_external_transaction_id",
                table: "github_authorizations",
                column: "external_transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_automation_invocations_delivery_id",
                table: "automation_invocations",
                column: "delivery_id",
                unique: true,
                filter: "delivery_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Identity persistence schema changes are forward-only.");
        }
    }
}
