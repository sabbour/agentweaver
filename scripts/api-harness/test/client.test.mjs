import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { AgentweaverClient } from '../lib/client.mjs';

const document = {
  openapi: '3.1.0',
  paths: {
    '/api/v1/projects/{projectId}': {
      get: {
        operationId: 'getProject',
        parameters: [{ in: 'path', name: 'projectId', required: true }, { in: 'query', name: 'runId' }],
      },
    },
    '/api/v1/projects': {
      post: { operationId: 'createProject', requestBody: { required: true } },
    },
  },
};

async function fixture() {
  const requests = [];
  let currentDocument = document;
  const server = createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    requests.push({ url: request.url, headers: request.headers, body: Buffer.concat(chunks).toString() });
    if (request.url === '/redirect') {
      response.writeHead(302, { Location: 'https://outside.test/' });
      response.end();
      return;
    }
    response.setHeader('Content-Type', 'application/json');
    response.setHeader('X-Correlation-Id', request.headers['x-correlation-id']);
    response.end(JSON.stringify(request.url === '/openapi/v1.json'
      ? currentDocument
      : { projectId: 'controlled-project', token: 'credential-canary-fixture', inputTokens: 12 }));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const target = `http://127.0.0.1:${server.address().port}/`;
  const client = new AgentweaverClient({
    baseUrl: target, tenantId: 'controlled-tenant',
    authProvider: { origin: new URL(target).origin,
      getAuthorization: async () => ['Bearer', 'credential-canary-fixture'].join(' ') },
  });
  return {
    client, requests, target, setDocument: value => { currentDocument = value; },
    close: () => new Promise((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
      server.closeAllConnections();
    }),
  };
}

test('actual loopback HTTP discovers the contract and retains redacted correlated request/result evidence', async () => {
  const host = await fixture();
  try {
    await host.client.discover();
    const result = await host.client.invoke('getProject', {
      pathParameters: { projectId: 'controlled-project' }, query: { runId: 'controlled-run' },
    });
    assert.equal(host.requests[0].headers.authorization, undefined);
    assert.equal(host.requests[1].headers.authorization, ['Bearer', 'credential-canary-fixture'].join(' '));
    assert.equal(host.requests[1].headers['x-agentweaver-tenant'], 'controlled-tenant');
    assert.equal(result.status, 200);
    assert.equal(result.traceId, result.requestId);
    assert.equal(result.responseBody.inputTokens, 12);
    assert.equal(result.transientResponseBody.token, 'credential-canary-fixture');
    assert.doesNotMatch(JSON.stringify(host.client.calls), /credential-canary-fixture/);
  } finally {
    await host.close();
  }
});

test('invalid tenant or auth metadata and a malformed authorization value fail before HTTP dispatch', async () => {
  const host = await fixture();
  try {
    assert.throws(() => new AgentweaverClient({
      baseUrl: host.target, tenantId: 'controlled-tenant',
      authProvider: { origin: 'https://foreign.test', getAuthorization: async () => 'not-used' },
    }), /exact API origin/);
    assert.throws(() => new AgentweaverClient({
      baseUrl: host.target, tenantId: '', authProvider: host.client.authProvider,
    }), /tenant selector/);
    host.client.authProvider.getAuthorization = async () => 'not-a-broker-bearer';
    await assert.rejects(host.client.call('GET', '/api/v1/projects'), /authorization value/);
    assert.equal(host.requests.length, 0);
  } finally { await host.close(); }
});

test('missing required live query and body fields cannot become an unqualified operation', async () => {
  const host = await fixture();
  try {
    host.setDocument({
      ...document,
      paths: {
        ...document.paths,
        '/api/v1/projects/{projectId}': { get: {
          operationId: 'getProject',
          parameters: [{ in: 'query', name: 'runId', required: true }],
        } },
      },
    });
    await host.client.discover();
    const before = host.requests.length;
    await assert.rejects(host.client.invoke('getProject', {
      pathParameters: { projectId: 'controlled-project' },
    }), /required live-contract query/);
    await assert.rejects(host.client.invoke('createProject'), /request body/);
    assert.equal(host.requests.length, before);
  } finally { await host.close(); }
});

test('cross-origin paths, identity header overrides and undiscovered or changed payload shapes are rejected', async () => {
  const host = await fixture();
  try {
    await host.client.discover();
    const before = host.requests.length;
    await assert.rejects(host.client.call('GET', 'https://outside.test/'), /cross-origin/);
    await assert.rejects(host.client.call('GET', '/', undefined, { headers: { AUTHORIZATION: 'other' } }), /override/);
    await assert.rejects(host.client.invoke('not-discovered'), /not been discovered/);
    await assert.rejects(host.client.invoke('getProject'), /Missing/);
    await assert.rejects(host.client.invoke('getProject', {
      pathParameters: { projectId: 'p' }, query: { foreign: 'value' },
    }), /query/);
    await assert.rejects(host.client.invoke('getProject', {
      pathParameters: { projectId: 'p' }, body: {},
    }), /request body/);
    await assert.rejects(host.client.invoke('createProject'), /request body/);
    assert.equal(host.requests.length, before);
  } finally {
    await host.close();
  }
});

test('a real HTTP redirect fails explicitly and records no success-shaped transport result', async () => {
  const host = await fixture();
  try {
    await assert.rejects(host.client.call('GET', '/redirect'));
    assert.equal(host.client.calls.length, 1);
    assert.equal(host.client.calls[0].status, null);
    assert.ok(host.client.calls[0].error);
  } finally {
    await host.close();
  }
});

test('invalid or duplicated refreshed live contracts invalidate the old operation menu', async () => {
  const host = await fixture();
  try {
    await host.client.discover();
    host.setDocument({ ...document, paths: {
      ...document.paths,
      '/api/v1/duplicate': { get: { operationId: 'getProject' } },
    } });
    await assert.rejects(host.client.discover(), /duplicated/);
    await assert.rejects(host.client.invoke('getProject', { pathParameters: { projectId: 'p' } }), /not been discovered/);
    host.setDocument({ openapi: '3.1.0', paths: {} });
    await assert.rejects(host.client.discover(), /no P1/);
  } finally {
    await host.close();
  }
});
