using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteMcpConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "remote_mcp_oauth_connections",
                schema: "identity_broker",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    owner_actor_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                    environment_configuration_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    identity_binding_reference = table.Column<string>(type: "character(32)", fixedLength: true, maxLength: 32, nullable: false),
                    endpoint_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    resource_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    issuer_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    redirect_uri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    transport_profile = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    scopes_json = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    binding_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    connection_revision = table.Column<long>(type: "bigint", nullable: false),
                    credential_revision = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    access_token_secret_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    access_token_secret_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    access_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refresh_token_secret_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    refresh_token_secret_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    refresh_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    refresh_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refresh_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_disconnect_idempotency_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    last_disconnect_request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    last_disconnect_result_revision = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_remote_mcp_oauth_connections", x => x.id);
                    table.CheckConstraint("ck_remote_mcp_oauth_connection_disconnect", "(last_disconnect_idempotency_hash IS NULL) = (last_disconnect_request_hash IS NULL) AND (last_disconnect_idempotency_hash IS NULL) = (last_disconnect_result_revision IS NULL)");
                    table.CheckConstraint("ck_remote_mcp_oauth_connection_hashes", "environment_configuration_hash ~ '^[0-9a-f]{64}$' AND binding_hash ~ '^[0-9a-f]{64}$' AND (last_disconnect_idempotency_hash IS NULL OR last_disconnect_idempotency_hash ~ '^[0-9a-f]{64}$') AND (last_disconnect_request_hash IS NULL OR last_disconnect_request_hash ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_remote_mcp_oauth_connection_refs", "(access_token_secret_id IS NULL) = (access_token_secret_version IS NULL) AND (access_token_secret_id IS NULL) = (access_token_expires_at IS NULL) AND (refresh_token_secret_id IS NULL) = (refresh_token_secret_version IS NULL) AND (refresh_token_secret_id IS NOT NULL OR refresh_token_expires_at IS NULL)");
                    table.CheckConstraint("ck_remote_mcp_oauth_connection_revisions", "configuration_revision > 0 AND connection_revision > 0 AND credential_revision >= 0");
                    table.CheckConstraint("ck_remote_mcp_oauth_connection_state", "state IN (0, 1, 2, 3, 4, 5, 6) AND ((state IN (2, 3, 4) AND access_token_secret_id IS NOT NULL) OR (state NOT IN (2, 3, 4) AND access_token_secret_id IS NULL)) AND ((state IN (3, 4) AND refresh_token_secret_id IS NOT NULL AND refresh_attempt_id IS NOT NULL AND refresh_started_at IS NOT NULL) OR (state NOT IN (3, 4) AND refresh_attempt_id IS NULL AND refresh_started_at IS NULL))");
                    table.ForeignKey(
                        name: "FK_remote_mcp_oauth_connections_broker_users_OwnerId",
                        column: x => x.owner_id,
                        principalSchema: "identity_broker",
                        principalTable: "broker_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "remote_mcp_oauth_consents",
                schema: "identity_broker",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    binding_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    connection_revision = table.Column<long>(type: "bigint", nullable: false),
                    state_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    pkce_challenge = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    verifier_secret_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    verifier_secret_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    claim_attempt_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_remote_mcp_oauth_consents", x => x.id);
                    table.CheckConstraint("ck_remote_mcp_oauth_consent_challenge", "pkce_challenge ~ '^[A-Za-z0-9_-]{43}$'");
                    table.CheckConstraint("ck_remote_mcp_oauth_consent_hashes", "binding_hash ~ '^[0-9a-f]{64}$' AND state_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_remote_mcp_oauth_consent_revisions", "connection_revision > 0 AND revision > 0 AND expires_at > created_at");
                    table.CheckConstraint("ck_remote_mcp_oauth_consent_state", "state IN (0, 1, 2, 3, 4) AND ((state = 1 AND claim_attempt_id IS NOT NULL) OR (state <> 1 AND claim_attempt_id IS NULL))");
                    table.ForeignKey(
                        name: "FK_remote_mcp_oauth_consents_remote_mcp_oauth_connections_Conn~",
                        column: x => x.connection_record_id,
                        principalSchema: "identity_broker",
                        principalTable: "remote_mcp_oauth_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_remote_mcp_oauth_connections_IdentityBindingReference",
                schema: "identity_broker",
                table: "remote_mcp_oauth_connections",
                column: "identity_binding_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_remote_mcp_oauth_connections_OwnerId_TenantId_ProjectId_Con~",
                schema: "identity_broker",
                table: "remote_mcp_oauth_connections",
                columns: new[] { "owner_id", "tenant_id", "project_id", "connection_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_remote_mcp_oauth_consents_ConnectionRecordId",
                schema: "identity_broker",
                table: "remote_mcp_oauth_consents",
                column: "connection_record_id");

            migrationBuilder.CreateIndex(
                name: "IX_remote_mcp_oauth_consents_CorrelationId",
                schema: "identity_broker",
                table: "remote_mcp_oauth_consents",
                column: "correlation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_remote_mcp_oauth_consents_ExpiresAt",
                schema: "identity_broker",
                table: "remote_mcp_oauth_consents",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_remote_mcp_oauth_consents_StateHash",
                schema: "identity_broker",
                table: "remote_mcp_oauth_consents",
                column: "state_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "remote_mcp_oauth_consents",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "remote_mcp_oauth_connections",
                schema: "identity_broker");
        }
    }
}
