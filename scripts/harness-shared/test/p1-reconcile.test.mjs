import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { AgentweaverClient } from '../../api-harness/lib/client.mjs';
import { persistP1ReconcileEvidence, reconcileP1Run } from '../p1-reconcile.mjs';

const INVOCATION_ID = '5749fb6f-543b-427d-a04e-b7f836180ef4';
const PROJECT_NAME = `p1-${INVOCATION_ID}`;
const PROJECT_ID = 'run-5-project';
const TOKEN = 'eyJhbGciOiJub25lIn0.eyJzdWIiOiJhY2NlcHRhbmNlLWFjdG9yIn0.signature';
const ENVIRONMENT = {
  baseUrl: 'https://environment.controlled.test/',
  issuer: 'https://identity.controlled.test/',
  audience: 'agentweaver-controlled',
  actorId: 'acceptance-actor',
  tenantId: 'acceptance-tenant',
};

const ROLE_ASSIGNMENTS = {
  projectId: PROJECT_ID,
  assignments: [
    {
      assignmentId: 'a0000000-0000-4000-8000-000000000001',
      principalId: ENVIRONMENT.actorId,
      role: 'owner',
      revision: 1,
      createdBy: 'acceptance-admin',
      createdAt: '2026-10-13T00:00:00.000Z',
    },
    {
      assignmentId: 'a0000000-0000-4000-8000-000000000002',
      principalId: ENVIRONMENT.actorId,
      role: 'orchestrator',
      revision: 1,
      createdBy: 'acceptance-admin',
      createdAt: '2026-10-13T00:00:00.000Z',
    },
  ],
};
const AUDIT_RECORDS = {
  invocationId: INVOCATION_ID,
  complete: true,
  truncated: false,
  accessToken: TOKEN,
  records: [
    {
      eventId: 'b0000000-0000-4000-8000-000000000001',
      eventType: 'role_assigned',
      assignmentId: ROLE_ASSIGNMENTS.assignments[0].assignmentId,
      resourceType: 'project',
      resourceId: PROJECT_ID,
      role: 'owner',
      revision: 1,
      actor: ENVIRONMENT.actorId,
      createdAt: '2026-10-13T00:00:00.000Z',
    },
    {
      eventId: 'b0000000-0000-4000-8000-000000000002',
      eventType: 'role_assigned',
      assignmentId: ROLE_ASSIGNMENTS.assignments[1].assignmentId,
      resourceType: 'project',
      resourceId: PROJECT_ID,
      role: 'orchestrator',
      revision: 1,
      createdBy: 'acceptance-admin',
      createdAt: '2026-10-13T00:00:00.000Z',
      actor: ENVIRONMENT.actorId,
    },
  ],
};

function project(projectId, name = PROJECT_NAME) {
  return {
    projectId,
    name,
    state: 'active',
    revision: 2,
    configurationRevision: 1,
    createdAt: '2026-10-13T00:00:00.000Z',
    updatedAt: '2026-10-13T00:00:00.000Z',
    effectiveRoles: [{ resourceType: 'project', resourceId: projectId, role: 'owner' }],
  };
}

function authorizationContext(effectiveAuthority = []) {
  return {
    contractVersion: 1,
    issuer: ENVIRONMENT.issuer,
    actorId: ENVIRONMENT.actorId,
    tenantId: ENVIRONMENT.tenantId,
    membershipRevision: 1,
    boundProjectId: null,
    boundRunId: null,
    effectiveAuthority,
    accessToken: TOKEN,
  };
}

function makeClient({
  projects = [],
  authority = authorizationContext(),
  authorityStatus = 200,
  roleAssignments = ROLE_ASSIGNMENTS,
  roleAssignmentStatus = 200,
  auditRecords = AUDIT_RECORDS,
  auditStatus = 200,
} = {}) {
  const requests = [];
  const contract = {
    openapi: '3.1.0',
    paths: {
      '/api/v1/projects': { get: { operationId: 'listProjects', parameters: [] } },
      '/api/v1/authorization/context': {
        get: { operationId: 'getAuthorizationContext', parameters: [] },
      },
      '/api/v1/projects/{projectId}/role-assignments': {
        get: {
          operationId: 'listProjectRoleAssignments',
          parameters: [{ in: 'path', name: 'projectId', required: true }],
        },
      },
      '/api/v1/project-setup-invocations/{invocationId}/authority-audit': {
        get: {
          operationId: 'readProjectSetupAuthorityAudit',
          parameters: [{ in: 'path', name: 'invocationId', required: true }],
        },
      },
    },
  };
  const authProvider = {
    origin: new URL(ENVIRONMENT.baseUrl).origin,
    async getAuthorization() { return `Bearer ${TOKEN}`; },
  };
  const client = new AgentweaverClient({
    baseUrl: ENVIRONMENT.baseUrl,
    authProvider,
    tenantId: ENVIRONMENT.tenantId,
    async fetchImpl(url, init) {
      requests.push({ method: init.method, path: new URL(url).pathname });
      const pathName = new URL(url).pathname;
      if (pathName === '/openapi/v1.json') {
        return Response.json(contract);
      }
      if (pathName === '/api/v1/projects') {
        return Response.json(projects);
      }
      if (pathName === '/api/v1/authorization/context') {
        return Response.json(authority, { status: authorityStatus });
      }
      if (pathName === `/api/v1/projects/${PROJECT_ID}/role-assignments`) {
        return Response.json(roleAssignments, { status: roleAssignmentStatus });
      }
      if (pathName === `/api/v1/project-setup-invocations/${INVOCATION_ID}/authority-audit`) {
        return Response.json(auditRecords, { status: auditStatus });
      }
      return Response.json({ error: 'unexpected request' }, { status: 404 });
    },
  });
  return { client, requests };
}

test('reconcile reads roles and audit records, assesses setup without writes, redacts tokens, and writes one JSON file', async () => {
  const { client, requests } = makeClient({
    projects: [project('run-5-project'), project('another-project', 'p1-another-invocation')],
    authority: authorizationContext([{
      resourceType: 'tenant',
      resourceId: ENVIRONMENT.tenantId,
      permissions: [{ permission: 'readProjects', roleRevision: 4 }],
    }]),
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });
  assert.deepEqual(requests, [
    { method: 'GET', path: '/openapi/v1.json' },
    { method: 'GET', path: '/api/v1/projects' },
    { method: 'GET', path: '/api/v1/authorization/context' },
    { method: 'GET', path: `/api/v1/projects/${PROJECT_ID}/role-assignments` },
    { method: 'GET', path: `/api/v1/project-setup-invocations/${INVOCATION_ID}/authority-audit` },
  ]);
  assert.equal(evidence.projectMatch.status, 'found');
  assert.deepEqual(evidence.projectMatch.matches.map(item => item.projectId), ['run-5-project']);
  assert.equal(evidence.effectiveAuthority.tenantWideProjectVisibility, true);
  assert.equal(evidence.roleAssignments.status, 'complete');
  assert.equal(evidence.auditRecords.status, 'complete');
  assert.equal(evidence.auditRecords.records.length, 2);
  assert.equal(evidence.verdict, 'safe to clean up');
  assert.match(evidence.verdictReason, /assessment only/);
  assert.equal(requests.every(request => request.method === 'GET'), true);
  assert.deepEqual(evidence.actions, {
    assessmentOnly: true,
    writeRequestsIssued: false,
    cleanupPerformed: false,
    fixtureWritesPerformed: false,
    grantsChanged: false,
    newJourneyStarted: false,
  });

  const directory = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-p1-reconcile-'));
  try {
    const output = path.join(directory, 'reconciliation.json');
    await persistP1ReconcileEvidence(output, evidence);
    const content = await readFile(output, 'utf8');
    assert.equal(JSON.parse(content).verdict, 'safe to clean up');
    assert.equal(content.includes(TOKEN), false);
    assert.match(content, /"accessToken": "\[REDACTED\]"/);
    assert.deepEqual(await readdir(directory), ['reconciliation.json']);
    await assert.rejects(persistP1ReconcileEvidence(output, evidence), { code: 'EEXIST' });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});

test('a filtered not-found result with incomplete authority and audit remains needs review', async () => {
  const incompleteAuthority = authorizationContext();
  incompleteAuthority.effectiveAuthority = null;
  const { client, requests } = makeClient({
    projects: [],
    authority: incompleteAuthority,
    auditRecords: { ...AUDIT_RECORDS, complete: false },
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.projectMatch.status, 'not-found');
  assert.equal(evidence.effectiveAuthority.status, 'incomplete');
  assert.equal(evidence.effectiveAuthority.targetVisibility, 'unknown');
  assert.equal(evidence.auditRecords.complete, false);
  assert.equal(evidence.verdict, 'needs review');
  assert.equal(requests.every(request => request.method === 'GET'), true);
});

test('a filtered not-found result without tenant-wide visibility is not treated as clean', async () => {
  const { client } = makeClient({ projects: [], authority: authorizationContext() });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.projectMatch.status, 'not-found');
  assert.equal(evidence.effectiveAuthority.status, 'complete');
  assert.equal(evidence.effectiveAuthority.tenantWideProjectVisibility, false);
  assert.equal(evidence.effectiveAuthority.targetVisibility, 'permission-filtered');
  assert.equal(evidence.verdict, 'needs review');
});

test('a found project still needs review when the authority context does not confirm visibility', async () => {
  const { client } = makeClient({
    projects: [project('run-5-project')],
    authority: authorizationContext(),
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.projectMatch.status, 'found');
  assert.equal(evidence.effectiveAuthority.targetVisibility, 'unverified');
  assert.equal(evidence.verdict, 'needs review');
});

test('tenant-wide visibility is not enough to call a missing project clean without complete audit evidence', async () => {
  const { client } = makeClient({
    projects: [],
    authority: authorizationContext([{
      resourceType: 'tenant',
      resourceId: ENVIRONMENT.tenantId,
      permissions: [{ permission: 'readProjects', roleRevision: 4 }],
    }]),
    auditRecords: { ...AUDIT_RECORDS, complete: false },
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.projectMatch.status, 'not-found');
  assert.equal(evidence.effectiveAuthority.tenantWideProjectVisibility, true);
  assert.equal(evidence.auditRecords.status, 'incomplete');
  assert.equal(evidence.verdict, 'needs review');
});

test('a Run 5 not-found and complete empty audit read do not establish already clean', async () => {
  const { client, requests } = makeClient({
    projects: [],
    authority: authorizationContext([{
      resourceType: 'tenant',
      resourceId: ENVIRONMENT.tenantId,
      permissions: [{ permission: 'readProjects', roleRevision: 4 }],
    }]),
    auditRecords: { ...AUDIT_RECORDS, records: [] },
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.projectMatch.status, 'not-found');
  assert.equal(evidence.auditRecords.complete, true);
  assert.equal(evidence.auditRecords.reasonCode, 'no_invocation_linked_audit_evidence');
  assert.equal(evidence.verdict, 'needs review');
  assert.equal(evidence.verdictReasonCode, 'no_invocation_linked_audit_evidence');
  assert.match(evidence.verdictReason, /does not prove that setup had no authority effects/);
  assert.equal(evidence.roleAssignments.status, 'not-applicable');
  assert.equal(requests.some(request => request.path.includes('/role-assignments')), false);
  assert.equal(requests.every(request => request.method === 'GET'), true);
});

test('tenant-wide visibility and complete setup revocation evidence establish already clean', async () => {
  const { client, requests } = makeClient({
    projects: [],
    authority: authorizationContext([{
      resourceType: 'tenant',
      resourceId: ENVIRONMENT.tenantId,
      permissions: [{ permission: 'readProjects', roleRevision: 4 }],
    }]),
    auditRecords: {
      ...AUDIT_RECORDS,
      records: [
        ...AUDIT_RECORDS.records,
        {
          eventId: 'b0000000-0000-4000-8000-000000000003',
          eventType: 'role_revoked',
          assignmentId: ROLE_ASSIGNMENTS.assignments[0].assignmentId,
          resourceType: 'project',
          resourceId: PROJECT_ID,
          role: 'owner',
          revision: 2,
          actor: 'cleanup-actor',
          createdAt: '2026-10-13T00:01:00.000Z',
        },
        {
          eventId: 'b0000000-0000-4000-8000-000000000004',
          eventType: 'role_revoked',
          assignmentId: ROLE_ASSIGNMENTS.assignments[1].assignmentId,
          resourceType: 'project',
          resourceId: PROJECT_ID,
          role: 'orchestrator',
          revision: 2,
          actor: 'cleanup-actor',
          createdAt: '2026-10-13T00:01:00.000Z',
        },
      ],
    },
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.projectMatch.status, 'not-found');
  assert.equal(evidence.auditRecords.complete, true);
  assert.equal(evidence.verdict, 'already clean');
  assert.equal(evidence.verdictReasonCode, 'positive_setup_revocation_evidence');
  assert.equal(evidence.roleAssignments.status, 'not-applicable');
  assert.equal(requests.some(request => request.path.includes('/role-assignments')), false);
  assert.equal(requests.every(request => request.method === 'GET'), true);
});

test('a denied role-assignment read remains needs review', async () => {
  const { client } = makeClient({
    projects: [project(PROJECT_ID)],
    authority: authorizationContext([{
      resourceType: 'tenant',
      resourceId: ENVIRONMENT.tenantId,
      permissions: [{ permission: 'readProjects', roleRevision: 4 }],
    }]),
    roleAssignments: { error: 'forbidden' },
    roleAssignmentStatus: 403,
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.roleAssignments.status, 'denied');
  assert.equal(evidence.verdict, 'needs review');
  assert.equal(evidence.verdictReasonCode, 'role_assignment_read_denied');
});

test('an Owner-style denial with no returned audit rows is explicit evidence, never absence', async () => {
  const { client } = makeClient({
    projects: [project(PROJECT_ID)],
    authority: authorizationContext([{
      resourceType: 'project',
      resourceId: PROJECT_ID,
      permissions: [{ permission: 'readProjects', roleRevision: 4 }],
    }]),
    auditRecords: { error: 'forbidden' },
    auditStatus: 403,
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.auditRecords.status, 'denied');
  assert.equal(evidence.auditRecords.complete, false);
  assert.deepEqual(evidence.auditRecords.records, []);
  assert.equal(evidence.auditRecords.reasonCode, 'owner_audit_read_denied');
  assert.match(evidence.auditRecords.reason, /current project Owner was explicitly denied/);
  assert.equal(evidence.verdictReasonCode, 'owner_audit_read_denied');
  assert.match(evidence.verdictReason, /not an empty result/);
  assert.equal(evidence.projectMatch.status, 'found');
  assert.equal(evidence.effectiveAuthority.targetVisibility, 'matched-project');
  assert.equal(evidence.verdict, 'needs review');
});

test('a denied audit read without visible Owner evidence keeps its explicit denial reason', async () => {
  const { client } = makeClient({
    projects: [],
    authority: authorizationContext([{
      resourceType: 'tenant',
      resourceId: ENVIRONMENT.tenantId,
      permissions: [{ permission: 'readProjects', roleRevision: 4 }],
    }]),
    auditRecords: { error: 'forbidden' },
    auditStatus: 403,
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.auditRecords.status, 'denied');
  assert.equal(evidence.auditRecords.reasonCode, 'read_denied');
  assert.equal(evidence.verdict, 'needs review');
  assert.equal(evidence.verdictReasonCode, 'authority_audit_read_denied');
  assert.match(evidence.verdictReason, /no absence was inferred/);
});

test('an audit endpoint not-found is not treated as no linked evidence', async () => {
  const { client } = makeClient({
    projects: [],
    authority: authorizationContext([{
      resourceType: 'tenant',
      resourceId: ENVIRONMENT.tenantId,
      permissions: [{ permission: 'readProjects', roleRevision: 4 }],
    }]),
    auditRecords: { error: 'not found' },
    auditStatus: 404,
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.auditRecords.status, 'incomplete');
  assert.equal(evidence.auditRecords.reasonCode, 'read_not_found');
  assert.equal(evidence.verdictReasonCode, 'authority_audit_read_not_found');
  assert.match(evidence.verdictReason, /not evidence that the invocation has no linked records/);
  assert.equal(evidence.verdict, 'needs review');
});

test('a truncated audit response is incomplete and cannot establish a verdict', async () => {
  const { client } = makeClient({
    projects: [],
    authority: authorizationContext([{
      resourceType: 'tenant',
      resourceId: ENVIRONMENT.tenantId,
      permissions: [{ permission: 'readProjects', roleRevision: 4 }],
    }]),
    auditRecords: { ...AUDIT_RECORDS, complete: false, truncated: true },
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.auditRecords.status, 'incomplete');
  assert.equal(evidence.auditRecords.truncated, true);
  assert.equal(evidence.verdict, 'needs review');
});

test('a denied authority read remains needs review even when the visible project list is empty', async () => {
  const { client } = makeClient({
    projects: [],
    authority: { error: 'forbidden' },
    authorityStatus: 403,
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.projectMatch.status, 'not-found');
  assert.equal(evidence.effectiveAuthority.status, 'denied');
  assert.equal(evidence.verdict, 'needs review');
  assert.equal(evidence.verdictReasonCode, 'authorization_context_denied');
  assert.match(evidence.verdictReason, /explicitly denied/);
});

test('refuses to invoke a discovered project write operation', async () => {
  let requests = 0;
  const api = {
    operations: new Map([
      ['listProjects', { method: 'POST', path: '/api/v1/projects' }],
      ['getAuthorizationContext', { method: 'GET', path: '/api/v1/authorization/context' }],
    ]),
    async discover() {},
    async invoke() { requests += 1; },
  };
  const evidence = await reconcileP1Run({
    api,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(requests, 1);
  assert.equal(evidence.projectMatch.status, 'unavailable');
  assert.equal(evidence.verdict, 'needs review');
  assert.equal(evidence.actions.writeRequestsIssued, false);
});
