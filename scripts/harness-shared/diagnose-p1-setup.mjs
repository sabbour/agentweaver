import { pathToFileURL } from 'node:url';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

/**
 * Generate an invocation-bound, read-only snapshot query, without connecting or acquiring credentials.
 * Absence is an observation, not permission to retry: an earlier exec can still be in flight.
 */
export function createP1SetupDiagnosisQuery(invocationId) {
  if (!UUID.test(invocationId ?? '')) throw new Error('A valid P1 invocation UUID is required.');
  const id = invocationId.toLowerCase();
  return `BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '15s';
WITH operations AS (
  SELECT * FROM projects_config.project_setup_operations
  WHERE invocation_id = '${id}'::uuid
), fixtures AS (
  SELECT p.* FROM projects_config.projects p
  WHERE p.name = 'p1-${id}'
     OR p.project_id IN (SELECT project_id FROM operations)
), grants AS (
  SELECT a.* FROM projects_config.project_role_assignments a
  WHERE (a.resource_type = 'Project' AND a.resource_id IN (SELECT project_id FROM fixtures))
     OR a.assignment_id IN (SELECT assignment_id FROM operations)
     OR a.assignment_id IN (SELECT owner_assignment_id FROM operations)
), selections AS (
  SELECT s.* FROM projects_config.project_run_selections s
  WHERE s.project_id IN (SELECT project_id FROM fixtures)
)
SELECT jsonb_build_object(
  'schemaVersion', 1,
  'invocationId', '${id}',
  'observedAt', transaction_timestamp(),
  'fixtureState', CASE
    WHEN NOT EXISTS (SELECT FROM operations) AND NOT EXISTS (SELECT FROM fixtures)
      AND NOT EXISTS (SELECT FROM grants) AND NOT EXISTS (SELECT FROM selections)
      THEN 'not-observed'
    ELSE 'fixtures-observed'
  END,
  'operations', COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'operationKind', operation_kind, 'resultState', result_state,
    'projectId', project_id, 'tenantId', tenant_id, 'issuer', issuer, 'subject', subject,
    'membershipId', membership_id, 'membershipRevision', membership_revision,
    'projectRevision', project_revision, 'projectConfigurationRevision', project_configuration_revision,
    'assignmentId', assignment_id, 'assignmentRevision', assignment_revision,
    'ownerAssignmentId', owner_assignment_id, 'ownerAssignmentRevision', owner_assignment_revision,
    'authorityAuditEventId', authority_audit_event_id,
    'ownerAuthorityAuditEventId', owner_authority_audit_event_id,
    'ownerRevocationAuditEventId', owner_revocation_audit_event_id,
    'createdAt', created_at
  ) ORDER BY operation_kind) FROM operations), '[]'::jsonb),
  'projects', COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'projectId', project_id, 'tenantId', tenant_id, 'createdByActorId', created_by_actor_id,
    'name', name, 'state', state, 'revision', revision,
    'configurationRevision', configuration_revision
  ) ORDER BY project_id) FROM fixtures), '[]'::jsonb),
  'grants', COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'assignmentId', assignment_id, 'membershipId', membership_id,
    'resourceType', resource_type, 'resourceId', resource_id,
    'role', role, 'state', state, 'revision', revision
  ) ORDER BY assignment_id) FROM grants), '[]'::jsonb),
  'selections', COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'projectId', project_id, 'runId', run_id
  ) ORDER BY project_id, run_id) FROM selections), '[]'::jsonb),
  'retryAuthorized', false
);
ROLLBACK;
`;
}

/** Print only the safe query or fixed usage errors; never read credentials or run setup/cleanup. */
export function runP1SetupDiagnosisCli(argv = process.argv.slice(2), io = console) {
  if (argv.length !== 2 || argv[0] !== '--invocation-id') {
    io.error('Usage: node scripts\\harness-shared\\diagnose-p1-setup.mjs --invocation-id <UUID>');
    return 1;
  }
  try {
    io.log(createP1SetupDiagnosisQuery(argv[1]));
    return 0;
  } catch {
    io.error('A valid P1 invocation UUID is required.');
    return 1;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runP1SetupDiagnosisCli();
}
