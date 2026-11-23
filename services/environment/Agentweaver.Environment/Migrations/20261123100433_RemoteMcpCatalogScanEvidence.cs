using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Environment.Migrations
{
    /// <inheritdoc />
    public partial class RemoteMcpCatalogScanEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_environment_remote_mcp_connections_revisions",
                schema: "environment",
                table: "remote_mcp_connections");

            migrationBuilder.DropCheckConstraint(
                name: "ck_environment_remote_mcp_connection_idempotency_operation",
                schema: "environment",
                table: "remote_mcp_connection_idempotency");

            migrationBuilder.AddColumn<long>(
                name: "accepted_configuration_revision",
                schema: "environment",
                table: "remote_mcp_connections",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "accepted_discovery_revision",
                schema: "environment",
                table: "remote_mcp_connections",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scan_evidence_json",
                schema: "environment",
                table: "remote_mcp_catalog_snapshots",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_environment_remote_mcp_connections_revisions",
                schema: "environment",
                table: "remote_mcp_connections",
                sql: "row_revision > 0 AND current_configuration_revision > 0 AND (current_discovery_revision IS NULL OR current_discovery_revision > 0) AND ((accepted_configuration_revision IS NULL AND accepted_discovery_revision IS NULL) OR (accepted_configuration_revision > 0 AND accepted_discovery_revision > 0))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_environment_remote_mcp_connection_idempotency_operation",
                schema: "environment",
                table: "remote_mcp_connection_idempotency",
                sql: "operation IN ('Create', 'Update', 'Enable', 'Disable', 'Remove', 'SaveDiscovery', 'AcceptDiscovery')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_environment_remote_mcp_catalog_snapshots_scan_evidence",
                schema: "environment",
                table: "remote_mcp_catalog_snapshots",
                sql: "scan_evidence_json IS NULL OR jsonb_typeof(scan_evidence_json) = 'object'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_environment_remote_mcp_connections_revisions",
                schema: "environment",
                table: "remote_mcp_connections");

            migrationBuilder.DropCheckConstraint(
                name: "ck_environment_remote_mcp_connection_idempotency_operation",
                schema: "environment",
                table: "remote_mcp_connection_idempotency");

            migrationBuilder.DropCheckConstraint(
                name: "ck_environment_remote_mcp_catalog_snapshots_scan_evidence",
                schema: "environment",
                table: "remote_mcp_catalog_snapshots");

            migrationBuilder.DropColumn(
                name: "accepted_configuration_revision",
                schema: "environment",
                table: "remote_mcp_connections");

            migrationBuilder.DropColumn(
                name: "accepted_discovery_revision",
                schema: "environment",
                table: "remote_mcp_connections");

            migrationBuilder.DropColumn(
                name: "scan_evidence_json",
                schema: "environment",
                table: "remote_mcp_catalog_snapshots");

            migrationBuilder.AddCheckConstraint(
                name: "ck_environment_remote_mcp_connections_revisions",
                schema: "environment",
                table: "remote_mcp_connections",
                sql: "row_revision > 0 AND current_configuration_revision > 0 AND (current_discovery_revision IS NULL OR current_discovery_revision > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_environment_remote_mcp_connection_idempotency_operation",
                schema: "environment",
                table: "remote_mcp_connection_idempotency",
                sql: "operation IN ('Create', 'Update', 'Enable', 'Disable', 'Remove', 'SaveDiscovery')");
        }
    }
}
