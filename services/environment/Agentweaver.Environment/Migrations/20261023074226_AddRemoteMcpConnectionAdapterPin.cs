using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Environment.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteMcpConnectionAdapterPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_environment_remote_mcp_connection_configurations_transport",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.AddColumn<string>(
                name: "transport_adapter_id",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "streamable-http");

            migrationBuilder.AddColumn<string>(
                name: "transport_adapter_version",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "1.0.0");

            migrationBuilder.AddColumn<int>(
                name: "transport_maximum_response_bytes",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 1048576);

            migrationBuilder.AddColumn<int>(
                name: "transport_maximum_tools_list_pages",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 16);

            migrationBuilder.AddColumn<string>(
                name: "transport_options_revision",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "streamable-http-2025-06-18-v1");

            migrationBuilder.AddColumn<int>(
                name: "transport_options_schema_version",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "transport_options_sha256",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "d01cdc1f6ba701256d992bdc1ecc76f6ff3445ccb0c957a285c71c6a5c03fe93");

            migrationBuilder.AddColumn<string>(
                name: "transport_protocol_version",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "2025-06-18");

            migrationBuilder.AddColumn<int>(
                name: "transport_request_timeout_seconds",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddCheckConstraint(
                name: "ck_environment_remote_mcp_connection_configurations_transport",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                sql: "transport_profile = 'StreamableHttp20250618' AND transport_adapter_id = 'streamable-http' AND transport_adapter_version = '1.0.0' AND transport_options_schema_version = 1 AND transport_options_revision = 'streamable-http-2025-06-18-v1' AND transport_protocol_version = '2025-06-18' AND transport_request_timeout_seconds = 30 AND transport_maximum_response_bytes = 1048576 AND transport_maximum_tools_list_pages = 16 AND transport_options_sha256 = 'd01cdc1f6ba701256d992bdc1ecc76f6ff3445ccb0c957a285c71c6a5c03fe93'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_environment_remote_mcp_connection_configurations_transport",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.DropColumn(
                name: "transport_adapter_id",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.DropColumn(
                name: "transport_adapter_version",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.DropColumn(
                name: "transport_maximum_response_bytes",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.DropColumn(
                name: "transport_maximum_tools_list_pages",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.DropColumn(
                name: "transport_options_revision",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.DropColumn(
                name: "transport_options_schema_version",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.DropColumn(
                name: "transport_options_sha256",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.DropColumn(
                name: "transport_protocol_version",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.DropColumn(
                name: "transport_request_timeout_seconds",
                schema: "environment",
                table: "remote_mcp_connection_configurations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_environment_remote_mcp_connection_configurations_transport",
                schema: "environment",
                table: "remote_mcp_connection_configurations",
                sql: "transport_profile = 'StreamableHttp20250618'");
        }
    }
}
