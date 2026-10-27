using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentweaver.Projects.Config.Migrations
{
    /// <inheritdoc />
    public partial class AddP1ProjectSetupFunctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE FUNCTION projects_config.read_p1_project(
                    p_invocation_id uuid,
                    p_issuer text,
                    p_subject text,
                    p_tenant_id text,
                    p_membership_id uuid,
                    p_expected_membership_revision bigint,
                    p_tenant_admin_assignment_id uuid,
                    p_expected_tenant_admin_assignment_revision bigint)
                RETURNS jsonb
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $function$
                DECLARE
                    v_operation projects_config.project_setup_operations%ROWTYPE;
                    v_project projects_config.projects%ROWTYPE;
                    v_assignment projects_config.project_role_assignments%ROWTYPE;
                    v_owner_assignment projects_config.project_role_assignments%ROWTYPE;
                    v_authorized text;
                    v_request_material jsonb;
                    v_expected_request_hash text;
                    v_configuration_exists boolean;
                    v_authority_audit_exists boolean;
                    v_owner_authority_audit_exists boolean;
                BEGIN
                    IF p_invocation_id IS NULL OR p_invocation_id = '00000000-0000-0000-0000-000000000000'::uuid
                       OR p_issuer IS NULL OR length(p_issuer) > 512
                       OR p_subject IS NULL OR length(p_subject) NOT BETWEEN 1 AND 256
                       OR p_subject !~ '^[A-Za-z0-9._:-]+$'
                       OR p_tenant_id IS NULL OR length(p_tenant_id) NOT BETWEEN 1 AND 256
                       OR p_tenant_id !~ '^[A-Za-z0-9._:-]+$'
                       OR p_membership_id IS NULL
                       OR p_expected_membership_revision IS NULL OR p_expected_membership_revision <= 0
                       OR p_tenant_admin_assignment_id IS NULL
                       OR p_expected_tenant_admin_assignment_revision IS NULL
                       OR p_expected_tenant_admin_assignment_revision <= 0 THEN
                        RAISE EXCEPTION 'invalid P1 project setup read arguments'
                            USING ERRCODE = '22023';
                    END IF;

                    SELECT operation.*
                    INTO v_operation
                    FROM projects_config.project_setup_operations AS operation
                    WHERE operation.invocation_id = p_invocation_id
                      AND operation.operation_kind = 'Setup';
                    IF NOT FOUND
                       OR v_operation.issuer IS DISTINCT FROM p_issuer
                       OR v_operation.subject IS DISTINCT FROM p_subject
                       OR v_operation.tenant_id IS DISTINCT FROM p_tenant_id
                       OR v_operation.membership_id IS DISTINCT FROM p_membership_id
                       OR v_operation.membership_revision IS DISTINCT FROM p_expected_membership_revision THEN
                        RAISE EXCEPTION 'P1 project setup receipt is unavailable to this caller'
                            USING ERRCODE = '42501';
                    END IF;

                    v_request_material := jsonb_build_object(
                        'version', 1,
                        'operationKind', 'setup',
                        'invocationId', p_invocation_id::text,
                        'tenantId', p_tenant_id,
                        'issuer', p_issuer,
                        'subject', p_subject,
                        'membershipId', p_membership_id::text,
                        'membershipRevision', p_expected_membership_revision,
                        'tenantAdminAssignmentId', p_tenant_admin_assignment_id::text,
                        'tenantAdminAssignmentRevision', p_expected_tenant_admin_assignment_revision,
                        'name', 'p1-' || p_invocation_id::text,
                        'expectedProjectRevision', NULL,
                        'expectedOrchestratorAssignmentRevision', NULL);
                    v_expected_request_hash := pg_catalog.encode(
                        pg_catalog.sha256(pg_catalog.convert_to(v_request_material::text, 'UTF8')),
                        'hex');
                    IF v_operation.request_hash IS DISTINCT FROM v_expected_request_hash THEN
                        RAISE EXCEPTION 'P1 project setup receipt is bound to another authority revision'
                            USING ERRCODE = '42501';
                    END IF;

                    IF v_operation.result_state IS DISTINCT FROM 'Created'
                       OR v_operation.expected_project_revision IS NOT NULL
                       OR v_operation.expected_assignment_revision IS NOT NULL
                       OR EXISTS (
                           SELECT 1
                           FROM projects_config.project_setup_operations AS cleanup
                           WHERE cleanup.invocation_id = p_invocation_id
                             AND cleanup.operation_kind = 'Cleanup') THEN
                        RAISE EXCEPTION 'P1 project setup receipt is no longer current'
                            USING ERRCODE = '55000';
                    END IF;

                    SELECT projects_config.lock_casting_authority(
                        p_membership_id,
                        p_issuer,
                        p_subject,
                        p_tenant_id,
                        p_expected_membership_revision,
                        v_operation.project_id,
                        true)
                    INTO v_authorized;
                    IF v_authorized IS DISTINCT FROM 'authorized' THEN
                        RAISE EXCEPTION 'P1 project setup authority is no longer current'
                            USING ERRCODE = '42501';
                    END IF;

                    PERFORM 1
                    FROM projects_config.project_role_assignments AS admin
                    WHERE admin.assignment_id = p_tenant_admin_assignment_id
                      AND admin.membership_id = p_membership_id
                      AND admin.resource_type = 'Tenant'
                      AND admin.resource_id = p_tenant_id
                      AND admin.role = 'TenantAdmin'
                      AND admin.state = 'Active'
                      AND admin.revision = p_expected_tenant_admin_assignment_revision
                    FOR SHARE;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'P1 project setup TenantAdmin assignment is no longer current'
                            USING ERRCODE = '42501';
                    END IF;

                    IF v_operation.owner_assignment_id IS NULL
                       OR v_operation.owner_assignment_revision IS NULL
                       OR v_operation.authority_audit_event_id IS NULL
                       OR v_operation.owner_authority_audit_event_id IS NULL
                       OR v_operation.project_revision <= 0
                       OR v_operation.project_configuration_revision <= 0
                       OR v_operation.assignment_revision <= 0
                       OR v_operation.owner_assignment_revision <= 0
                       OR v_operation.request_hash !~ '^[0-9a-f]{64}$'
                       OR v_operation.result->>'invocationId' IS DISTINCT FROM p_invocation_id::text
                       OR v_operation.result->>'issuer' IS DISTINCT FROM p_issuer
                       OR v_operation.result->>'subject' IS DISTINCT FROM p_subject
                       OR v_operation.result->>'tenantId' IS DISTINCT FROM p_tenant_id
                       OR v_operation.result->>'membershipId' IS DISTINCT FROM p_membership_id::text
                       OR v_operation.result->>'membershipRevision' IS DISTINCT FROM v_operation.membership_revision::text
                       OR v_operation.result->>'projectId' IS DISTINCT FROM v_operation.project_id
                       OR v_operation.result->>'createdByActorId' IS DISTINCT FROM p_subject
                       OR v_operation.result->>'projectRevision' IS DISTINCT FROM v_operation.project_revision::text
                       OR v_operation.result->>'projectConfigurationRevision' IS DISTINCT FROM v_operation.project_configuration_revision::text
                       OR v_operation.result->>'assignmentId' IS DISTINCT FROM v_operation.assignment_id::text
                       OR v_operation.result->>'assignmentRevision' IS DISTINCT FROM v_operation.assignment_revision::text
                       OR v_operation.result->>'authorityAuditEventId' IS DISTINCT FROM v_operation.authority_audit_event_id::text
                       OR v_operation.result->>'ownerAssignmentId' IS DISTINCT FROM v_operation.owner_assignment_id::text
                       OR v_operation.result->>'ownerAssignmentRevision' IS DISTINCT FROM v_operation.owner_assignment_revision::text
                       OR v_operation.result->>'ownerAuthorityAuditEventId' IS DISTINCT FROM v_operation.owner_authority_audit_event_id::text THEN
                        RAISE EXCEPTION 'P1 project setup receipt does not match its immutable binding'
                            USING ERRCODE = '55000';
                    END IF;

                    SELECT project.*
                    INTO v_project
                    FROM projects_config.projects AS project
                    WHERE project.project_id = v_operation.project_id
                      AND project.tenant_id = p_tenant_id
                      AND project.created_by_actor_id = p_subject
                      AND project.state = 'Active'
                      AND project.revision = v_operation.project_revision
                      AND project.configuration_revision = v_operation.project_configuration_revision;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'P1 project setup project readback is stale'
                            USING ERRCODE = '55000';
                    END IF;

                    SELECT assignment.*
                    INTO v_assignment
                    FROM projects_config.project_role_assignments AS assignment
                    WHERE assignment.assignment_id = v_operation.assignment_id
                      AND assignment.membership_id = p_membership_id
                      AND assignment.resource_type = 'Project'
                      AND assignment.resource_id = v_operation.project_id
                      AND assignment.role = 'Orchestrator'
                      AND assignment.state = 'Active'
                      AND assignment.revision = v_operation.assignment_revision;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'P1 project setup Orchestrator assignment readback is stale'
                            USING ERRCODE = '55000';
                    END IF;

                    SELECT assignment.*
                    INTO v_owner_assignment
                    FROM projects_config.project_role_assignments AS assignment
                    WHERE assignment.assignment_id = v_operation.owner_assignment_id
                      AND assignment.membership_id = p_membership_id
                      AND assignment.resource_type = 'Project'
                      AND assignment.resource_id = v_operation.project_id
                      AND assignment.role = 'Owner'
                      AND assignment.state = 'Active'
                      AND assignment.revision = v_operation.owner_assignment_revision;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'P1 project setup Owner assignment readback is stale'
                            USING ERRCODE = '55000';
                    END IF;

                    SELECT EXISTS (
                        SELECT 1
                        FROM projects_config.project_configuration_revisions AS configuration
                        WHERE configuration.project_id = v_operation.project_id
                          AND configuration.revision = v_operation.project_configuration_revision)
                    INTO v_configuration_exists;
                    SELECT EXISTS (
                        SELECT 1
                        FROM projects_config.authority_audit AS audit
                        WHERE audit.event_id = v_operation.authority_audit_event_id
                          AND audit.invocation_id = p_invocation_id
                          AND audit.assignment_id = v_operation.assignment_id
                          AND audit.membership_id = p_membership_id
                          AND audit.tenant_id = p_tenant_id
                          AND audit.resource_type = 'Project'
                          AND audit.resource_id = v_operation.project_id
                          AND audit.role = 'Orchestrator'
                          AND audit.event_type = 'role_assigned'
                          AND audit.revision = v_operation.assignment_revision
                          AND audit.actor = p_subject)
                    INTO v_authority_audit_exists;
                    SELECT EXISTS (
                        SELECT 1
                        FROM projects_config.authority_audit AS audit
                        WHERE audit.event_id = v_operation.owner_authority_audit_event_id
                          AND audit.invocation_id = p_invocation_id
                          AND audit.assignment_id = v_operation.owner_assignment_id
                          AND audit.membership_id = p_membership_id
                          AND audit.tenant_id = p_tenant_id
                          AND audit.resource_type = 'Project'
                          AND audit.resource_id = v_operation.project_id
                          AND audit.role = 'Owner'
                          AND audit.event_type = 'role_assigned'
                          AND audit.revision = v_operation.owner_assignment_revision
                          AND audit.actor = p_subject)
                    INTO v_owner_authority_audit_exists;

                    IF NOT v_configuration_exists
                       OR NOT v_authority_audit_exists
                       OR NOT v_owner_authority_audit_exists THEN
                        RAISE EXCEPTION 'P1 project setup audit or initial configuration readback is stale'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN jsonb_build_object(
                        'receipt', v_operation.result,
                        'readback', jsonb_build_object(
                            'projectExists', true,
                            'projectState', 'active',
                            'projectRevision', v_project.revision,
                            'projectConfigurationRevision', v_project.configuration_revision,
                            'initialConfigurationExists', v_configuration_exists,
                            'assignmentExists', true,
                            'assignmentState', 'active',
                            'assignmentRevision', v_assignment.revision,
                            'authorityAuditExists', v_authority_audit_exists,
                            'ownerAssignmentId', v_owner_assignment.assignment_id,
                            'ownerAssignmentExists', true,
                            'ownerAssignmentState', 'active',
                            'ownerAssignmentRevision', v_owner_assignment.revision,
                            'ownerAuthorityAuditExists', v_owner_authority_audit_exists));
                END;
                $function$;

                CREATE FUNCTION projects_config.issue_p1_project(
                    p_invocation_id uuid,
                    p_issuer text,
                    p_subject text,
                    p_tenant_id text,
                    p_membership_id uuid,
                    p_expected_membership_revision bigint,
                    p_tenant_admin_assignment_id uuid,
                    p_expected_tenant_admin_assignment_revision bigint)
                RETURNS jsonb
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $function$
                DECLARE
                    v_existing projects_config.project_setup_operations%ROWTYPE;
                    v_cleanup_exists boolean;
                    v_project_id text;
                    v_assignment_id uuid;
                    v_owner_assignment_id uuid;
                    v_authority_audit_event_id uuid;
                    v_owner_authority_audit_event_id uuid;
                    v_request_hash text;
                    v_request_material jsonb;
                    v_configuration jsonb;
                    v_receipt jsonb;
                    v_authorized text;
                    v_now timestamptz;
                BEGIN
                    IF p_invocation_id IS NULL OR p_invocation_id = '00000000-0000-0000-0000-000000000000'::uuid
                       OR p_issuer IS NULL OR length(p_issuer) > 512
                       OR p_subject IS NULL OR length(p_subject) NOT BETWEEN 1 AND 256
                       OR p_subject !~ '^[A-Za-z0-9._:-]+$'
                       OR p_tenant_id IS NULL OR length(p_tenant_id) NOT BETWEEN 1 AND 256
                       OR p_tenant_id !~ '^[A-Za-z0-9._:-]+$'
                       OR p_membership_id IS NULL
                       OR p_expected_membership_revision IS NULL OR p_expected_membership_revision <= 0
                       OR p_tenant_admin_assignment_id IS NULL
                       OR p_expected_tenant_admin_assignment_revision IS NULL
                       OR p_expected_tenant_admin_assignment_revision <= 0 THEN
                        RAISE EXCEPTION 'invalid P1 project setup issue arguments'
                            USING ERRCODE = '22023';
                    END IF;

                    PERFORM pg_catalog.pg_advisory_xact_lock(
                        pg_catalog.hashtextextended(p_invocation_id::text, 0));

                    v_request_material := jsonb_build_object(
                        'version', 1,
                        'operationKind', 'setup',
                        'invocationId', p_invocation_id::text,
                        'tenantId', p_tenant_id,
                        'issuer', p_issuer,
                        'subject', p_subject,
                        'membershipId', p_membership_id::text,
                        'membershipRevision', p_expected_membership_revision,
                        'tenantAdminAssignmentId', p_tenant_admin_assignment_id::text,
                        'tenantAdminAssignmentRevision', p_expected_tenant_admin_assignment_revision,
                        'name', 'p1-' || p_invocation_id::text,
                        'expectedProjectRevision', NULL,
                        'expectedOrchestratorAssignmentRevision', NULL);
                    v_request_hash := pg_catalog.encode(
                        pg_catalog.sha256(pg_catalog.convert_to(v_request_material::text, 'UTF8')),
                        'hex');

                    SELECT operation.*
                    INTO v_existing
                    FROM projects_config.project_setup_operations AS operation
                    WHERE operation.invocation_id = p_invocation_id
                      AND operation.operation_kind = 'Setup';
                    IF FOUND THEN
                        IF v_existing.request_hash IS DISTINCT FROM v_request_hash
                           OR v_existing.issuer IS DISTINCT FROM p_issuer
                           OR v_existing.subject IS DISTINCT FROM p_subject
                           OR v_existing.tenant_id IS DISTINCT FROM p_tenant_id
                           OR v_existing.membership_id IS DISTINCT FROM p_membership_id
                           OR v_existing.membership_revision IS DISTINCT FROM p_expected_membership_revision THEN
                            RAISE EXCEPTION 'P1 invocation already has a different setup owner or request'
                                USING ERRCODE = '23505';
                        END IF;

                        RETURN projects_config.read_p1_project(
                            p_invocation_id,
                            p_issuer,
                            p_subject,
                            p_tenant_id,
                            p_membership_id,
                            p_expected_membership_revision,
                            p_tenant_admin_assignment_id,
                            p_expected_tenant_admin_assignment_revision);
                    END IF;

                    SELECT EXISTS (
                        SELECT 1
                        FROM projects_config.project_setup_operations AS cleanup
                        WHERE cleanup.invocation_id = p_invocation_id
                          AND cleanup.operation_kind = 'Cleanup')
                    INTO v_cleanup_exists;
                    IF v_cleanup_exists THEN
                        RAISE EXCEPTION 'P1 invocation already has a cleanup receipt without its setup receipt'
                            USING ERRCODE = '55000';
                    END IF;

                    v_project_id := replace(pg_catalog.gen_random_uuid()::text, '-', '');
                    SELECT projects_config.lock_casting_authority(
                        p_membership_id,
                        p_issuer,
                        p_subject,
                        p_tenant_id,
                        p_expected_membership_revision,
                        v_project_id,
                        true)
                    INTO v_authorized;
                    IF v_authorized IS DISTINCT FROM 'authorized' THEN
                        RAISE EXCEPTION 'P1 project setup authority is no longer current'
                            USING ERRCODE = '42501';
                    END IF;

                    PERFORM 1
                    FROM projects_config.project_role_assignments AS admin
                    WHERE admin.assignment_id = p_tenant_admin_assignment_id
                      AND admin.membership_id = p_membership_id
                      AND admin.resource_type = 'Tenant'
                      AND admin.resource_id = p_tenant_id
                      AND admin.role = 'TenantAdmin'
                      AND admin.state = 'Active'
                      AND admin.revision = p_expected_tenant_admin_assignment_revision
                    FOR SHARE;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'P1 project setup TenantAdmin assignment is no longer current'
                            USING ERRCODE = '42501';
                    END IF;

                    v_now := pg_catalog.clock_timestamp();
                    v_assignment_id := pg_catalog.gen_random_uuid();
                    v_owner_assignment_id := pg_catalog.gen_random_uuid();
                    v_authority_audit_event_id := pg_catalog.gen_random_uuid();
                    v_owner_authority_audit_event_id := pg_catalog.gen_random_uuid();
                    v_configuration := jsonb_build_object(
                        'modelSelection', NULL,
                        'modelTaskOverrides', jsonb_build_array(),
                        'modelRoleOverrides', jsonb_build_array(),
                        'modelAgentOverrides', jsonb_build_array(),
                        'providerOverrides', jsonb_build_array(),
                        'orderedProviderOverrides', jsonb_build_array(),
                        'agentCharters', jsonb_build_array(),
                        'casting', jsonb_build_array(),
                        'blueprintWorkflowReferences', jsonb_build_array(),
                        'defaultWorkflowId', NULL,
                        'skills', jsonb_build_array(),
                        'egressNarrowing', NULL,
                        'runLimits', jsonb_build_object(
                            'maxModelTurns', NULL,
                            'maxToolCalls', NULL,
                            'maxChildren', NULL,
                            'maxConcurrentChildren', NULL,
                            'maxWallTimeSeconds', NULL,
                            'maxPromptTokens', NULL));

                    INSERT INTO projects_config.projects (
                        project_id, tenant_id, created_by_actor_id, name, state,
                        revision, configuration_revision, created_at, updated_at)
                    VALUES (
                        v_project_id, p_tenant_id, p_subject,
                        'p1-' || p_invocation_id::text, 'Active',
                        1, 1, v_now, v_now);

                    INSERT INTO projects_config.project_configuration_revisions (
                        project_id, revision, configuration, updated_by_actor_id, created_at)
                    VALUES (v_project_id, 1, v_configuration, p_subject, v_now);

                    INSERT INTO projects_config.project_role_assignments (
                        assignment_id, membership_id, resource_type, resource_id, role,
                        state, revision, granted_by, granted_at)
                    VALUES
                        (v_assignment_id, p_membership_id, 'Project', v_project_id, 'Orchestrator',
                         'Active', 1, p_subject, v_now),
                        (v_owner_assignment_id, p_membership_id, 'Project', v_project_id, 'Owner',
                         'Active', 1, p_subject, v_now);

                    INSERT INTO projects_config.authority_audit (
                        event_id, invocation_id, event_type, membership_id, assignment_id,
                        issuer, subject, tenant_id, resource_type, resource_id, role,
                        revision, actor, created_at)
                    VALUES
                        (v_authority_audit_event_id, p_invocation_id, 'role_assigned',
                         p_membership_id, v_assignment_id, p_issuer, p_subject, p_tenant_id,
                         'Project', v_project_id, 'Orchestrator', 1, p_subject, v_now),
                        (v_owner_authority_audit_event_id, p_invocation_id, 'role_assigned',
                         p_membership_id, v_owner_assignment_id, p_issuer, p_subject, p_tenant_id,
                         'Project', v_project_id, 'Owner', 1, p_subject, v_now);

                    v_receipt := jsonb_build_object(
                        'invocationId', p_invocation_id,
                        'issuer', p_issuer,
                        'subject', p_subject,
                        'tenantId', p_tenant_id,
                        'membershipId', p_membership_id,
                        'membershipRevision', p_expected_membership_revision,
                        'projectId', v_project_id,
                        'createdByActorId', p_subject,
                        'projectRevision', 1,
                        'projectConfigurationRevision', 1,
                        'assignmentId', v_assignment_id,
                        'assignmentRevision', 1,
                        'authorityAuditEventId', v_authority_audit_event_id,
                        'createdAt', v_now,
                        'ownerAssignmentId', v_owner_assignment_id,
                        'ownerAssignmentRevision', 1,
                        'ownerAuthorityAuditEventId', v_owner_authority_audit_event_id);

                    INSERT INTO projects_config.project_setup_operations (
                        invocation_id, operation_kind, request_hash, issuer, subject, tenant_id,
                        membership_id, membership_revision, project_id, project_revision,
                        project_configuration_revision, assignment_id, assignment_revision,
                        expected_project_revision, expected_assignment_revision,
                        authority_audit_event_id, result_state, result, created_at,
                        owner_assignment_id, owner_assignment_revision,
                        owner_authority_audit_event_id, owner_revocation_audit_event_id)
                    VALUES (
                        p_invocation_id, 'Setup', v_request_hash, p_issuer, p_subject, p_tenant_id,
                        p_membership_id, p_expected_membership_revision, v_project_id, 1, 1,
                        v_assignment_id, 1, NULL, NULL, v_authority_audit_event_id, 'Created',
                        v_receipt, v_now, v_owner_assignment_id, 1,
                        v_owner_authority_audit_event_id, NULL);

                    RETURN projects_config.read_p1_project(
                        p_invocation_id,
                        p_issuer,
                        p_subject,
                        p_tenant_id,
                        p_membership_id,
                        p_expected_membership_revision,
                        p_tenant_admin_assignment_id,
                        p_expected_tenant_admin_assignment_revision);
                END;
                $function$;

                REVOKE ALL ON FUNCTION projects_config.issue_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint) FROM PUBLIC;
                REVOKE ALL ON FUNCTION projects_config.read_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS projects_config.issue_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint);
                DROP FUNCTION IF EXISTS projects_config.read_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint);
                """);
        }
    }
}
