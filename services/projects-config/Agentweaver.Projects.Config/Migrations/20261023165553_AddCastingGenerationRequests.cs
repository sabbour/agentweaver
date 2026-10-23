using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddCastingGenerationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "casting_generation_requests",
                schema: "projects_config",
                columns: table => new
                {
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    proposal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_configuration_revision = table.Column<long>(type: "bigint", nullable: false),
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    actor_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    output_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_casting_generation_requests", x => x.request_id);
                    table.CheckConstraint("ck_casting_generation_requests_base_revision", "base_configuration_revision > 0");
                    table.CheckConstraint("ck_casting_generation_requests_idempotency_key", "idempotency_key ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$'");
                    table.CheckConstraint("ck_casting_generation_requests_mode", "mode IN ('Formulate', 'Analyze')");
                    table.CheckConstraint("ck_casting_generation_requests_request_digest", "request_digest ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_casting_generation_requests_result", "(state = 'Completed' AND output_digest IS NOT NULL AND output_digest ~ '^[0-9a-f]{64}$' AND failure_code IS NULL) OR (state = 'Failed' AND output_digest IS NULL AND failure_code IS NOT NULL AND failure_code ~ '^[a-z][a-z0-9_]{0,63}$') OR (state IN ('Pending', 'Stale', 'Cancelled') AND output_digest IS NULL AND failure_code IS NULL)");
                    table.CheckConstraint("ck_casting_generation_requests_state", "state IN ('Pending', 'Completed', 'Failed', 'Stale', 'Cancelled')");
                    table.ForeignKey(
                        name: "FK_casting_generation_requests_project_casting_proposals_propo~",
                        column: x => x.proposal_id,
                        principalSchema: "projects_config",
                        principalTable: "project_casting_proposals",
                        principalColumn: "proposal_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_casting_generation_requests_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "projects_config",
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_casting_generation_requests_proposal_id",
                schema: "projects_config",
                table: "casting_generation_requests",
                column: "proposal_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_casting_generation_requests_idempotency",
                schema: "projects_config",
                table: "casting_generation_requests",
                columns: new[] { "project_id", "actor_issuer", "actor_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_casting_generation_requests_project_run",
                schema: "projects_config",
                table: "casting_generation_requests",
                columns: new[] { "project_id", "run_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "casting_generation_requests",
                schema: "projects_config");
        }
    }
}
