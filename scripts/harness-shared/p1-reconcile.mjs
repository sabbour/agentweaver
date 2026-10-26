import { mkdir, open } from 'node:fs/promises';
import path from 'node:path';
import { p1FixtureProjectName } from './p1-fixture-setup.mjs';
import { redact } from './redaction.mjs';

const INVOCATION_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const PROJECTS_PATH = '/api/v1/projects';
const AUTHORITY_PATH = '/api/v1/authorization/context';

function readFailure(call) {
  if (call?.status === 401 || call?.status === 403) {
    return { status: 'denied', reason: 'The read request was denied.' };
  }
  return { status: 'incomplete', reason: 'The read request did not return a complete successful response.' };
}

async function invokeRead(api, operationId, expectedPath, signal) {
  const operation = api.operations?.get(operationId);
  if (!operation || operation.method !== 'GET' || operation.path !== expectedPath) {
    return { status: 'unavailable', reason: 'The expected read operation is not present in the discovered Gateway contract.' };
  }

  try {
    const call = await api.invoke(operationId, { signal });
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

function reconcileVerdict(projectMatch, effectiveAuthority, auditRecords) {
  if (!['found', 'not-found'].includes(projectMatch.status)
    || effectiveAuthority.status !== 'complete'
    || auditRecords.status !== 'complete' || auditRecords.complete !== true) {
    return {
      verdict: 'needs review',
      reason: 'Project, effective-authority, and complete matching audit evidence are all required for a conclusive assessment.',
    };
  }

  // A not-found result is conclusive only for a caller proven to see every project in the selected tenant.
  if (projectMatch.status === 'not-found' && !effectiveAuthority.tenantWideProjectVisibility) {
    return {
      verdict: 'needs review',
      reason: 'The permission-filtered project list cannot prove that the invocation-named project is gone.',
    };
  }
  if (projectMatch.status === 'found'
    && !['all-tenant-projects', 'matched-project'].includes(effectiveAuthority.targetVisibility)) {
    return {
      verdict: 'needs review',
      reason: 'The authorization context does not confirm current visibility of the matched project.',
    };
  }

  // The current Gateway contract has no authority-audit read operation, so it cannot support either positive verdict.
  return {
    verdict: 'needs review',
    reason: 'The Gateway contract does not expose matching authority audit records; cleanup state cannot be verified.',
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

  // Invoke only these two known GET operations; an absent or changed contract fails closed without trying another route.
  const projectRead = discovery.status === 'complete'
    ? await invokeRead(api, 'listProjects', PROJECTS_PATH, signal)
    : { status: 'unavailable', reason: discovery.reason };
  const projectMatch = summarizeProjectRead(projectRead, projectName);

  const authorityRead = discovery.status === 'complete'
    ? await invokeRead(api, 'getAuthorizationContext', AUTHORITY_PATH, signal)
    : { status: 'unavailable', reason: discovery.reason };
  const effectiveAuthority = summarizeAuthorityRead(authorityRead, environment, projectMatch);

  // No audit-read operation is declared in the current Gateway API contract; never guess a path or query the database.
  const auditRecords = {
    status: 'unavailable',
    complete: false,
    operationId: null,
    records: [],
    reason: 'The current Gateway API contract has no authority-audit read operation for this invocation.',
  };
  const assessment = reconcileVerdict(projectMatch, effectiveAuthority, auditRecords);

  // Include explicit action flags so this assessment cannot be mistaken for cleanup or approval to resume a journey.
  return redact({
    kind: 'p1-run-reconciliation',
    schemaVersion: 1,
    mode: 'read-only-assessment',
    invocationId: invocationId.toLowerCase(),
    projectName,
    projectMatch,
    effectiveAuthority,
    auditRecords,
    verdict: assessment.verdict,
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

/** Write exactly one redacted JSON evidence object without replacing an existing output file. */
export async function persistP1ReconcileEvidence(file, evidence) {
  if (typeof file !== 'string' || !file.trim()) {
    throw new Error('A reconciliation evidence output path is required.');
  }

  // Exclusive creation preserves prior evidence and ensures each invocation produces one standalone JSON record.
  await mkdir(path.dirname(path.resolve(file)), { recursive: true });
  const handle = await open(file, 'wx', 0o600);
  try {
    await handle.writeFile(`${JSON.stringify(redact(evidence), null, 2)}\n`, 'utf8');
  } finally {
    await handle.close();
  }
}
