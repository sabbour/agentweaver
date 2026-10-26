import { mkdir, open, unlink } from 'node:fs/promises';
import path from 'node:path';
import { p1FixtureProjectName } from './p1-fixture-setup.mjs';
import { redact } from './redaction.mjs';

const INVOCATION_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const PROJECTS_PATH = '/api/v1/projects';
const AUTHORITY_PATH = '/api/v1/authorization/context';
const ROLE_ASSIGNMENTS_PATH = '/api/v1/projects/{projectId}/role-assignments';
const AUTHORITY_AUDIT_PATH = '/api/v1/project-setup-invocations/{invocationId}/authority-audit';
const AUDIT_EVENT_TYPES = new Set([
  'membership_granted',
  'membership_revoked',
  'role_assigned',
  'role_revoked',
]);
const AUTHORITY_ROLES = new Set([
  'platformAdmin',
  'tenantAdmin',
  'owner',
  'contributor',
  'viewer',
  'orchestrator',
]);

function readFailure(call) {
  if (call?.status === 401) {
    return {
      status: 'denied',
      reasonCode: 'read_unauthenticated',
      reason: 'The read request was not authenticated with HTTP 401; no absence was inferred.',
    };
  }
  if (call?.status === 403) {
    return {
      status: 'denied',
      reasonCode: 'read_denied',
      reason: 'The read request was explicitly denied with HTTP 403; no absence was inferred.',
    };
  }
  if (call?.status === 404) {
    return {
      status: 'incomplete',
      reasonCode: 'read_not_found',
      reason: 'The read endpoint returned HTTP 404; this is not evidence that the invocation has no linked records.',
    };
  }
  return {
    status: 'incomplete',
    reasonCode: 'read_incomplete',
    reason: 'The read request did not return a complete successful response.',
  };
}

async function invokeRead(api, operationId, expectedPath, signal, pathParameters) {
  const operation = api.operations?.get(operationId);
  if (!operation || operation.method !== 'GET' || operation.path !== expectedPath) {
    return { status: 'unavailable', reason: 'The expected read operation is not present in the discovered Gateway contract.' };
  }

  try {
    const call = await api.invoke(operationId, {
      ...(pathParameters ? { pathParameters } : {}),
      signal,
    });
    if (call.status !== 200) return readFailure(call);
    return { status: 'complete', value: call.transientResponseBody };
  } catch {
    return { status: 'incomplete', reason: 'The read request failed before a complete response was received.' };
  }
}

function summarizeProjectRead(read, projectName) {
  if (read.status !== 'complete' || !Array.isArray(read.value)) {
    return {
      status: read.status === 'complete' ? 'incomplete' : read.status,
      matches: [],
      reason: read.reason ?? 'The project list response was not a complete project summary list.',
    };
  }

  const validProjects = read.value.every(project =>
    project && typeof project === 'object' && !Array.isArray(project)
    && typeof project.projectId === 'string' && project.projectId.length > 0
    && typeof project.name === 'string'
    && ['active', 'archived'].includes(project.state)
    && Number.isSafeInteger(project.revision) && project.revision > 0
    && Number.isSafeInteger(project.configurationRevision) && project.configurationRevision > 0
    && typeof project.createdAt === 'string' && project.createdAt.length > 0
    && typeof project.updatedAt === 'string' && project.updatedAt.length > 0
    && Array.isArray(project.effectiveRoles)
    && project.effectiveRoles.every(role =>
      role && typeof role === 'object' && !Array.isArray(role)
      && ['tenant', 'project'].includes(role.resourceType)
      && typeof role.resourceId === 'string' && role.resourceId.length > 0
      && ['tenantAdmin', 'owner', 'contributor', 'viewer', 'orchestrator'].includes(role.role)));
  if (!validProjects) {
    return {
      status: 'incomplete',
      matches: [],
      reason: 'The project list contains an incomplete project summary.',
    };
  }

  // listProjects has no name query and filters projects by the caller's current visibility, so compare the exact name locally.
  const matches = read.value.filter(project => project.name === projectName);
  const status = matches.length === 0 ? 'not-found' : matches.length === 1 ? 'found' : 'ambiguous';
  return {
    status,
    matches: matches.map(project => ({
      projectId: project.projectId,
      name: project.name,
      state: project.state,
      revision: project.revision ?? null,
      configurationRevision: project.configurationRevision ?? null,
      effectiveRoles: project.effectiveRoles ?? [],
    })),
  };
}

function summarizeRoleAssignments(read, expectedProjectId) {
  if (read.status !== 'complete') {
    return {
      status: read.status,
      complete: false,
      projectId: expectedProjectId,
      assignments: [],
      reason: read.reason,
    };
  }

  const result = read.value;
  const valid = result && typeof result === 'object' && !Array.isArray(result)
    && result.projectId === expectedProjectId
    && Array.isArray(result.assignments)
    && result.assignments.every(assignment =>
      assignment && typeof assignment === 'object' && !Array.isArray(assignment)
      && typeof assignment.assignmentId === 'string' && INVOCATION_ID.test(assignment.assignmentId)
      && typeof assignment.principalId === 'string' && assignment.principalId.length > 0
      && ['owner', 'contributor', 'viewer', 'orchestrator'].includes(assignment.role)
      && Number.isSafeInteger(assignment.revision) && assignment.revision > 0
      && typeof assignment.createdBy === 'string'
      && typeof assignment.createdAt === 'string' && assignment.createdAt.length > 0);
  if (!valid) {
    return {
      status: 'incomplete',
      complete: false,
      projectId: expectedProjectId,
      assignments: [],
      reason: 'The role-assignment response was incomplete or did not match the visible project.',
    };
  }

  return {
    status: 'complete',
    complete: true,
    projectId: expectedProjectId,
    assignments: result.assignments.map(assignment => ({
      assignmentId: assignment.assignmentId,
      principalId: assignment.principalId,
      role: assignment.role,
      revision: assignment.revision,
      createdBy: assignment.createdBy,
      createdAt: assignment.createdAt,
    })),
  };
}

function isValidAuditRecord(record) {
  return record && typeof record === 'object' && !Array.isArray(record)
    && typeof record.eventId === 'string' && INVOCATION_ID.test(record.eventId)
    && AUDIT_EVENT_TYPES.has(record.eventType)
    && (record.assignmentId == null
      || typeof record.assignmentId === 'string' && INVOCATION_ID.test(record.assignmentId))
    && (record.resourceType == null || ['platform', 'tenant', 'project'].includes(record.resourceType))
    && (record.resourceId == null || typeof record.resourceId === 'string')
    && (record.role == null || AUTHORITY_ROLES.has(record.role))
    && Number.isSafeInteger(record.revision) && record.revision > 0
    && typeof record.actor === 'string' && record.actor.length > 0
    && typeof record.createdAt === 'string' && record.createdAt.length > 0;
}

function summarizeAuditRead(read, invocationId) {
  if (read.status !== 'complete') {
    return {
      status: read.status,
      complete: false,
      truncated: false,
      operationId: 'readProjectSetupAuthorityAudit',
      records: [],
      reasonCode: read.reasonCode ?? 'audit_read_incomplete',
      reason: read.reason,
    };
  }

  const result = read.value;
  if (!result || typeof result !== 'object' || Array.isArray(result)
    || typeof result.invocationId !== 'string'
    || result.invocationId.toLowerCase() !== invocationId.toLowerCase()
    || !Array.isArray(result.records)) {
    return {
      status: 'incomplete',
      complete: false,
      truncated: false,
      operationId: 'readProjectSetupAuthorityAudit',
      records: [],
      reasonCode: 'audit_response_mismatch',
      reason: 'The authority-audit response did not match the requested invocation.',
    };
  }

  const records = result.records.filter(isValidAuditRecord).map(record => ({
    eventId: record.eventId,
    eventType: record.eventType,
    assignmentId: record.assignmentId ?? null,
    resourceType: record.resourceType ?? null,
    resourceId: record.resourceId ?? null,
    role: record.role ?? null,
    revision: record.revision,
    actor: record.actor,
    createdAt: record.createdAt,
  }));
  const complete = result.complete === true
    && result.truncated === false
    && result.records.length === records.length
    && records.length <= 32;
  return {
    status: complete ? 'complete' : 'incomplete',
    complete,
    truncated: result.truncated === true,
    operationId: 'readProjectSetupAuthorityAudit',
    records,
    ...(complete && records.length === 0 ? {
      reasonCode: 'no_invocation_linked_audit_evidence',
      reason: 'The complete audit read returned no invocation-linked records; this does not prove that setup had no authority effects.',
    } : {}),
    ...(complete ? {} : {
      reasonCode: 'audit_response_incomplete',
      reason: 'The authority-audit response was partial, truncated, malformed, or incomplete.',
    }),
  };
}

function hasPermission(context, resourceType, resourceId, permissionName) {
  return context.effectiveAuthority.some(resource =>
    typeof resource?.resourceType === 'string'
    && resource.resourceType.toLowerCase() === resourceType
    && resource.resourceId === resourceId
    && Array.isArray(resource.permissions)
    && resource.permissions.some(permission =>
      typeof permission?.permission === 'string'
      && permission.permission.toLowerCase() === permissionName.toLowerCase()));
}

function summarizeAuthorityRead(read, environment, projectMatch) {
  if (read.status !== 'complete') {
    return {
      status: read.status,
      context: null,
      tenantWideProjectVisibility: false,
      targetVisibility: 'unknown',
      reason: read.reason,
    };
  }

  const context = read.value;
  const completeContext = context && typeof context === 'object' && !Array.isArray(context)
    && context.contractVersion === 1
    && context.issuer === environment.issuer
    && context.actorId === environment.actorId
    && context.tenantId === environment.tenantId
    && Number.isSafeInteger(context.membershipRevision) && context.membershipRevision > 0
    && context.boundProjectId == null
    && context.boundRunId == null
    && Array.isArray(context.effectiveAuthority)
    && context.effectiveAuthority.every(resource =>
      resource && typeof resource === 'object' && !Array.isArray(resource)
      && ['platform', 'tenant', 'project'].includes(resource.resourceType)
      && typeof resource.resourceId === 'string' && resource.resourceId.length > 0
      && Array.isArray(resource.permissions)
      && resource.permissions.every(permission =>
        permission && typeof permission === 'object' && !Array.isArray(permission)
        && typeof permission.permission === 'string'
        && Number.isSafeInteger(permission.roleRevision) && permission.roleRevision > 0));
  if (!completeContext) {
    return {
      status: 'incomplete',
      context: null,
      tenantWideProjectVisibility: false,
      targetVisibility: 'unknown',
      reason: 'The authorization context is incomplete or does not match the unbound setup actor.',
    };
  }

  // Tenant-level ReadProjects is the only positive evidence that a missing item was not hidden by project filtering.
  const tenantWideProjectVisibility = hasPermission(
    context, 'tenant', environment.tenantId, 'readProjects',
  );
  const foundProjectId = projectMatch.status === 'found'
    ? projectMatch.matches[0]?.projectId
    : undefined;
  const projectScopedVisibility = typeof foundProjectId === 'string'
    && (hasPermission(context, 'project', foundProjectId, 'readProjects')
      || hasPermission(context, 'project', foundProjectId, 'writeProjects'));
  const targetVisibility = tenantWideProjectVisibility
    ? 'all-tenant-projects'
    : projectScopedVisibility
      ? 'matched-project'
      : projectMatch.status === 'not-found' ? 'permission-filtered' : 'unverified';

  return {
    status: 'complete',
    context,
    tenantWideProjectVisibility,
    targetVisibility,
  };
}

function hasCompleteSetupRevocationEvidence(records, environment, expectedProjectId = null) {
  if (records.length === 0) return false;

  const recordsByAssignment = new Map();
  const projectIds = new Set();
  for (const record of records) {
    if (!['role_assigned', 'role_revoked'].includes(record.eventType)
      || typeof record.assignmentId !== 'string'
      || record.resourceType !== 'project'
      || typeof record.resourceId !== 'string'
      || !['owner', 'orchestrator'].includes(record.role)) return false;

    projectIds.add(record.resourceId);
    const events = recordsByAssignment.get(record.assignmentId) ?? [];
    events.push(record);
    recordsByAssignment.set(record.assignmentId, events);
  }

  // An empty complete read is not positive cleanup evidence; require both P1 role grants and their revocations.
  if (projectIds.size !== 1 || expectedProjectId && !projectIds.has(expectedProjectId)
    || recordsByAssignment.size !== 2) return false;

  const roles = new Set();
  for (const events of recordsByAssignment.values()) {
    if (events.length !== 2) return false;
    const [assigned, revoked] = [...events].sort((left, right) => left.revision - right.revision);
    if (assigned.eventType !== 'role_assigned'
      || assigned.revision !== 1
      || assigned.actor !== environment.actorId
      || revoked.eventType !== 'role_revoked'
      || revoked.revision !== 2
      || assigned.resourceId !== revoked.resourceId
      || assigned.role !== revoked.role) return false;
    roles.add(assigned.role);
  }

  return roles.size === 2 && roles.has('owner') && roles.has('orchestrator');
}

function hasArchivedSetupRevocationEvidence(projectMatch, roleAssignments, auditRecords, environment) {
  const project = projectMatch.matches[0];
  // The assignment endpoint lists active grants only; the audit must prove both setup grants were revoked.
  return projectMatch.status === 'found'
    && project?.state === 'archived'
    && roleAssignments.status === 'complete'
    && roleAssignments.complete === true
    && roleAssignments.assignments.length === 0
    && hasCompleteSetupRevocationEvidence(auditRecords.records, environment, project.projectId);
}

function matchesSetupAssignments(projectMatch, roleAssignments, effectiveAuthority, auditRecords, environment) {
  if (projectMatch.status !== 'found'
    || projectMatch.matches[0]?.state !== 'active'
    || !['all-tenant-projects', 'matched-project'].includes(effectiveAuthority.targetVisibility)
    || roleAssignments.status !== 'complete'
    || roleAssignments.complete !== true
    || auditRecords.records.length !== 2) return false;

  const projectId = projectMatch.matches[0].projectId;
  const setupEvents = auditRecords.records;
  if (!setupEvents.every(record =>
    record.eventType === 'role_assigned'
    && record.resourceType === 'project'
    && record.resourceId === projectId
    && record.revision === 1
    && record.actor === environment.actorId
    && typeof record.assignmentId === 'string')
  ) return false;

  const roles = new Set(setupEvents.map(record => record.role));
  if (roles.size !== 2 || !roles.has('owner') || !roles.has('orchestrator')) return false;

  const assignmentIds = new Set(setupEvents.map(record => record.assignmentId));
  if (assignmentIds.size !== 2 || roleAssignments.assignments.length !== 2) return false;
  return setupEvents.every(event => roleAssignments.assignments.some(assignment =>
    assignment.assignmentId === event.assignmentId
    && assignment.principalId === environment.actorId
    && assignment.role === event.role
    && assignment.revision === event.revision));
}

function reconcileVerdict(projectMatch, effectiveAuthority, roleAssignments, auditRecords, environment) {
  const needsReview = (reasonCode, reason) => ({
    verdict: 'needs review',
    reasonCode,
    reason,
  });

  if (!['found', 'not-found'].includes(projectMatch.status)) {
    return needsReview(
      `project_list_${projectMatch.status}`,
      projectMatch.reason ?? 'The project list read did not establish matching-project visibility.',
    );
  }
  if (effectiveAuthority.status !== 'complete') {
    return needsReview(
      `authorization_context_${effectiveAuthority.status}`,
      effectiveAuthority.reason ?? 'The authorization context read did not establish current visibility.',
    );
  }

  // A permission-filtered list cannot prove absence; only tenant-wide ReadProjects plus a complete audit read can.
  if (projectMatch.status === 'not-found' && !effectiveAuthority.tenantWideProjectVisibility) {
    return needsReview(
      'project_not_found_visibility_unconfirmed',
      'The permission-filtered project list cannot prove that the invocation-named project is gone.',
    );
  }
  if (auditRecords.status !== 'complete' || auditRecords.complete !== true) {
    let reasonCode = 'authority_audit_read_incomplete';
    if (auditRecords.reasonCode === 'owner_audit_read_denied') {
      reasonCode = 'owner_audit_read_denied';
    } else if (auditRecords.reasonCode === 'read_unauthenticated') {
      reasonCode = 'authority_audit_read_unauthenticated';
    } else if (auditRecords.status === 'denied') {
      reasonCode = 'authority_audit_read_denied';
    } else if (auditRecords.reasonCode === 'read_not_found') {
      reasonCode = 'authority_audit_read_not_found';
    }
    return needsReview(
      reasonCode,
      auditRecords.reason ?? 'A complete authority-audit read is required; no absence was inferred.',
    );
  }
  if (auditRecords.records.length === 0) {
    return needsReview(
      'no_invocation_linked_audit_evidence',
      auditRecords.reason ?? 'The complete audit read returned no invocation-linked records; this does not prove that setup had no authority effects.',
    );
  }
  if (projectMatch.status === 'not-found') {
    if (!hasCompleteSetupRevocationEvidence(auditRecords.records, environment)) {
      return needsReview(
        'setup_revocation_evidence_incomplete',
        'The complete audit read does not prove that both setup assignments were revoked.',
      );
    }
    return {
      verdict: 'already clean',
      reasonCode: 'positive_setup_revocation_evidence',
      reason: 'The caller can see every project in the tenant, no invocation-named project is visible, and the complete audit read proves both setup assignments were revoked.',
    };
  }
  if (projectMatch.status === 'found'
    && !['all-tenant-projects', 'matched-project'].includes(effectiveAuthority.targetVisibility)) {
    return needsReview(
      'matched_project_visibility_unconfirmed',
      'The authorization context does not confirm current visibility of the matched project.',
    );
  }
  if (roleAssignments.status !== 'complete' || roleAssignments.complete !== true) {
    const reasonCode = roleAssignments.status === 'denied'
      ? 'role_assignment_read_denied'
      : `role_assignment_read_${roleAssignments.status}`;
    return needsReview(
      reasonCode,
      roleAssignments.reason ?? 'A complete read of the visible project role assignments is required for a conclusive assessment.',
    );
  }
  if (projectMatch.matches[0]?.state === 'archived') {
    if (hasArchivedSetupRevocationEvidence(
      projectMatch, roleAssignments, auditRecords, environment,
    )) {
      return {
        verdict: 'already clean',
        reasonCode: 'positive_archived_setup_revocation_evidence',
        reason: 'The visible archived project has no active role assignments, and the complete invocation-linked audit proves that both setup assignments were granted and later revoked.',
      };
    }
    return needsReview(
      'archived_setup_evidence_incomplete',
      'The archived project does not have complete evidence that both setup assignments were revoked.',
    );
  }
  if (!matchesSetupAssignments(projectMatch, roleAssignments, effectiveAuthority, auditRecords, environment)) {
    return needsReview(
      'setup_evidence_mismatch',
      'The visible project, active assignments, and invocation-linked audit records do not prove an unchanged P1 setup fixture.',
    );
  }

  // Cleanup is guarded separately by the owner; this read-only verdict never authorizes or performs that mutation.
  return {
    verdict: 'safe to clean up',
    reasonCode: 'positive_active_setup_evidence',
    reason: 'The visible active project, its Owner and Orchestrator assignments, and complete invocation-linked audit records match the setup actor. This is an assessment only; it does not authorize or perform cleanup.',
  };
}

/** Read invocation-owned project visibility and actor authority without issuing any mutation or journey request. */
export async function reconcileP1Run({ api, environment, invocationId, signal } = {}) {
  if (typeof invocationId !== 'string' || !INVOCATION_ID.test(invocationId)) {
    throw new Error('A valid P1 invocation ID is required for reconciliation.');
  }
  if (!api || typeof api.discover !== 'function' || typeof api.invoke !== 'function'
    || !(api.operations instanceof Map)) {
    throw new Error('The read-only P1 Gateway client is invalid.');
  }
  if (!environment || typeof environment.issuer !== 'string'
    || typeof environment.actorId !== 'string' || typeof environment.tenantId !== 'string') {
    throw new Error('The selected P1 setup identity is incomplete.');
  }

  const projectName = p1FixtureProjectName(invocationId);
  let discovery;
  try {
    // The existing Gateway client discovers its contract with one unauthenticated GET before invoking operations.
    await api.discover();
    discovery = { status: 'complete' };
  } catch {
    discovery = {
      status: 'incomplete',
      reason: 'The Gateway API contract could not be discovered.',
    };
  }

  // Invoke only known GET operations; an absent or changed contract fails closed without trying another route.
  const projectRead = discovery.status === 'complete'
    ? await invokeRead(api, 'listProjects', PROJECTS_PATH, signal)
    : { status: 'unavailable', reason: discovery.reason };
  const projectMatch = summarizeProjectRead(projectRead, projectName);

  const authorityRead = discovery.status === 'complete'
    ? await invokeRead(api, 'getAuthorizationContext', AUTHORITY_PATH, signal)
    : { status: 'unavailable', reason: discovery.reason };
  const effectiveAuthority = summarizeAuthorityRead(authorityRead, environment, projectMatch);

  const roleAssignments = projectMatch.status === 'found' && discovery.status === 'complete'
    ? summarizeRoleAssignments(
      await invokeRead(
        api,
        'listProjectRoleAssignments',
        ROLE_ASSIGNMENTS_PATH,
        signal,
        { projectId: projectMatch.matches[0].projectId },
      ),
      projectMatch.matches[0].projectId,
    )
    : {
      status: 'not-applicable',
      complete: false,
      projectId: null,
      assignments: [],
      reason: 'No single visible project was available for a role-assignment read.',
    };

  const auditRead = discovery.status === 'complete'
    ? await invokeRead(
      api,
      'readProjectSetupAuthorityAudit',
      AUTHORITY_AUDIT_PATH,
      signal,
      { invocationId: invocationId.toLowerCase() },
    )
    : { status: 'unavailable', reason: discovery.reason };
  const auditRecords = summarizeAuditRead(auditRead, invocationId);
  if (auditRecords.status === 'denied'
    && auditRecords.reasonCode === 'read_denied'
    && projectMatch.status === 'found'
    && roleAssignments.status === 'complete'
    && roleAssignments.assignments.some(assignment =>
      assignment.principalId === environment.actorId && assignment.role === 'owner')) {
    // A visible Owner's explicit denial is evidence, never an empty audit result or proof of absence.
    auditRecords.reasonCode = 'owner_audit_read_denied';
    auditRecords.reason = 'The current project Owner was explicitly denied the authority-audit read; this is not an empty result and does not prove that setup had no authority effects.';
  }
  const assessment = reconcileVerdict(
    projectMatch, effectiveAuthority, roleAssignments, auditRecords, environment,
  );

  // Include explicit action flags so this assessment cannot be mistaken for cleanup or approval to resume a journey.
  return redact({
    kind: 'p1-run-reconciliation',
    schemaVersion: 1,
    mode: 'read-only-assessment',
    invocationId: invocationId.toLowerCase(),
    projectName,
    projectMatch,
    effectiveAuthority,
    roleAssignments,
    auditRecords,
    verdict: assessment.verdict,
    verdictReasonCode: assessment.reasonCode,
    verdictReason: assessment.reason,
    actions: {
      assessmentOnly: true,
      writeRequestsIssued: false,
      cleanupPerformed: false,
      fixtureWritesPerformed: false,
      grantsChanged: false,
      newJourneyStarted: false,
    },
  });
}

/** Reserve an exclusive evidence path before any recovery-cleanup write can be sent. */
export async function reserveP1EvidenceOutput(file) {
  if (typeof file !== 'string' || !file.trim()) {
    throw new Error('A reconciliation evidence output path is required.');
  }

  const destination = path.resolve(file);
  await mkdir(path.dirname(destination), { recursive: true });
  // Reserving the exact destination prevents an existing assessment from being reused as cleanup output.
  const handle = await open(destination, 'wx', 0o600);
  let writeAttempted = false;
  let closed = false;
  const close = async () => {
    if (!closed) {
      await handle.close();
      closed = true;
    }
  };
  return {
    async write(evidence) {
      if (writeAttempted) throw new Error('The evidence output reservation can only be written once.');
      writeAttempted = true;
      try {
        await handle.writeFile(`${JSON.stringify(redact(evidence), null, 2)}\n`, 'utf8');
      } finally {
        await close();
      }
    },
    async release() {
      await close();
      if (!writeAttempted) await unlink(destination);
    },
  };
}

/** Write exactly one redacted JSON evidence object without replacing an existing output file. */
export async function persistP1ReconcileEvidence(file, evidence) {
  const output = await reserveP1EvidenceOutput(file);
  try {
    await output.write(evidence);
  } finally {
    await output.release();
  }
}
