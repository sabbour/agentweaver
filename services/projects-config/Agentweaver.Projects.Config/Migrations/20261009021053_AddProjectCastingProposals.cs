using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectCastingProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_casting_proposals",
                schema: "projects_config",
                columns: table => new
                {
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    base_configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                    draft_revision = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    draft = table.Column<string>(type: "jsonb", nullable: false),
                    confirmed_configuration_revision = table.Column<long>(type: "bigint", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    transfer_format_version = table.Column<int>(type: "integer", nullable: true),
                    transfer_source_project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    transfer_source_configuration_revision = table.Column<long>(type: "bigint", nullable: true),
                    transfer_content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_casting_proposals", x => x.proposal_id);
                    table.CheckConstraint("ck_project_casting_proposals_base_revision", "base_configuration_revision > 0");
                    table.CheckConstraint("ck_project_casting_proposals_confirmed_result", "(state = 'Confirmed' AND confirmed_configuration_revision IS NOT NULL AND result IS NOT NULL) OR (state <> 'Confirmed' AND confirmed_configuration_revision IS NULL AND result IS NULL)");
                    table.CheckConstraint("ck_project_casting_proposals_draft_revision", "draft_revision > 0");
                    table.CheckConstraint("ck_project_casting_proposals_state", "state IN ('Pending', 'Confirmed', 'Rejected')");
                    table.CheckConstraint("ck_project_casting_proposals_transfer_provenance", "(transfer_format_version IS NULL AND transfer_source_project_id IS NULL AND transfer_source_configuration_revision IS NULL AND transfer_content_digest IS NULL) OR (transfer_format_version = 1 AND transfer_source_project_id IS NOT NULL AND transfer_source_configuration_revision > 0 AND transfer_content_digest ~ '^[0-9a-f]{64}$')");
                    table.ForeignKey(
                        name: "FK_project_casting_proposals_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "projects_config",
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_casting_proposals_project_id_created_at",
                schema: "projects_config",
                table: "project_casting_proposals",
                columns: new[] { "project_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_casting_proposals",
                schema: "projects_config");
        }
    }
}
