using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoAppPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "github_app_authorizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: false),
                    app_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    credential_reference = table.Column<string>(type: "TEXT", nullable: false),
                    credential_version = table.Column<string>(type: "TEXT", nullable: false),
                    grant_digest = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_app_authorizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "github_audit_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: true),
                    actor_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    action = table.Column<int>(type: "INTEGER", nullable: false),
                    resource_id = table.Column<string>(type: "TEXT", nullable: false),
                    app_kind = table.Column<int>(type: "INTEGER", nullable: true),
                    purpose = table.Column<int>(type: "INTEGER", nullable: true),
                    outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    reason_code = table.Column<int>(type: "INTEGER", nullable: false),
                    correlation_id = table.Column<string>(type: "TEXT", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    credential_version_or_digest = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_audit_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    OriginKind = table.Column<string>(type: "TEXT", nullable: false),
                    SourceRepository = table.Column<string>(type: "TEXT", nullable: true),
                    WorkingDirectory = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultProvider = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultModelCopilot = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultModelFoundry = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    WebhookSecret = table.Column<string>(type: "TEXT", nullable: true),
                    TeamRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxReadyPerHeartbeat = table.Column<int>(type: "INTEGER", nullable: false),
                    PickupAutopilot = table.Column<bool>(type: "INTEGER", nullable: false),
                    PickupAutoApproveTools = table.Column<bool>(type: "INTEGER", nullable: false),
                    PreviewApprovalTimeoutMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultWorkflowId = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveReviewPolicyName = table.Column<string>(type: "TEXT", nullable: true),
                    SandboxProfile = table.Column<string>(type: "TEXT", nullable: true),
                    SourceBlueprintId = table.Column<string>(type: "TEXT", nullable: true),
                    SourceBlueprintType = table.Column<string>(type: "TEXT", nullable: true),
                    BlueprintGenerationModel = table.Column<string>(type: "TEXT", nullable: true),
                    WorkflowGenerationModel = table.Column<string>(type: "TEXT", nullable: true),
                    OutcomeSpecGenerationModel = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedWorkflowIds = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.project_id);
                });

            migrationBuilder.CreateTable(
                name: "github_authorizations",
                columns: table => new
                {
                    state = table.Column<string>(type: "TEXT", nullable: false),
                    app_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: true),
                    expires_at_unix_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    return_route_key = table.Column<string>(type: "TEXT", nullable: false),
                    pkce_verifier_protected = table.Column<string>(type: "TEXT", nullable: false),
                    callback_cookie_hash = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_authorizations", x => x.state);
                    table.ForeignKey(
                        name: "FK_github_authorizations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "github_installations",
                columns: table => new
                {
                    installation_id = table.Column<long>(type: "INTEGER", nullable: false),
                    app_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_installations", x => x.installation_id);
                    table.ForeignKey(
                        name: "FK_github_installations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_copilot_bindings",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: false),
                    credential_reference = table.Column<string>(type: "TEXT", nullable: false),
                    credential_version = table.Column<string>(type: "TEXT", nullable: false),
                    grant_digest = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    bound_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_copilot_bindings", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_copilot_bindings_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run_github_identity_snapshots",
                columns: table => new
                {
                    run_id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    app_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    credential_reference = table.Column<string>(type: "TEXT", nullable: false),
                    credential_version = table.Column<string>(type: "TEXT", nullable: false),
                    grant_digest = table.Column<string>(type: "TEXT", nullable: false),
                    installation_id = table.Column<long>(type: "INTEGER", nullable: true),
                    repository_id = table.Column<long>(type: "INTEGER", nullable: true),
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_github_identity_snapshots", x => x.run_id);
                    table.ForeignKey(
                        name: "FK_run_github_identity_snapshots_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "github_repository_grants",
                columns: table => new
                {
                    installation_id = table.Column<long>(type: "INTEGER", nullable: false),
                    repository_id = table.Column<long>(type: "INTEGER", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    full_name_display = table.Column<string>(type: "TEXT", nullable: false),
                    permission_digest = table.Column<string>(type: "TEXT", nullable: false),
                    granted_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_repository_grants", x => new { x.installation_id, x.repository_id });
                    table.ForeignKey(
                        name: "FK_github_repository_grants_installations_installation_id",
                        column: x => x.installation_id,
                        principalTable: "github_installations",
                        principalColumn: "installation_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_github_repository_grants_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "automation_activations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    installation_id = table.Column<long>(type: "INTEGER", nullable: false),
                    repository_id = table.Column<long>(type: "INTEGER", nullable: false),
                    automation_key = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    invalidated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_activations", x => x.id);
                    table.ForeignKey(
                        name: "FK_automation_activations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_automation_activations_repository_grants_installation_id_repository_id",
                        columns: x => new { x.installation_id, x.repository_id },
                        principalTable: "github_repository_grants",
                        principalColumns: new[] { "installation_id", "repository_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "automation_invocations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    activation_id = table.Column<string>(type: "TEXT", nullable: false),
                    occurrence_key = table.Column<string>(type: "TEXT", nullable: false),
                    delivery_id = table.Column<string>(type: "TEXT", nullable: true),
                    event_name = table.Column<string>(type: "TEXT", nullable: true),
                    installation_id = table.Column<long>(type: "INTEGER", nullable: true),
                    repository_id = table.Column<long>(type: "INTEGER", nullable: true),
                    outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_invocations", x => x.id);
                    table.ForeignKey(
                        name: "FK_automation_invocations_activations_activation_id",
                        column: x => x.activation_id,
                        principalTable: "automation_activations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_automation_invocations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_automation_activations_installation_id_repository_id",
                table: "automation_activations",
                columns: new[] { "installation_id", "repository_id" });

            migrationBuilder.CreateIndex(
                name: "IX_automation_activations_project_id_installation_id_repository_id_automation_key",
                table: "automation_activations",
                columns: new[] { "project_id", "installation_id", "repository_id", "automation_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_automation_invocations_activation_id_occurrence_key",
                table: "automation_invocations",
                columns: new[] { "activation_id", "occurrence_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_automation_invocations_delivery_id_event_name",
                table: "automation_invocations",
                columns: new[] { "delivery_id", "event_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_automation_invocations_project_id",
                table: "automation_invocations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_github_app_authorizations_entra_object_id_app_kind_purpose",
                table: "github_app_authorizations",
                columns: new[] { "entra_object_id", "app_kind", "purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_github_audit_records_occurred_at",
                table: "github_audit_records",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_github_authorizations_entra_object_id_state",
                table: "github_authorizations",
                columns: new[] { "entra_object_id", "state" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_authorizations_expires_at_unix_ms",
                table: "github_authorizations",
                column: "expires_at_unix_ms");

            migrationBuilder.CreateIndex(
                name: "IX_github_authorizations_project_id",
                table: "github_authorizations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_github_installations_project_id",
                table: "github_installations",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_grants_installation_id_repository_id",
                table: "github_repository_grants",
                columns: new[] { "installation_id", "repository_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_repository_grants_project_id",
                table: "github_repository_grants",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "UX_project_copilot_bindings_active_project",
                table: "project_copilot_bindings",
                column: "project_id",
                unique: true,
                filter: "status = 0");

            migrationBuilder.CreateIndex(
                name: "IX_run_github_identity_snapshots_project_id",
                table: "run_github_identity_snapshots",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_invocations");

            migrationBuilder.DropTable(
                name: "github_app_authorizations");

            migrationBuilder.DropTable(
                name: "github_audit_records");

            migrationBuilder.DropTable(
                name: "github_authorizations");

            migrationBuilder.DropTable(
                name: "project_copilot_bindings");

            migrationBuilder.DropTable(
                name: "run_github_identity_snapshots");

            migrationBuilder.DropTable(
                name: "automation_activations");

            migrationBuilder.DropTable(
                name: "github_repository_grants");

            migrationBuilder.DropTable(
                name: "github_installations");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
