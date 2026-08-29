using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceCapabilityClaimLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claim_lease_expires_at",
                table: "marketplace_copilot_capabilities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE marketplace_copilot_capabilities
                SET claim_lease_expires_at = expires_at
                WHERE consumed_at IS NOT NULL
                  AND claim_lease_expires_at IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "claim_lease_expires_at",
                table: "marketplace_copilot_capabilities");
        }
    }
}
