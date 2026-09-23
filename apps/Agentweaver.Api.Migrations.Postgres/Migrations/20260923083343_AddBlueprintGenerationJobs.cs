using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Api.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddBlueprintGenerationJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "blueprint_generation_jobs",
                columns: table => new
                {
                    job_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    target_repository = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    blueprint_model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    workflow_model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    provider_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    resolution_scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    credential_binding_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    queued_provider_key = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    failure_retryable = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blueprint_generation_jobs", x => x.job_id);
                });

            migrationBuilder.CreateTable(
                name: "blueprint_generation_artifacts",
                columns: table => new
                {
                    artifact_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    job_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    logical_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    blueprint_json = table.Column<string>(type: "text", nullable: false),
                    generated_workflow_yaml = table.Column<string>(type: "text", nullable: true),
                    warnings_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_blueprint_generation_artifacts", x => x.artifact_id);
                    table.ForeignKey(
                        name: "FK_blueprint_generation_artifacts_blueprint_generation_jobs_jo~",
                        column: x => x.job_id,
                        principalTable: "blueprint_generation_jobs",
                        principalColumn: "job_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_blueprint_generation_artifacts_job_id",
                table: "blueprint_generation_artifacts",
                column: "job_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_blueprint_generation_jobs_status_lease_expires_at_created_at",
                table: "blueprint_generation_jobs",
                columns: new[] { "status", "lease_expires_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_blueprint_generation_jobs_subject_idempotency_key",
                table: "blueprint_generation_jobs",
                columns: new[] { "subject", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "blueprint_generation_artifacts");

            migrationBuilder.DropTable(
                name: "blueprint_generation_jobs");
        }
    }
}
