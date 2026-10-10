using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentweaver.Environment.Migrations;

public partial class AddRemoteMcpConnections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "remote_mcp_connections",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                row_revision = table.Column<long>(type: "bigint", nullable: false),
                current_configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                current_discovery_revision = table.Column<long>(type: "bigint", nullable: true),
                state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "pk_environment_remote_mcp_connections",
                    row => new { row.tenant_id, row.project_id, row.connection_id });
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connections_revisions",
                    "row_revision > 0 AND current_configuration_revision > 0 AND (current_discovery_revision IS NULL OR current_discovery_revision > 0)");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connections_state",
                    "state IN ('Draft', 'Enabled', 'Disabled', 'Removed')");
            });

        migrationBuilder.CreateTable(
            name: "remote_mcp_connection_configurations",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                endpoint_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                resource_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                authentication_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                identity_binding_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                transport_profile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                registry_server_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                registry_exact_version = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                registry_metadata_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                configuration_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "pk_environment_remote_mcp_connection_configurations",
                    row => new { row.tenant_id, row.project_id, row.connection_id, row.configuration_revision });
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_revision",
                    "configuration_revision > 0");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_authentication",
                    "authentication_mode IN ('None', 'DelegatedOAuth')");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_transport",
                    "transport_profile = 'StreamableHttp20250618'");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_registry_pin",
                    "(registry_server_name IS NULL AND registry_exact_version IS NULL AND registry_metadata_sha256 IS NULL) OR (registry_server_name IS NOT NULL AND registry_exact_version IS NOT NULL AND registry_exact_version <> 'latest' AND registry_metadata_sha256 ~ '^[0-9a-f]{64}$')");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_oauth_resource",
                    "authentication_mode <> 'DelegatedOAuth' OR resource_uri IS NOT NULL");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connection_configurations_digests",
                    "configuration_sha256 ~ '^[0-9a-f]{64}$'");
                table.ForeignKey(
                    "fk_environment_remote_mcp_configurations_connection",
                    row => new { row.tenant_id, row.project_id, row.connection_id },
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "remote_mcp_connections",
                    principalColumns: ["tenant_id", "project_id", "connection_id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "remote_mcp_catalog_snapshots",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                discovery_revision = table.Column<long>(type: "bigint", nullable: false),
                catalog_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                registry_metadata_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                tools_json = table.Column<string>(type: "jsonb", nullable: false),
                applied_policy_reference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                environment_tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                environment_lifecycle_generation = table.Column<long>(type: "bigint", nullable: false),
                applied_policy_generation = table.Column<long>(type: "bigint", nullable: false),
                egress_intent_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "pk_environment_remote_mcp_catalog_snapshots",
                    row => new
                    {
                        row.tenant_id,
                        row.project_id,
                        row.connection_id,
                        row.configuration_revision,
                        row.discovery_revision
                    });
                table.CheckConstraint(
                    "ck_environment_remote_mcp_catalog_snapshots_revisions",
                    "configuration_revision > 0 AND discovery_revision > 0");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_catalog_snapshots_digests",
                    "catalog_sha256 ~ '^[0-9a-f]{64}$' AND (registry_metadata_sha256 IS NULL OR registry_metadata_sha256 ~ '^[0-9a-f]{64}$') AND egress_intent_sha256 ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_catalog_snapshots_applied_policy",
                    "applied_policy_generation > 0 AND environment_lifecycle_generation > 0");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_catalog_snapshots_tools_json",
                    "jsonb_typeof(tools_json) = 'array'");
                table.ForeignKey(
                    "fk_environment_remote_mcp_catalog_snapshots_configuration",
                    row => new
                    {
                        row.tenant_id,
                        row.project_id,
                        row.connection_id,
                        row.configuration_revision
                    },
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "remote_mcp_connection_configurations",
                    principalColumns:
                    [
                        "tenant_id",
                        "project_id",
                        "connection_id",
                        "configuration_revision"
                    ],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "remote_mcp_connection_idempotency",
            schema: EnvironmentDbContext.Schema,
            columns: table => new
            {
                tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                result_json = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "pk_environment_remote_mcp_connection_idempotency",
                    row => new { row.tenant_id, row.project_id, row.idempotency_key });
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connection_idempotency_fingerprint",
                    "request_fingerprint ~ '^[0-9a-f]{64}$'");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connection_idempotency_operation",
                    "operation IN ('Create', 'Update', 'Enable', 'Disable', 'Remove', 'SaveDiscovery')");
                table.CheckConstraint(
                    "ck_environment_remote_mcp_connection_idempotency_result",
                    "jsonb_typeof(result_json) = 'object'");
                table.ForeignKey(
                    "fk_environment_remote_mcp_connection_idempotency_connection",
                    row => new { row.tenant_id, row.project_id, row.connection_id },
                    principalSchema: EnvironmentDbContext.Schema,
                    principalTable: "remote_mcp_connections",
                    principalColumns: ["tenant_id", "project_id", "connection_id"],
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_environment_remote_mcp_connections_project_state",
            schema: EnvironmentDbContext.Schema,
            table: "remote_mcp_connections",
            columns: ["tenant_id", "project_id", "state", "connection_id"]);
        migrationBuilder.CreateIndex(
            name: "ix_environment_remote_mcp_connection_idempotency_connection",
            schema: EnvironmentDbContext.Schema,
            table: "remote_mcp_connection_idempotency",
            columns: ["tenant_id", "project_id", "connection_id"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("remote_mcp_catalog_snapshots", EnvironmentDbContext.Schema);
        migrationBuilder.DropTable("remote_mcp_connection_idempotency", EnvironmentDbContext.Schema);
        migrationBuilder.DropTable("remote_mcp_connection_configurations", EnvironmentDbContext.Schema);
        migrationBuilder.DropTable("remote_mcp_connections", EnvironmentDbContext.Schema);
    }
}
