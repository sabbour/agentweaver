using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRunGitHubCapabilitySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "capability_purpose",
                table: "github_audit_records",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "grant_digest",
                table: "github_audit_records",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "run_github_capability_snapshots",
                columns: table => new
                {
                    snapshot_ref = table.Column<string>(type: "TEXT", nullable: false),
                    run_id = table.Column<string>(type: "TEXT", nullable: false),
                    purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    app_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    source_kind = table.Column<int>(type: "INTEGER", nullable: false),
                    project_id = table.Column<string>(type: "TEXT", nullable: false),
                    entra_object_id = table.Column<string>(type: "TEXT", nullable: true),
                    source_authorization_id = table.Column<string>(type: "TEXT", nullable: true),
                    source_binding_id = table.Column<string>(type: "TEXT", nullable: true),
                    installation_id = table.Column<long>(type: "INTEGER", nullable: true),
                    repository_id = table.Column<long>(type: "INTEGER", nullable: true),
                    credential_reference = table.Column<string>(type: "TEXT", nullable: true),
                    credential_version = table.Column<string>(type: "TEXT", nullable: true),
                    grant_digest = table.Column<string>(type: "TEXT", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    snapshot_expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_github_capability_snapshots", x => x.snapshot_ref);
                    table.CheckConstraint("CK_run_github_capability_snapshots_purpose_mapping", "(purpose = 0 AND app_kind = 0 AND source_kind = 0 AND entra_object_id IS NOT NULL AND source_authorization_id IS NOT NULL AND source_binding_id IS NULL AND installation_id IS NULL AND repository_id IS NOT NULL AND credential_reference IS NOT NULL AND credential_version IS NOT NULL)\nOR (purpose = 1 AND app_kind = 0 AND source_kind = 0 AND entra_object_id IS NOT NULL AND source_authorization_id IS NOT NULL AND source_binding_id IS NULL AND installation_id IS NULL AND repository_id IS NULL AND credential_reference IS NOT NULL AND credential_version IS NOT NULL)\nOR (purpose = 2 AND app_kind = 0 AND source_kind = 1 AND entra_object_id IS NULL AND source_authorization_id IS NULL AND source_binding_id IS NULL AND installation_id IS NOT NULL AND repository_id IS NOT NULL AND credential_reference IS NULL AND credential_version IS NULL)\nOR (purpose = 3 AND app_kind = 1 AND source_kind = 2 AND entra_object_id IS NULL AND source_authorization_id IS NULL AND source_binding_id IS NOT NULL AND installation_id IS NULL AND repository_id IS NULL AND credential_reference IS NOT NULL AND credential_version IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_run_github_capability_snapshots_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_run_github_capability_snapshots_project_id",
                table: "run_github_capability_snapshots",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "UX_run_github_capability_snapshots_run_purpose",
                table: "run_github_capability_snapshots",
                columns: new[] { "run_id", "purpose" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE TRIGGER TR_run_github_capability_snapshots_immutable
                BEFORE UPDATE ON run_github_capability_snapshots
                BEGIN
                    SELECT RAISE(ABORT, 'Capability snapshots are immutable.');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Capability snapshot schema changes are forward-only.");
        }
    }
}
