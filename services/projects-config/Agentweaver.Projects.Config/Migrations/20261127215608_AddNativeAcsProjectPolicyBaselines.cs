using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeAcsProjectPolicyBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "native_acs_project_policy_baselines",
                schema: "projects_config",
                columns: table => new
                {
                    baseline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    project_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    policy_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    artifact = table.Column<string>(type: "jsonb", nullable: false),
                    profile_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    approver_issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    approver_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    approver_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approver_membership_revision = table.Column<long>(type: "bigint", nullable: false),
                    approver_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approver_assignment_revision = table.Column<long>(type: "bigint", nullable: false),
                    approval_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    approval_request_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    approval_scope_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    approval_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    approval_correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operator_issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    operator_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    operator_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operator_membership_revision = table.Column<long>(type: "bigint", nullable: true),
                    pin_idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    pin_request_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pin_scope_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    platform_policy_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    platform_policy_revision = table.Column<long>(type: "bigint", nullable: true),
                    platform_content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    pin_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    pin_correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    pinned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    revoked_by_actor_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    revoked_correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumed_revision = table.Column<long>(type: "bigint", nullable: true),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_native_acs_project_policy_baselines", x => x.baseline_id);
                    table.CheckConstraint("ck_native_acs_project_policy_baselines_hashes", "content_sha256 ~ '^[0-9a-f]{64}$' AND approval_request_digest ~ '^[0-9a-f]{64}$' AND approval_scope_digest ~ '^[0-9a-f]{64}$' AND (pin_request_digest IS NULL OR pin_request_digest ~ '^[0-9a-f]{64}$') AND (pin_scope_digest IS NULL OR pin_scope_digest ~ '^[0-9a-f]{64}$') AND (platform_content_sha256 IS NULL OR platform_content_sha256 ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("ck_native_acs_project_policy_baselines_installation", "installation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_native_acs_project_policy_baselines_policy_id", "policy_id = 'canonical'");
                    table.CheckConstraint("ck_native_acs_project_policy_baselines_revisions", "approver_membership_revision > 0 AND approver_assignment_revision > 0 AND (operator_membership_revision IS NULL OR operator_membership_revision > 0) AND (platform_policy_revision IS NULL OR platform_policy_revision > 0) AND (consumed_revision IS NULL OR consumed_revision > 0)");
                    table.CheckConstraint("ck_native_acs_project_policy_baselines_state", "state IN ('Approved', 'Pinned', 'Consumed', 'Revoked')");
                    table.ForeignKey(
                        name: "FK_native_acs_project_policy_baselines_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "projects_config",
                        principalTable: "projects",
                        principalColumn: "project_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_native_acs_project_policy_baselines_active_project",
                schema: "projects_config",
                table: "native_acs_project_policy_baselines",
                column: "project_id",
                unique: true,
                filter: "state IN ('Approved', 'Pinned')");

            migrationBuilder.CreateIndex(
                name: "ux_native_acs_project_policy_baselines_approval_scope",
                schema: "projects_config",
                table: "native_acs_project_policy_baselines",
                column: "approval_scope_digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_native_acs_project_policy_baselines_pin_scope",
                schema: "projects_config",
                table: "native_acs_project_policy_baselines",
                column: "pin_scope_digest",
                unique: true,
                filter: "pin_scope_digest IS NOT NULL");

            migrationBuilder.Sql("""
                CREATE FUNCTION projects_config.guard_native_acs_project_policy_baseline_insert()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW.state <> 'Approved'
                        OR NEW.operator_issuer IS NOT NULL
                        OR NEW.operator_actor_id IS NOT NULL
                        OR NEW.operator_membership_id IS NOT NULL
                        OR NEW.operator_membership_revision IS NOT NULL
                        OR NEW.pin_idempotency_key IS NOT NULL
                        OR NEW.pin_request_digest IS NOT NULL
                        OR NEW.pin_scope_digest IS NOT NULL
                        OR NEW.platform_policy_id IS NOT NULL
                        OR NEW.platform_policy_revision IS NOT NULL
                        OR NEW.platform_content_sha256 IS NOT NULL
                        OR NEW.pin_reason IS NOT NULL
                        OR NEW.pin_correlation_id IS NOT NULL
                        OR NEW.pinned_at IS NOT NULL
                        OR NEW.revoked_by_issuer IS NOT NULL
                        OR NEW.revoked_by_actor_id IS NOT NULL
                        OR NEW.revoked_reason IS NOT NULL
                        OR NEW.revoked_correlation_id IS NOT NULL
                        OR NEW.revoked_at IS NOT NULL
                        OR NEW.consumed_revision IS NOT NULL
                        OR NEW.consumed_at IS NOT NULL THEN
                        RAISE EXCEPTION 'new native ACS project policy baselines must start Approved with no pin, revoke, or consume state'
                            USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER native_acs_project_policy_baselines_insert_guard
                BEFORE INSERT ON projects_config.native_acs_project_policy_baselines
                FOR EACH ROW EXECUTE FUNCTION projects_config.guard_native_acs_project_policy_baseline_insert();

                CREATE TRIGGER native_acs_project_policy_baselines_no_delete
                BEFORE DELETE ON projects_config.native_acs_project_policy_baselines
                FOR EACH ROW EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE TRIGGER native_acs_project_policy_baselines_no_truncate
                BEFORE TRUNCATE ON projects_config.native_acs_project_policy_baselines
                FOR EACH STATEMENT EXECUTE FUNCTION projects_config.reject_immutable_record_mutation();

                CREATE FUNCTION projects_config.guard_native_acs_project_policy_baseline_transition()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW.baseline_id IS DISTINCT FROM OLD.baseline_id
                        OR NEW.installation_id IS DISTINCT FROM OLD.installation_id
                        OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                        OR NEW.project_id IS DISTINCT FROM OLD.project_id
                        OR NEW.policy_id IS DISTINCT FROM OLD.policy_id
                        OR NEW.content_sha256 IS DISTINCT FROM OLD.content_sha256
                        OR NEW.artifact IS DISTINCT FROM OLD.artifact
                        OR NEW.profile_id IS DISTINCT FROM OLD.profile_id
                        OR NEW.approver_issuer IS DISTINCT FROM OLD.approver_issuer
                        OR NEW.approver_actor_id IS DISTINCT FROM OLD.approver_actor_id
                        OR NEW.approver_membership_id IS DISTINCT FROM OLD.approver_membership_id
                        OR NEW.approver_membership_revision IS DISTINCT FROM OLD.approver_membership_revision
                        OR NEW.approver_assignment_id IS DISTINCT FROM OLD.approver_assignment_id
                        OR NEW.approver_assignment_revision IS DISTINCT FROM OLD.approver_assignment_revision
                        OR NEW.approval_idempotency_key IS DISTINCT FROM OLD.approval_idempotency_key
                        OR NEW.approval_request_digest IS DISTINCT FROM OLD.approval_request_digest
                        OR NEW.approval_scope_digest IS DISTINCT FROM OLD.approval_scope_digest
                        OR NEW.approval_reason IS DISTINCT FROM OLD.approval_reason
                        OR NEW.approval_correlation_id IS DISTINCT FROM OLD.approval_correlation_id
                        OR NEW.approved_at IS DISTINCT FROM OLD.approved_at THEN
                        RAISE EXCEPTION 'native ACS project policy baseline approval fields are immutable'
                            USING ERRCODE = '55000';
                    END IF;

                    IF OLD.state = 'Approved' AND NEW.state = 'Pinned' THEN
                        IF NEW.operator_issuer IS NULL OR NEW.operator_actor_id IS NULL
                            OR NEW.operator_membership_id IS NULL OR NEW.operator_membership_revision IS NULL
                            OR NEW.pin_idempotency_key IS NULL OR NEW.pin_request_digest IS NULL
                            OR NEW.pin_scope_digest IS NULL OR NEW.platform_policy_id IS NULL
                            OR NEW.platform_policy_revision IS NULL OR NEW.platform_content_sha256 IS NULL
                            OR NEW.pin_reason IS NULL OR NEW.pin_correlation_id IS NULL
                            OR NEW.pinned_at IS NULL
                            OR NEW.revoked_by_issuer IS DISTINCT FROM OLD.revoked_by_issuer
                            OR NEW.revoked_by_actor_id IS DISTINCT FROM OLD.revoked_by_actor_id
                            OR NEW.revoked_reason IS DISTINCT FROM OLD.revoked_reason
                            OR NEW.revoked_correlation_id IS DISTINCT FROM OLD.revoked_correlation_id
                            OR NEW.revoked_at IS DISTINCT FROM OLD.revoked_at
                            OR NEW.consumed_revision IS DISTINCT FROM OLD.consumed_revision
                            OR NEW.consumed_at IS DISTINCT FROM OLD.consumed_at THEN
                            RAISE EXCEPTION 'invalid native ACS project policy baseline pin transition'
                                USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    ELSIF OLD.state IN ('Approved', 'Pinned') AND NEW.state = 'Revoked' THEN
                        IF NEW.revoked_by_issuer IS NULL OR NEW.revoked_by_actor_id IS NULL
                            OR NEW.revoked_reason IS NULL OR NEW.revoked_correlation_id IS NULL
                            OR NEW.revoked_at IS NULL
                            OR NEW.operator_issuer IS DISTINCT FROM OLD.operator_issuer
                            OR NEW.operator_actor_id IS DISTINCT FROM OLD.operator_actor_id
                            OR NEW.operator_membership_id IS DISTINCT FROM OLD.operator_membership_id
                            OR NEW.operator_membership_revision IS DISTINCT FROM OLD.operator_membership_revision
                            OR NEW.pin_idempotency_key IS DISTINCT FROM OLD.pin_idempotency_key
                            OR NEW.pin_request_digest IS DISTINCT FROM OLD.pin_request_digest
                            OR NEW.pin_scope_digest IS DISTINCT FROM OLD.pin_scope_digest
                            OR NEW.platform_policy_id IS DISTINCT FROM OLD.platform_policy_id
                            OR NEW.platform_policy_revision IS DISTINCT FROM OLD.platform_policy_revision
                            OR NEW.platform_content_sha256 IS DISTINCT FROM OLD.platform_content_sha256
                            OR NEW.pin_reason IS DISTINCT FROM OLD.pin_reason
                            OR NEW.pin_correlation_id IS DISTINCT FROM OLD.pin_correlation_id
                            OR NEW.pinned_at IS DISTINCT FROM OLD.pinned_at
                            OR NEW.consumed_revision IS DISTINCT FROM OLD.consumed_revision
                            OR NEW.consumed_at IS DISTINCT FROM OLD.consumed_at THEN
                            RAISE EXCEPTION 'invalid native ACS project policy baseline revoke transition'
                                USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    ELSIF OLD.state = 'Pinned' AND NEW.state = 'Consumed' THEN
                        IF NEW.consumed_revision IS NULL OR NEW.consumed_at IS NULL
                            OR NEW.operator_issuer IS DISTINCT FROM OLD.operator_issuer
                            OR NEW.operator_actor_id IS DISTINCT FROM OLD.operator_actor_id
                            OR NEW.operator_membership_id IS DISTINCT FROM OLD.operator_membership_id
                            OR NEW.operator_membership_revision IS DISTINCT FROM OLD.operator_membership_revision
                            OR NEW.pin_idempotency_key IS DISTINCT FROM OLD.pin_idempotency_key
                            OR NEW.pin_request_digest IS DISTINCT FROM OLD.pin_request_digest
                            OR NEW.pin_scope_digest IS DISTINCT FROM OLD.pin_scope_digest
                            OR NEW.platform_policy_id IS DISTINCT FROM OLD.platform_policy_id
                            OR NEW.platform_policy_revision IS DISTINCT FROM OLD.platform_policy_revision
                            OR NEW.platform_content_sha256 IS DISTINCT FROM OLD.platform_content_sha256
                            OR NEW.pin_reason IS DISTINCT FROM OLD.pin_reason
                            OR NEW.pin_correlation_id IS DISTINCT FROM OLD.pin_correlation_id
                            OR NEW.pinned_at IS DISTINCT FROM OLD.pinned_at
                            OR NEW.revoked_by_issuer IS DISTINCT FROM OLD.revoked_by_issuer
                            OR NEW.revoked_by_actor_id IS DISTINCT FROM OLD.revoked_by_actor_id
                            OR NEW.revoked_reason IS DISTINCT FROM OLD.revoked_reason
                            OR NEW.revoked_correlation_id IS DISTINCT FROM OLD.revoked_correlation_id
                            OR NEW.revoked_at IS DISTINCT FROM OLD.revoked_at THEN
                            RAISE EXCEPTION 'invalid native ACS project policy baseline consume transition'
                                USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    ELSE
                        RAISE EXCEPTION 'invalid native ACS project policy baseline state transition'
                            USING ERRCODE = '55000';
                    END IF;
                END;
                $$;

                CREATE TRIGGER native_acs_project_policy_baselines_transition_guard
                BEFORE UPDATE ON projects_config.native_acs_project_policy_baselines
                FOR EACH ROW EXECUTE FUNCTION projects_config.guard_native_acs_project_policy_baseline_transition();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS native_acs_project_policy_baselines_transition_guard
                    ON projects_config.native_acs_project_policy_baselines;
                DROP FUNCTION IF EXISTS projects_config.guard_native_acs_project_policy_baseline_transition();
                DROP TRIGGER IF EXISTS native_acs_project_policy_baselines_no_truncate
                    ON projects_config.native_acs_project_policy_baselines;
                DROP TRIGGER IF EXISTS native_acs_project_policy_baselines_no_delete
                    ON projects_config.native_acs_project_policy_baselines;
                DROP TRIGGER IF EXISTS native_acs_project_policy_baselines_insert_guard
                    ON projects_config.native_acs_project_policy_baselines;
                DROP FUNCTION IF EXISTS projects_config.guard_native_acs_project_policy_baseline_insert();
                """);
            migrationBuilder.DropTable(
                name: "native_acs_project_policy_baselines",
                schema: "projects_config");
        }
    }
}
