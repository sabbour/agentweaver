using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddSkillAdmissionCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "capability_declaration_valid",
                schema: "projects_config",
                table: "project_skill_trust_evidence",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "heuristic_score",
                schema: "projects_config",
                table: "project_skill_trust_evidence",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "score_status",
                schema: "projects_config",
                table: "project_skill_trust_evidence",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unavailable");

            migrationBuilder.AddColumn<bool>(
                name: "short_description",
                schema: "projects_config",
                table: "project_skill_trust_evidence",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "signature_verified",
                schema: "projects_config",
                table: "project_skill_trust_evidence",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "trust_tier",
                schema: "projects_config",
                table: "project_skill_trust_evidence",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "admission_classification",
                schema: "projects_config",
                table: "project_skill_content_revisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LegacyThirdParty");

            migrationBuilder.AddColumn<string>(
                name: "source_authority_kind",
                schema: "projects_config",
                table: "project_skill_content_revisions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "CallerClaimed");

            migrationBuilder.CreateTable(
                name: "project_skill_admission_fence",
                schema: "projects_config",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    release_inventory_revision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    platform_skills_policy_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_skill_admission_fence", x => x.id);
                    table.CheckConstraint("ck_skill_admission_fence_generation", "generation > 0");
                    table.CheckConstraint("ck_skill_admission_fence_id", "id = 1");
                    table.CheckConstraint("ck_skill_admission_fence_state", "state IN ('Open', 'Draining', 'Active')");
                });

            migrationBuilder.CreateTable(
                name: "project_skill_admissions",
                schema: "projects_config",
                columns: table => new
                {
                    admission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    skill_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    evidence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cutover_generation = table.Column<long>(type: "bigint", nullable: false),
                    classification = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_authority_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    verified_source_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    verified_source_revision = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    verified_requested_ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    verified_resolved_commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    verified_selected_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    release_id = table.Column<Guid>(type: "uuid", nullable: true),
                    trusted_publisher_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    signature_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    platform_policy_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    platform_policy_revision = table.Column<long>(type: "bigint", nullable: true),
                    platform_policy_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    project_policy_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    project_policy_revision = table.Column<long>(type: "bigint", nullable: true),
                    project_policy_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_skill_admissions", x => x.admission_id);
                    table.ForeignKey(
                        name: "FK_project_skill_admissions_project_skill_content_revisions_pr~",
                        columns: x => new { x.project_id, x.skill_id, x.revision },
                        principalSchema: "projects_config",
                        principalTable: "project_skill_content_revisions",
                        principalColumns: new[] { "project_id", "skill_id", "revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_project_skill_admissions_project_skill_trust_evidence_evide~",
                        column: x => x.evidence_id,
                        principalSchema: "projects_config",
                        principalTable: "project_skill_trust_evidence",
                        principalColumn: "evidence_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "project_skill_builtin_releases",
                schema: "projects_config",
                columns: table => new
                {
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inventory_revision = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    repository = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    resolved_commit_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    selected_path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    content_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    trusted_publisher_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    signature_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_skill_builtin_releases", x => x.release_id);
                    table.CheckConstraint("ck_skill_builtin_release_hashes", "content_digest ~ '^[0-9a-f]{64}$' AND resolved_commit_sha ~ '^[0-9a-f]{40,64}$' AND (signature_sha256 IS NULL OR signature_sha256 ~ '^[0-9a-f]{64}$')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_skill_admissions_project_id_skill_id_revision",
                schema: "projects_config",
                table: "project_skill_admissions",
                columns: new[] { "project_id", "skill_id", "revision" });

            migrationBuilder.CreateIndex(
                name: "IX_project_skill_admissions_evidence_id",
                schema: "projects_config",
                table: "project_skill_admissions",
                column: "evidence_id");

            migrationBuilder.CreateIndex(
                name: "ux_project_skill_builtin_release_artifact",
                schema: "projects_config",
                table: "project_skill_builtin_releases",
                columns: new[] { "inventory_revision", "repository", "resolved_commit_sha", "selected_path", "content_digest" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO projects_config.project_skill_admission_fence
                    (id, state, generation, release_inventory_revision,
                     platform_skills_policy_sha256, updated_at)
                VALUES (1, 'Open', 1, NULL, NULL, now());

                CREATE FUNCTION projects_config.require_skill_admission_writer()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    current_state text;
                BEGIN
                    SELECT state INTO current_state
                    FROM projects_config.project_skill_admission_fence
                    WHERE id = 1
                    FOR SHARE;

                    IF current_state = 'Open' THEN
                        RETURN NEW;
                    END IF;
                    IF current_state = 'Active'
                       AND current_setting('agentweaver.skill_admission_v1', true) = '1' THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'skill admission fence blocks imports';
                END;
                $function$;

                CREATE FUNCTION projects_config.register_verified_skill_release(
                    p_release_id uuid,
                    p_inventory_revision text,
                    p_repository text,
                    p_resolved_commit_sha text,
                    p_selected_path text,
                    p_content_digest text,
                    p_trusted_publisher_id text,
                    p_signature_sha256 text,
                    p_created_at timestamptz)
                RETURNS void
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog
                AS $function$
                BEGIN
                    IF current_setting('agentweaver.skill_release_management_v1', true) <> '1' THEN
                        RAISE EXCEPTION 'verified Skill release registration is required';
                    END IF;
                    INSERT INTO projects_config.project_skill_builtin_releases
                        (release_id, inventory_revision, repository, resolved_commit_sha,
                         selected_path, content_digest, trusted_publisher_id,
                         signature_sha256, is_active, created_at)
                    VALUES
                        (p_release_id, p_inventory_revision, p_repository, p_resolved_commit_sha,
                         p_selected_path, p_content_digest, p_trusted_publisher_id,
                         p_signature_sha256, true, p_created_at);
                END;
                $function$;

                CREATE TRIGGER require_skill_admission_writer
                BEFORE INSERT ON projects_config.project_skill_content_revisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.require_skill_admission_writer();

                CREATE TRIGGER project_skill_admissions_immutable
                BEFORE UPDATE OR DELETE ON projects_config.project_skill_admissions
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                CREATE TRIGGER project_skill_admissions_no_truncate
                BEFORE TRUNCATE ON projects_config.project_skill_admissions
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER require_skill_admission_writer
                ON projects_config.project_skill_content_revisions;
                DROP TRIGGER project_skill_admissions_immutable
                ON projects_config.project_skill_admissions;
                DROP TRIGGER project_skill_admissions_no_truncate
                ON projects_config.project_skill_admissions;
                DROP FUNCTION projects_config.require_skill_admission_writer();
                DROP FUNCTION projects_config.register_verified_skill_release(
                    uuid, text, text, text, text, text, text, text, timestamptz);
                """);
            migrationBuilder.DropTable(
                name: "project_skill_admission_fence",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "project_skill_admissions",
                schema: "projects_config");

            migrationBuilder.DropTable(
                name: "project_skill_builtin_releases",
                schema: "projects_config");

            migrationBuilder.DropColumn(
                name: "capability_declaration_valid",
                schema: "projects_config",
                table: "project_skill_trust_evidence");

            migrationBuilder.DropColumn(
                name: "heuristic_score",
                schema: "projects_config",
                table: "project_skill_trust_evidence");

            migrationBuilder.DropColumn(
                name: "score_status",
                schema: "projects_config",
                table: "project_skill_trust_evidence");

            migrationBuilder.DropColumn(
                name: "short_description",
                schema: "projects_config",
                table: "project_skill_trust_evidence");

            migrationBuilder.DropColumn(
                name: "signature_verified",
                schema: "projects_config",
                table: "project_skill_trust_evidence");

            migrationBuilder.DropColumn(
                name: "trust_tier",
                schema: "projects_config",
                table: "project_skill_trust_evidence");

            migrationBuilder.DropColumn(
                name: "admission_classification",
                schema: "projects_config",
                table: "project_skill_content_revisions");

            migrationBuilder.DropColumn(
                name: "source_authority_kind",
                schema: "projects_config",
                table: "project_skill_content_revisions");
        }
    }
}
