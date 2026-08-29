using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
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
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "grant_digest",
                table: "github_audit_records",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "run_github_capability_snapshots",
                columns: table => new
                {
                    snapshot_ref = table.Column<string>(type: "text", nullable: false),
                    run_id = table.Column<string>(type: "text", nullable: false),
                    purpose = table.Column<int>(type: "integer", nullable: false),
                    app_kind = table.Column<int>(type: "integer", nullable: false),
                    source_kind = table.Column<int>(type: "integer", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    entra_object_id = table.Column<string>(type: "text", nullable: true),
                    source_authorization_id = table.Column<string>(type: "text", nullable: true),
                    source_binding_id = table.Column<string>(type: "text", nullable: true),
                    installation_id = table.Column<long>(type: "bigint", nullable: true),
                    repository_id = table.Column<long>(type: "bigint", nullable: true),
                    credential_reference = table.Column<string>(type: "text", nullable: true),
                    credential_version = table.Column<string>(type: "text", nullable: true),
                    grant_digest = table.Column<string>(type: "text", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    snapshot_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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
                CREATE FUNCTION prevent_run_github_capability_snapshot_update()
                RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Capability snapshots are immutable.';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER TR_run_github_capability_snapshots_immutable
                BEFORE UPDATE ON run_github_capability_snapshots
                FOR EACH ROW EXECUTE FUNCTION prevent_run_github_capability_snapshot_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Capability snapshot schema changes are forward-only.");
        }
    }
}
