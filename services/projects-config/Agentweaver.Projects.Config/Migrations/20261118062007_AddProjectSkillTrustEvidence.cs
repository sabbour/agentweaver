using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSkillTrustEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_skill_trust_evidence",
                schema: "projects_config",
                columns: table => new
                {
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    skill_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    actor_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    source_revision = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    requested_ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    resolved_commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    selected_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    report_set = table.Column<byte[]>(type: "bytea", nullable: false),
                    report_set_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_skill_trust_evidence", x => x.evidence_id);
                    table.CheckConstraint("ck_project_skill_trust_evidence_hashes", "request_fingerprint ~ '^[0-9a-f]{64}$' AND content_digest ~ '^[0-9a-f]{64}$' AND report_set_sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_project_skill_trust_evidence_report_size", "octet_length(report_set) BETWEEN 1 AND 1048576");
                    table.CheckConstraint("ck_project_skill_trust_evidence_revision", "revision > 0");
                    table.ForeignKey(
                        name: "FK_project_skill_trust_evidence_project_skill_content_revision~",
                        columns: x => new { x.project_id, x.skill_id, x.revision },
                        principalSchema: "projects_config",
                        principalTable: "project_skill_content_revisions",
                        principalColumns: new[] { "project_id", "skill_id", "revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_skill_trust_evidence_project_id_skill_id_revision",
                schema: "projects_config",
                table: "project_skill_trust_evidence",
                columns: new[] { "project_id", "skill_id", "revision" });

            migrationBuilder.CreateIndex(
                name: "ux_project_skill_trust_evidence_idempotency",
                schema: "projects_config",
                table: "project_skill_trust_evidence",
                columns: new[] { "project_id", "actor_issuer", "actor_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER project_skill_trust_evidence_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_skill_trust_evidence
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER project_skill_trust_evidence_no_truncate
                BEFORE TRUNCATE ON projects_config.project_skill_trust_evidence
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_skill_trust_evidence",
                schema: "projects_config");
        }
    }
}
