using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSkillRefreshPersistenceReviewed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_project_skill_admissions_admission_id_evidence_id",
                schema: "projects_config",
                table: "project_skill_admissions",
                columns: new[] { "admission_id", "evidence_id" });

            migrationBuilder.CreateTable(
                name: "project_skill_refresh_receipts",
                schema: "projects_config",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    run_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    agent_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    skill_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform_policy_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    platform_policy_revision = table.Column<long>(type: "bigint", nullable: false),
                    platform_policy_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    project_policy_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    project_policy_revision = table.Column<long>(type: "bigint", nullable: true),
                    project_policy_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_skill_refresh_receipts", x => x.receipt_id);
                    table.UniqueConstraint("AK_project_skill_refresh_receipts_receipt_id_evidence_id_admis~", x => new { x.receipt_id, x.evidence_id, x.admission_id });
                    table.CheckConstraint("ck_project_skill_refresh_receipt_hashes", "request_fingerprint ~ '^[0-9a-f]{64}$' AND content_digest ~ '^[0-9a-f]{64}$' AND platform_policy_sha256 ~ '^[0-9a-f]{64}$' AND (project_policy_sha256 IS NULL OR project_policy_sha256 ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_project_skill_refresh_receipt_project_pin", "(project_policy_id IS NULL AND project_policy_revision IS NULL AND project_policy_sha256 IS NULL) OR (project_policy_id IS NOT NULL AND project_policy_revision IS NOT NULL AND project_policy_sha256 IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_project_skill_refresh_receipts_project_skill_admissions_adm~",
                        columns: x => new { x.admission_id, x.evidence_id },
                        principalSchema: "projects_config",
                        principalTable: "project_skill_admissions",
                        principalColumns: new[] { "admission_id", "evidence_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_skill_refresh_receipts_project_skill_trust_evidence~",
                        column: x => x.evidence_id,
                        principalSchema: "projects_config",
                        principalTable: "project_skill_trust_evidence",
                        principalColumn: "evidence_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddColumn<long>(
                name: "cutover_generation",
                schema: "projects_config",
                table: "project_skill_admission_fence",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql(
                """
                UPDATE projects_config.project_skill_admission_fence
                SET cutover_generation = generation
                WHERE id = 1;
                ALTER TABLE projects_config.project_skill_admission_fence
                ADD CONSTRAINT ck_skill_admission_fence_cutover_generation
                CHECK (cutover_generation > 0);

                CREATE FUNCTION projects_config.bump_skill_admission_ordering()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                BEGIN
                    IF TG_TABLE_NAME = 'project_configuration_revisions'
                       AND NEW.revision = 1 THEN
                        RETURN NEW;
                    END IF;
                    UPDATE projects_config.project_skill_admission_fence
                    SET generation = generation + 1
                    WHERE id = 1;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER projects_skill_admission_ordering
                BEFORE INSERT ON projects_config.projects
                FOR EACH ROW EXECUTE FUNCTION projects_config.bump_skill_admission_ordering();
                CREATE TRIGGER project_configuration_revisions_skill_admission_ordering
                AFTER INSERT ON projects_config.project_configuration_revisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.bump_skill_admission_ordering();
                """);

            migrationBuilder.CreateTable(
                name: "project_skill_refresh_audit_intents",
                schema: "projects_config",
                columns: table => new
                {
                    audit_intent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    effective_role_revisions = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_skill_refresh_audit_intents", x => x.audit_intent_id);
                    table.ForeignKey(
                        name: "FK_project_skill_refresh_audit_intents_project_skill_refresh_r~",
                        columns: x => new { x.receipt_id, x.evidence_id, x.admission_id },
                        principalSchema: "projects_config",
                        principalTable: "project_skill_refresh_receipts",
                        principalColumns: new[] { "receipt_id", "evidence_id", "admission_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_skill_refresh_audit_intents_receipt_id_evidence_id_~",
                schema: "projects_config",
                table: "project_skill_refresh_audit_intents",
                columns: new[] { "receipt_id", "evidence_id", "admission_id" });

            migrationBuilder.CreateIndex(
                name: "ux_project_skill_refresh_audit_evidence",
                schema: "projects_config",
                table: "project_skill_refresh_audit_intents",
                column: "evidence_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_skill_refresh_receipts_admission_id_evidence_id",
                schema: "projects_config",
                table: "project_skill_refresh_receipts",
                columns: new[] { "admission_id", "evidence_id" });

            migrationBuilder.CreateIndex(
                name: "ux_project_skill_refresh_receipts_evidence",
                schema: "projects_config",
                table: "project_skill_refresh_receipts",
                column: "evidence_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_project_skill_refresh_receipts_idempotency",
                schema: "projects_config",
                table: "project_skill_refresh_receipts",
                columns: new[] { "project_id", "actor_issuer", "actor_id", "idempotency_key" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER project_skill_refresh_receipts_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_skill_refresh_receipts
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER project_skill_refresh_receipts_no_truncate
                BEFORE TRUNCATE ON projects_config.project_skill_refresh_receipts
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER project_skill_refresh_audit_intents_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_skill_refresh_audit_intents
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER project_skill_refresh_audit_intents_no_truncate
                BEFORE TRUNCATE ON projects_config.project_skill_refresh_audit_intents
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                LOCK TABLE projects_config.project_skill_admission_fence,
                           projects_config.project_skill_refresh_audit_intents,
                           projects_config.project_skill_refresh_receipts
                IN ACCESS EXCLUSIVE MODE;
                DO $migration$
                BEGIN
                    IF EXISTS (SELECT 1 FROM projects_config.project_skill_refresh_audit_intents)
                       OR EXISTS (SELECT 1 FROM projects_config.project_skill_refresh_receipts) THEN
                        RAISE EXCEPTION 'skill refresh persistence must be retained'
                            USING ERRCODE = '55000';
                    END IF;
                END
                $migration$;

                UPDATE projects_config.project_skill_admission_fence
                SET generation = cutover_generation
                WHERE id = 1
                  AND generation <> cutover_generation;

                ALTER TABLE projects_config.project_skill_admission_fence
                DROP CONSTRAINT ck_skill_admission_fence_cutover_generation;
                DROP TRIGGER projects_skill_admission_ordering
                ON projects_config.projects;
                DROP TRIGGER project_configuration_revisions_skill_admission_ordering
                ON projects_config.project_configuration_revisions;
                DROP FUNCTION projects_config.bump_skill_admission_ordering();
                DROP TRIGGER project_skill_refresh_audit_intents_no_truncate
                ON projects_config.project_skill_refresh_audit_intents;
                DROP TRIGGER project_skill_refresh_audit_intents_immutable
                ON projects_config.project_skill_refresh_audit_intents;
                DROP TRIGGER project_skill_refresh_receipts_no_truncate
                ON projects_config.project_skill_refresh_receipts;
                DROP TRIGGER project_skill_refresh_receipts_immutable
                ON projects_config.project_skill_refresh_receipts;
                """);
            migrationBuilder.DropTable(
                name: "project_skill_refresh_audit_intents",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "project_skill_refresh_receipts",
                schema: "projects_config");

            migrationBuilder.DropColumn(
                name: "cutover_generation",
                schema: "projects_config",
                table: "project_skill_admission_fence");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_project_skill_admissions_admission_id_evidence_id",
                schema: "projects_config",
                table: "project_skill_admissions");
        }
    }
}
