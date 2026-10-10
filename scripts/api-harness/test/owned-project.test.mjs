import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { AgentweaverClient } from '../lib/client.mjs';
import { OwnedProjectFixture } from '../lib/owned-project.mjs';

async function fixture() {
  const requests = [];
  let project;
  let conflict = false;
  let loseArchiveResponse = false;
  let verifyState = true;
  const document = {
    openapi: '3.1.0', paths: {
      '/api/v1/projects': { post: { operationId: 'createProject', requestBody: { required: true } } },
      '/api/v1/projects/{projectId}': {
        get: { operationId: 'getProject' },
        patch: { operationId: 'updateProject', requestBody: { required: true } },
      },
    },
  };
  const server = createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : null;
    requests.push({ method: request.method, path: request.url, body });
    response.setHeader('Content-Type', 'application/json');
    response.setHeader('X-Correlation-Id', request.headers['x-correlation-id']);
    if (request.url === '/openapi/v1.json') return response.end(JSON.stringify(document));
    if (request.method === 'POST') {
      project = { projectId: 'controlled-owned-project', name: body.name, state: 'Active', revision: 1 };
      response.statusCode = 201;
    }
    if (request.method === 'PATCH') {
      if (conflict || body.expectedRevision !== project.revision) {
        response.statusCode = 409;
        return response.end(JSON.stringify({ code: 'project_revision_changed' }));
      }
      if (verifyState) project = { ...project, state: body.state, revision: project.revision + 1 };
      if (loseArchiveResponse) {
        loseArchiveResponse = false;
        request.socket.destroy();
        return;
      }
    }
    response.end(JSON.stringify(project));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const baseUrl = `http://127.0.0.1:${server.address().port}/`;
  const client = new AgentweaverClient({
    baseUrl, tenantId: 'controlled-tenant',
    authProvider: { origin: new URL(baseUrl).origin,
      getAuthorization: async () => ['Bearer', 'credential-canary-owned'].join(' ') },
  });
  await client.discover();
  return {
    client, requests, conflict: () => { conflict = true; },
    loseResponse: () => { loseArchiveResponse = true; }, keepActive: () => { verifyState = false; },
    rename: () => { project.name = 'outside-change'; },
    close: () => new Promise((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
      server.closeAllConnections();
    }),
  };
}

test('actual HTTP creates only a new project and verifies current-revision archive, not deletion or Environment cleanup', async () => {
  const host = await fixture();
  try {
    const owned = await OwnedProjectFixture.create(host.client, { name: ' Controlled owned fixture ' });
    assert.equal(owned.evidence.creation.status, 201);
    const evidence = await owned.archive();
    assert.equal(evidence.cleanupResult, 'verified-archived');
    assert.deepEqual(host.requests.find(request => request.method === 'PATCH').body,
      { expectedRevision: 1, name: 'Controlled owned fixture', state: 'Archived' });
    assert.equal(host.requests.some(request => request.method === 'DELETE'), false);
    const patches = host.requests.filter(request => request.method === 'PATCH').length;
    await owned.archive();
    assert.equal(host.requests.filter(request => request.method === 'PATCH').length, patches);
    assert.doesNotMatch(JSON.stringify(evidence), /credential-canary/);
  } finally { await host.close(); }
});

test('a stale revision and an unconfirmed archive remain failed owner cleanup evidence', async () => {
  for (const fault of ['conflict', 'keepActive']) {
    const host = await fixture();
    try {
      const owned = await OwnedProjectFixture.create(host.client, { name: 'Controlled fixture' });
      host[fault]();
      await assert.rejects(owned.archive());
      assert.equal(owned.evidence.cleanupResult, 'failed');
      assert.ok(owned.evidence.cleanupReceipts.some(call => call.status === (fault === 'conflict' ? 409 : 200)));
    } finally { await host.close(); }
  }
});

test('lost archive response stays failed until a later actual read verifies the same owned project, without another PATCH', async () => {
  const host = await fixture();
  try {
    const owned = await OwnedProjectFixture.create(host.client, { name: 'Controlled fixture' });
    host.loseResponse();
    await assert.rejects(owned.archive());
    assert.equal(owned.evidence.cleanupResult, 'failed');
    await owned.archive();
    assert.equal(owned.evidence.cleanupResult, 'verified-archived');
    assert.equal(host.requests.filter(request => request.method === 'PATCH').length, 1);
  } finally { await host.close(); }
});

test('changed tenant or owner name stops cleanup before a mutation', async () => {
  for (const fault of ['tenant', 'name']) {
    const host = await fixture();
    try {
      const owned = await OwnedProjectFixture.create(host.client, { name: 'Controlled fixture' });
      if (fault === 'tenant') host.client.tenantId = 'foreign-tenant';
      else host.rename();
      await assert.rejects(owned.archive());
      assert.equal(host.requests.some(request => request.method === 'PATCH'), false);
      assert.equal(owned.evidence.cleanupResult, 'failed');
    } finally { await host.close(); }
  }
});

test('a pre-existing project or a missing live lifecycle operation cannot be adopted as an owned fixture', async () => {
  const host = await fixture();
  try {
    assert.throws(() => new OwnedProjectFixture(host.client, { projectId: 'pre-existing-project' },
      { status: 201, operationId: 'createProject' }), /actual created-project receipt/);
    host.client.operations.delete('updateProject');
    await assert.rejects(OwnedProjectFixture.create(host.client, { name: 'Controlled fixture' }), /missing: updateProject/);
    assert.equal(host.requests.some(request => request.method === 'POST'), false);
  } finally { await host.close(); }
});
