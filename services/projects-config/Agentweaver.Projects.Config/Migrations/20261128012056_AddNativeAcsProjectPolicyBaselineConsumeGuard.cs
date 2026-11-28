using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddNativeAcsProjectPolicyBaselineConsumeGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION projects_config.guard_native_acs_project_policy_baseline_transition()
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
                        IF NEW.consumed_revision IS DISTINCT FROM 1 OR NEW.consumed_at IS NULL
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
                            OR NEW.revoked_at IS DISTINCT FROM OLD.revoked_at
                            OR NOT EXISTS (
                                SELECT 1
                                FROM projects_config.native_acs_policy_heads AS head
                                JOIN projects_config.native_acs_policy_revisions AS revision
                                  ON revision.scope_type = head.scope_type
                                 AND revision.scope_id = head.scope_id
                                 AND revision.policy_id = head.policy_id
                                 AND revision.revision = head.current_revision
                                WHERE head.scope_type = 'Project'
                                  AND head.scope_id = OLD.project_id
                                  AND head.policy_id = 'canonical'
                                  AND head.current_revision = 1
                                  AND revision.revision = 1) THEN
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
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION projects_config.guard_native_acs_project_policy_baseline_transition()
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
                """);
        }
    }
}
