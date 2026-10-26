import { redact } from './redaction.mjs';
import { runP1CleanupOperations } from './p1-cleanup-operations.mjs';
import { p1FixtureProjectName } from './p1-fixture-setup.mjs';
import { reconcileP1Run } from './p1-reconcile.mjs';

const INVOCATION_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const SETUP_ROLES = ['owner', 'orchestrator'];

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function hasPermission(context, resourceType, resourceId, permissionName) {
  return context?.effectiveAuthority?.some(resource =>
    resource?.resourceType === resourceType
    && resource.resourceId === resourceId
    && resource.permissions?.some(permission =>
      typeof permission?.permission === 'string'
      && permission.permission.toLowerCase() === permissionName.toLowerCase())) === true;
}

function cleanupTarget(evidence, invocationId, environment) {
  const expectedInvocationId = invocationId.toLowerCase();
  const context = evidence?.effectiveAuthority?.context;
  if (!isRecord(evidence) || evidence.kind !== 'p1-run-reconciliation'
    || evidence.schemaVersion !== 1 || evidence.mode !== 'read-only-assessment'
    || evidence.invocationId !== expectedInvocationId
    || evidence.verdict !== 'safe to clean up'
    || evidence.verdictReasonCode !== 'positive_active_setup_evidence'
    || evidence.actions?.assessmentOnly !== true
    || evidence.actions?.writeRequestsIssued !== false
    || evidence.actions?.cleanupPerformed !== false
    || evidence.actions?.fixtureWritesPerformed !== false
    || evidence.actions?.grantsChanged !== false
    || evidence.actions?.newJourneyStarted !== false
    || evidence.projectName !== p1FixtureProjectName(expectedInvocationId)
    || evidence.projectMatch?.status !== 'found'
    || !Array.isArray(evidence.projectMatch.matches)
    || evidence.projectMatch.matches.length !== 1
    || evidence.effectiveAuthority?.status !== 'complete'
    || context?.contractVersion !== 1
    || context.actorId !== environment.actorId
    || context.issuer !== environment.issuer
    || context.tenantId !== environment.tenantId
    || !Number.isSafeInteger(context.membershipRevision) || context.membershipRevision < 1
    || context.boundProjectId != null || context.boundRunId != null
    || evidence.roleAssignments?.status !== 'complete'
    || evidence.roleAssignments.complete !== true
    || !Array.isArray(evidence.roleAssignments.assignments)
    || evidence.auditRecords?.status !== 'complete'
    || evidence.auditRecords.complete !== true
    || evidence.auditRecords.truncated !== false
    || !Array.isArray(evidence.auditRecords.records)
    || evidence.auditRecords.records.length !== 2) {
    return null;
  }

  const project = evidence.projectMatch.matches[0];
  if (!isRecord(project) || project.name !== p1FixtureProjectName(expectedInvocationId)
    || project.state !== 'active' || typeof project.projectId !== 'string'
    || !project.projectId || !Number.isSafeInteger(project.revision) || project.revision < 1
    || !Number.isSafeInteger(project.configurationRevision) || project.configurationRevision < 1
    || evidence.roleAssignments.projectId !== project.projectId
    || evidence.roleAssignments.assignments.length !== 2) return null;

  const assignmentsByRole = new Map();
  for (const assignment of evidence.roleAssignments.assignments) {
    if (!isRecord(assignment) || !SETUP_ROLES.includes(assignment.role)
      || assignmentsByRole.has(assignment.role)
      || assignment.principalId !== environment.actorId
      || assignment.createdBy !== environment.actorId
      || !INVOCATION_ID.test(assignment.assignmentId ?? '')
      || assignment.revision !== 1
      || typeof assignment.createdAt !== 'string' || assignment.createdAt.length === 0) return null;
    assignmentsByRole.set(assignment.role, assignment);
  }
  if (assignmentsByRole.size !== SETUP_ROLES.length) return null;

  const auditByAssignment = new Map();
  for (const record of evidence.auditRecords.records) {
    if (!isRecord(record) || record.eventType !== 'role_assigned'
      || record.resourceType !== 'project' || record.resourceId !== project.projectId
      || record.actor !== environment.actorId || record.revision !== 1
      || !INVOCATION_ID.test(record.eventId ?? '')
      || !INVOCATION_ID.test(record.assignmentId ?? '')
      || typeof record.createdAt !== 'string' || record.createdAt.length === 0
      || auditByAssignment.has(record.assignmentId.toLowerCase())) {
      return null;
    }
    auditByAssignment.set(record.assignmentId.toLowerCase(), record);
  }

  const assignments = {};
  for (const role of SETUP_ROLES) {
    const assignment = assignmentsByRole.get(role);
    const audit = auditByAssignment.get(assignment.assignmentId.toLowerCase());
    if (!audit || audit.role !== role) return null;
    assignments[role] = {
      assignmentId: assignment.assignmentId,
      revision: assignment.revision,
    };
  }
  if (auditByAssignment.size !== assignmentsByRole.size) return null;

  return {
    actorId: environment.actorId,
    projectId: project.projectId,
    projectRevision: project.revision,
    projectConfigurationRevision: project.configurationRevision,
    assignments,
  };
}

function refusal(invocationId, reasonCode, reason, preflight = null) {
  return redact({
    kind: 'p1-run-recovery-cleanup',
    schemaVersion: 1,
    mode: 'guarded-recovery-cleanup',
    invocationId: invocationId.toLowerCase(),
    outcome: 'refused',
    reasonCode,
    reason,
    preflight,
    steps: [],
    readback: null,
    actions: {
      assessmentOnly: false,
      writeRequestsIssued: false,
      cleanupPerformed: false,
      cleanupVerified: false,
      grantsChanged: false,
      newJourneyStarted: false,
    },
  });
}

async function readback(reconcile, api, environment, invocationId, signal) {
  try {
    return await reconcile({ api, environment, invocationId, signal });
  } catch {
    return {
      kind: 'p1-run-reconciliation',
      schemaVersion: 1,
      mode: 'read-only-assessment',
      invocationId: invocationId.toLowerCase(),
      verdict: 'needs review',
      verdictReasonCode: 'recovery_cleanup_readback_failed',
      verdictReason: 'The post-write reconciliation could not be completed.',
      actions: {
        assessmentOnly: true,
        writeRequestsIssued: false,
        cleanupPerformed: false,
        fixtureWritesPerformed: false,
        grantsChanged: false,
        newJourneyStarted: false,
      },
    };
  }
}

/** Validate a saved safe-to-clean-up assessment without contacting its target. */
export function isValidP1RecoveryEvidence(evidence, { invocationId, environment } = {}) {
  return typeof invocationId === 'string' && INVOCATION_ID.test(invocationId)
    && Boolean(environment?.actorId && environment?.issuer && environment?.tenantId)
    && cleanupTarget(evidence, invocationId, environment) !== null;
}

/** Re-read the exact saved setup in one sign-in, then perform one guarded cleanup and read it back. */
export async function recoverP1RunCleanup({
  api,
  environment,
  invocationId,
  savedEvidence,
  signal,
  reconcile = reconcileP1Run,
} = {}) {
  if (typeof invocationId !== 'string' || !INVOCATION_ID.test(invocationId)
    || !environment?.actorId || !environment?.issuer || !environment?.tenantId
    || typeof reconcile !== 'function') {
    return refusal(
      typeof invocationId === 'string' ? invocationId : '',
      'recovery_cleanup_input_invalid',
      'A valid invocation, selected setup actor, and reconciliation provider are required.',
    );
  }
  const target = cleanupTarget(savedEvidence, invocationId, environment);
  if (!target) {
    return refusal(
      invocationId,
      'saved_reconciliation_evidence_invalid',
      'The saved evidence is not a complete, unchanged assessment for this invocation and setup actor.',
    );
  }

  // Saved evidence pins the target; only these fresh signed-in reads can authorize writes.
  const fresh = await readback(reconcile, api, environment, invocationId, signal);
  const freshTarget = cleanupTarget(fresh, invocationId, environment);
  if (!freshTarget
    || freshTarget.actorId !== target.actorId
    || freshTarget.projectId !== target.projectId
    || freshTarget.projectRevision !== target.projectRevision
    || freshTarget.projectConfigurationRevision !== target.projectConfigurationRevision
    || SETUP_ROLES.some(role =>
      freshTarget.assignments[role].assignmentId.toLowerCase()
        !== target.assignments[role].assignmentId.toLowerCase()
      || freshTarget.assignments[role].revision !== target.assignments[role].revision)) {
    return refusal(
      invocationId,
      'fresh_reconciliation_mismatch',
      'The fresh signed-in assessment does not exactly match the saved project, actor, assignments, and revisions; no writes were sent.',
      fresh,
    );
  }

  const currentContext = fresh.effectiveAuthority.context;
  if (!hasPermission(currentContext, 'tenant', environment.tenantId, 'writeProjects')) {
    return refusal(
      invocationId,
      'tenant_admin_authority_required',
      'The signed-in actor does not have current tenant-admin project-write authority; no writes were sent.',
      fresh,
    );
  }

  let cleanup;
  try {
    cleanup = await runP1CleanupOperations({
      api,
      invocationId,
      projectName: fresh.projectName,
      projectId: target.projectId,
      projectRevision: target.projectRevision,
      ownerAssignmentId: target.assignments.owner.assignmentId,
      ownerAssignmentRevision: target.assignments.owner.revision,
      orchestratorAssignmentId: target.assignments.orchestrator.assignmentId,
      orchestratorAssignmentRevision: target.assignments.orchestrator.revision,
      actorId: target.actorId,
      signal,
    });
  } catch (error) {
    const steps = Array.isArray(error?.p1CleanupSteps) ? error.p1CleanupSteps : [];
    if (steps.length === 0) {
      return refusal(
        invocationId,
        'cleanup_contract_unavailable',
        'The discovered Gateway contract did not support the guarded cleanup operations; no writes were sent.',
        fresh,
      );
    }
    const afterFailure = await readback(reconcile, api, environment, invocationId, signal);
    return redact({
      kind: 'p1-run-recovery-cleanup',
      schemaVersion: 1,
      mode: 'guarded-recovery-cleanup',
      invocationId: invocationId.toLowerCase(),
      outcome: 'indeterminate',
      reasonCode: 'cleanup_write_failed_after_send',
      reason: 'A cleanup request failed or its receipt did not match. No further writes were sent; use the readback for review and do not retry.',
      preflight: fresh,
      steps,
      readback: afterFailure,
      actions: {
        assessmentOnly: false,
        writeRequestsIssued: true,
        cleanupPerformed: steps.some(step => step.outcome === 'confirmed'),
        cleanupVerified: false,
        grantsChanged: steps.some(step => step.outcome === 'confirmed'
          && step.operationId === 'revokeProjectRoleAssignment'),
        newJourneyStarted: false,
      },
    });
  }

  const observed = await readback(reconcile, api, environment, invocationId, signal);
  const complete = observed.verdict === 'already clean'
    && observed.verdictReasonCode === 'positive_archived_setup_revocation_evidence';
  return redact({
    kind: 'p1-run-recovery-cleanup',
    schemaVersion: 1,
    mode: 'guarded-recovery-cleanup',
    invocationId: invocationId.toLowerCase(),
    outcome: complete ? 'completed' : 'indeterminate',
    reasonCode: complete ? 'cleanup_verified' : 'cleanup_readback_needs_review',
    reason: complete
      ? 'All guarded writes returned exact receipts, and readback confirmed the archived project and revoked setup assignments.'
      : 'The writes returned receipts, but readback did not prove the clean residual state. Do not retry; review the readback.',
    preflight: fresh,
    steps: cleanup.steps,
    readback: observed,
    actions: {
      assessmentOnly: false,
      writeRequestsIssued: true,
      cleanupPerformed: true,
      cleanupVerified: complete,
      grantsChanged: true,
      newJourneyStarted: false,
    },
  });
}
