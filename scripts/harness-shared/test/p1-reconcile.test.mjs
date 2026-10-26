import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { AgentweaverClient } from '../../api-harness/lib/client.mjs';
import { persistP1ReconcileEvidence, reconcileP1Run } from '../p1-reconcile.mjs';

const INVOCATION_ID = '5749fb6f-543b-427d-a04e-b7f836180ef4';
const PROJECT_NAME = `p1-${INVOCATION_ID}`;
const TOKEN = 'eyJhbGciOiJub25lIn0.eyJzdWIiOiJhY2NlcHRhbmNlLWFjdG9yIn0.signature';
const ENVIRONMENT = {
  baseUrl: 'https://environment.controlled.test/',
  issuer: 'https://identity.controlled.test/',
  audience: 'agentweaver-controlled',
  actorId: 'acceptance-actor',
  tenantId: 'acceptance-tenant',
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

function makeClient({ projects = [], authority = authorizationContext(), authorityStatus = 200 } = {}) {
  const requests = [];
  const contract = {
    openapi: '3.1.0',
    paths: {
      '/api/v1/projects': { get: { operationId: 'listProjects', parameters: [] } },
      '/api/v1/authorization/context': {
        get: { operationId: 'getAuthorizationContext', parameters: [] },
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
      return Response.json({ error: 'unexpected request' }, { status: 404 });
    },
  });
  return { client, requests };
}

test('reconcile only issues GETs, exact-filters the project, redacts tokens, and writes one JSON file', async () => {
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
  ]);
  assert.equal(evidence.projectMatch.status, 'found');
  assert.deepEqual(evidence.projectMatch.matches.map(item => item.projectId), ['run-5-project']);
  assert.equal(evidence.effectiveAuthority.tenantWideProjectVisibility, true);
  assert.equal(evidence.auditRecords.status, 'unavailable');
  assert.equal(evidence.verdict, 'needs review');
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
    assert.equal(JSON.parse(content).verdict, 'needs review');
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
  });
  const evidence = await reconcileP1Run({
    api: client,
    environment: ENVIRONMENT,
    invocationId: INVOCATION_ID,
  });

  assert.equal(evidence.projectMatch.status, 'not-found');
  assert.equal(evidence.effectiveAuthority.tenantWideProjectVisibility, true);
  assert.equal(evidence.auditRecords.status, 'unavailable');
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
