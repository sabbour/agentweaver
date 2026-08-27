using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class HardenIdentityPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT delivery_id
                        FROM automation_invocations
                        WHERE delivery_id IS NOT NULL
                        GROUP BY delivery_id
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Cannot harden automation delivery replay: duplicate X-GitHub-Delivery values exist.';
                    END IF;
                END
                $$;

                ALTER TABLE automation_invocations
                    DROP CONSTRAINT automation_invocations_delivery_id_event_name_key;
                """);

            migrationBuilder.AddColumn<string>(
                name: "external_transaction_id",
                table: "github_authorizations",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE github_authorizations
                SET external_transaction_id = md5(state || random()::text || clock_timestamp()::text)
                WHERE external_transaction_id IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "external_transaction_id",
                table: "github_authorizations",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "github_lifecycle_deliveries",
                columns: table => new
                {
                    delivery_id = table.Column<string>(type: "text", nullable: false),
                    event_name = table.Column<string>(type: "text", nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: true),
                    repository_id = table.Column<long>(type: "bigint", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
