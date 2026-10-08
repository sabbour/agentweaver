using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Identity.Broker.Migrations
{
    /// <inheritdoc />
    public partial class AddGitHubRepoAppConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "repo_app_authorization_transactions",
                schema: "identity_broker",
                columns: table => new
                {
                    state_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    transaction_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    callback_cookie_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    protected_code_verifier = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    return_route_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    installation_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_app_authorization_transactions", x => x.state_hash);
                    table.CheckConstraint("ck_repo_app_authorization_state", "state IN (0, 1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_repo_app_authorization_transactions_broker_users_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "identity_broker",
                        principalTable: "broker_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repo_app_connections",
                schema: "identity_broker",
                columns: table => new
                {
                    connection_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    github_login = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    access_token_secret_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    access_token_secret_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    access_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    refresh_token_secret_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    refresh_token_secret_version = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    refresh_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    connection_revision = table.Column<long>(type: "bigint", nullable: false),
                    credential_revision = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    refresh_lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refresh_lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_app_connections", x => x.connection_id);
                    table.CheckConstraint("ck_repo_app_connection_refresh_lease", "(refresh_lease_id IS NULL) = (refresh_lease_expires_at IS NULL)");
                    table.CheckConstraint("ck_repo_app_connection_revisions", "connection_revision > 0 AND credential_revision > 0");
                    table.CheckConstraint("ck_repo_app_connection_state", "state IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_repo_app_connections_broker_users_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "identity_broker",
                        principalTable: "broker_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repo_app_installations",
                schema: "identity_broker",
                columns: table => new
                {
                    connection_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    account_login = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    account_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    repository_selection = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    connection_revision = table.Column<long>(type: "bigint", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_app_installations", x => new { x.connection_id, x.installation_id });
                    table.CheckConstraint("ck_repo_app_installation_identity", "installation_id > 0 AND connection_revision > 0");
                    table.ForeignKey(
                        name: "FK_repo_app_installations_repo_app_connections_connection_id",
                        column: x => x.connection_id,
                        principalSchema: "identity_broker",
                        principalTable: "repo_app_connections",
                        principalColumn: "connection_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "repo_app_repository_selections",
                schema: "identity_broker",
                columns: table => new
                {
                    code_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    connection_revision = table.Column<long>(type: "bigint", nullable: false),
                    installation_id = table.Column<long>(type: "bigint", nullable: false),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    repository_full_name = table.Column<string>(type: "character varying(201)", maxLength: 201, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    project_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    permission_digest = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    issue_write_requested = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_repo_app_repository_selections", x => x.code_hash);
                    table.CheckConstraint("ck_repo_app_selection_binding", "connection_revision > 0 AND installation_id > 0 AND repository_id > 0 AND expires_at > created_at");
                    table.CheckConstraint("ck_repo_app_selection_permission_digest", "permission_digest IS NULL OR permission_digest ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_repo_app_repository_selections_broker_users_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "identity_broker",
                        principalTable: "broker_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_repo_app_repository_selections_repo_app_connections_connect~",
                        column: x => x.connection_id,
                        principalSchema: "identity_broker",
                        principalTable: "repo_app_connections",
                        principalColumn: "connection_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_repo_app_authorization_transactions_expires_at",
                schema: "identity_broker",
                table: "repo_app_authorization_transactions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_repo_app_authorization_transactions_owner_id",
                schema: "identity_broker",
                table: "repo_app_authorization_transactions",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_repo_app_authorization_transactions_transaction_id",
                schema: "identity_broker",
                table: "repo_app_authorization_transactions",
                column: "transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repo_app_connections_owner_id",
                schema: "identity_broker",
                table: "repo_app_connections",
                column: "owner_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repo_app_repository_selections_connection_id",
                schema: "identity_broker",
                table: "repo_app_repository_selections",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "IX_repo_app_repository_selections_expires_at",
                schema: "identity_broker",
                table: "repo_app_repository_selections",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_repo_app_repository_selections_owner_id",
                schema: "identity_broker",
                table: "repo_app_repository_selections",
                column: "owner_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "repo_app_authorization_transactions",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "repo_app_installations",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "repo_app_repository_selections",
                schema: "identity_broker");

            migrationBuilder.DropTable(
                name: "repo_app_connections",
                schema: "identity_broker");
        }
    }
}
