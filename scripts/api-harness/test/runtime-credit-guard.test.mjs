import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import {
  RUNTIME_CREDIT_GUARD_READ_CONTRACT,
  RUNTIME_CREDIT_GUARD_READ_PATH,
  validateRuntimeCreditGuardReadRequest,
} from '../lib/runtime-credit-guard-contract.mjs';
import { RuntimeCreditGuardClient } from '../lib/runtime-credit-guard-client.mjs';

const intent = {
  contractVersion: 1,
  runtimeInstanceId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  registrationRevision: 3,
  tenantId: 'controlled-tenant',
  projectId: 'controlled-project',
  runId: 'controlled-run',
  rootSessionId: 'controlled-root',
  acceptedSelectionHash: 'a'.repeat(64),
  executionFence: 8,
  softCreditLimit: 10,
  hardCreditLimit: 20,
};

const request = {
  authorization: {
    grantId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
    runtimeInstanceId: intent.runtimeInstanceId,
    revision: 5,
    purpose: 'Observe',
    audience: 'https://orchestrator.example.test',
    configurationHash: 'b'.repeat(64),
    credentialValue: 'credential-canary-fixture',
    credentialExpiresAt: '2099-01-01T00:00:00.000Z',
    operationId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  },
  intent,
};

function snapshot(overrides = {}) {
  return {
    intent,
    registrationHash: 'd'.repeat(64),
    costIdentity: 'e'.repeat(64),
    costUnit: 'AIC',
    costScope: 'root-run',
    copilotAicFloor: 3.25,
    accountedThroughLedgerEventCount: 17,
    ledgerDigest: 'f'.repeat(64),
    guardRevision: 4,
    softWarningEmitted: false,
    hardCapExhausted: false,
    updatedAt: '2026-10-13T18:00:00.000Z',
    canonicalPayloadHash: '1'.repeat(64),
    ...overrides,
  };
}

async function fixture() {
  const requests = [];
  let response = { status: 200, body: snapshot() };
  const server = createServer(async (incoming, outgoing) => {
    const chunks = [];
    for await (const chunk of incoming) chunks.push(chunk);
    requests.push({
      method: incoming.method,
      url: incoming.url,
      headers: incoming.headers,
      body: Buffer.concat(chunks).toString(),
    });
    if (response.status === 302) {
      outgoing.writeHead(302, { Location: 'https://outside.test/' });
      outgoing.end();
      return;
    }
    outgoing.writeHead(response.status, {
      'Cache-Control': response.cacheControl ?? 'no-store',
      'Content-Type': response.status === 200 ? 'application/json' : 'application/problem+json',
      'X-Correlation-Id': incoming.headers['x-correlation-id'],
    });
    outgoing.end(JSON.stringify(response.body));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const baseUrl = `http://127.0.0.1:${server.address().port}/`;
  const authProvider = {
    origin: new URL(baseUrl).origin,
    getAuthorization: async url => {
      assert.equal(url.origin, new URL(baseUrl).origin);
      return 'Bearer actor-canary-fixture';
    },
  };
  const client = new RuntimeCreditGuardClient({ baseUrl, authProvider });
  return {
    client,
    requests,
    baseUrl,
    setResponse: value => { response = value; },
    close: () => new Promise((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
      server.closeAllConnections();
    }),
  };
}

test('static contract documents only the direct Orchestrator POST and its no-store owner responses', () => {
  const contract = RUNTIME_CREDIT_GUARD_READ_CONTRACT;
  assert.equal(contract.openapi, '3.1.0');
  assert.deepEqual(Object.keys(contract.paths), [RUNTIME_CREDIT_GUARD_READ_PATH]);
  assert.deepEqual(Object.keys(contract.paths[RUNTIME_CREDIT_GUARD_READ_PATH]), ['post']);
  const operation = contract.paths[RUNTIME_CREDIT_GUARD_READ_PATH].post;
  assert.deepEqual(operation.security, [{ ProjectsActor: [] }]);
  assert.equal(operation.operationId, 'readRuntimeCreditGuardSnapshot');
  for (const response of Object.values(operation.responses)) {
    assert.equal(response.headers['Cache-Control'].required, true);
    assert.match(response.headers['Cache-Control'].schema.pattern, /no-store/);
  }
  assert.equal(
    operation.responses['404'].content['application/problem+json'].schema.properties.code.const,
    'runtime_credit_guard_unknown',
  );
  assert.equal(contract.paths['/openapi/v1.json'], undefined);
  assert.equal(contract.paths['/api/v1/runtime/credit-guards/read'], undefined);
  assert.deepEqual(
    contract.components.schemas.RuntimeCreditGuardReadRequest.required,
    ['authorization', 'intent'],
  );
  assert.deepEqual(
    contract.components.schemas.RuntimeCreditGuardSnapshot.required,
    [
      'intent', 'registrationHash', 'costIdentity', 'costUnit', 'costScope',
      'copilotAicFloor', 'accountedThroughLedgerEventCount', 'ledgerDigest',
      'guardRevision', 'softWarningEmitted', 'hardCapExhausted', 'updatedAt',
      'canonicalPayloadHash',
    ],
  );
});

test('controlled loopback reads the exact request using the injected Orchestrator actor and retains redacted evidence', async () => {
  const host = await fixture();
  try {
    const result = await host.client.read(request);
    const sent = host.requests[0];
    assert.equal(sent.method, 'POST');
    assert.equal(sent.url, RUNTIME_CREDIT_GUARD_READ_PATH);
    assert.equal(sent.headers.authorization, 'Bearer actor-canary-fixture');
    assert.equal(sent.headers['cache-control'], 'no-store');
    assert.equal(sent.headers['x-agentweaver-tenant'], undefined);
    assert.deepEqual(JSON.parse(sent.body), request);
    assert.equal(result.status, 200);
    assert.equal(result.responseBody.costUnit, 'AIC');
    assert.equal(result.cacheControl, 'no-store');
    assert.equal(result.traceId, result.requestId);
    assert.ok(Object.values(result.requestBody.authorization).every(value => value === '[REDACTED]'));
    assert.doesNotMatch(JSON.stringify(host.client.calls), /credential-canary-fixture|actor-canary-fixture/);
    assert.doesNotMatch(JSON.stringify(result), /credential-canary-fixture|actor-canary-fixture/);
  } finally {
    await host.close();
  }
});

test('intent matching is structural rather than dependent on JSON property order', async () => {
  const host = await fixture();
  try {
    host.setResponse({
      status: 200,
      body: snapshot({ intent: Object.fromEntries(Object.entries(intent).reverse()) }),
      cacheControl: 'private, no-store',
    });
    const result = await host.client.read(request);
    assert.equal(result.status, 200);
    assert.equal(result.cacheControl, 'private, no-store');
  } finally {
    await host.close();
  }
});

test('unknown guard remains an explicit redacted 404 and is never represented as a snapshot', async () => {
  const host = await fixture();
  try {
    host.setResponse({
      status: 404,
      body: {
        code: 'runtime_credit_guard_unknown',
        details: { credentialValue: 'credential-canary-fixture' },
      },
    });
    const result = await host.client.read(request);
    assert.equal(result.status, 404);
    assert.equal(result.responseBody.code, 'runtime_credit_guard_unknown');
    assert.equal(result.responseBody.details.credentialValue, '[REDACTED]');
    assert.equal(Object.hasOwn(result, 'snapshot'), false);
    assert.equal(host.client.calls[0].status, 404);
    assert.doesNotMatch(JSON.stringify(host.client.calls), /credential-canary-fixture|actor-canary-fixture/);
  } finally {
    await host.close();
  }
});

test('missing response no-store is an explicit contract failure with status and body evidence retained', async () => {
  const host = await fixture();
  try {
    host.setResponse({ status: 200, body: snapshot(), cacheControl: 'public, max-age=30' });
    await assert.rejects(host.client.read(request), /did not satisfy no-store/);
    assert.equal(host.client.calls[0].status, 200);
    assert.equal(host.client.calls[0].responseBody.costUnit, 'AIC');
    assert.equal(host.client.calls[0].cacheControl, 'public, max-age=30');
  } finally {
    await host.close();
  }
});

test('a 200 response with a different intent is rejected while its status and redacted evidence remain available', async () => {
  const host = await fixture();
  try {
    host.setResponse({
      status: 200,
      body: snapshot({ intent: { ...intent, executionFence: intent.executionFence + 1 } }),
    });
    await assert.rejects(host.client.read(request), /snapshot does not match/);
    assert.equal(host.client.calls[0].status, 200);
    assert.equal(host.client.calls[0].responseBody.intent.executionFence, intent.executionFence + 1);
    assert.doesNotMatch(JSON.stringify(host.client.calls), /credential-canary-fixture|actor-canary-fixture/);
  } finally {
    await host.close();
  }
});

test('invalid proof, foreign actor origin, and malformed actor authorization fail without HTTP dispatch', async () => {
  const host = await fixture();
  try {
    assert.throws(() => validateRuntimeCreditGuardReadRequest({
      ...request,
      authorization: { ...request.authorization, purpose: 'Configure' },
    }), /authorization/);
    assert.throws(() => new RuntimeCreditGuardClient({
      baseUrl: host.baseUrl,
      authProvider: { origin: 'https://foreign.test', getAuthorization: async () => 'unused' },
    }), /exact Orchestrator origin/);
    host.client.authProvider.getAuthorization = async () => 'not-a-bearer';
    await assert.rejects(host.client.read(request), /authorization value/);
    assert.equal(host.requests.length, 0);
    assert.equal(host.client.calls[0].status, null);
    assert.doesNotMatch(JSON.stringify(host.client.calls), /credential-canary-fixture/);
  } finally {
    await host.close();
  }
});

test('a redirected owner request fails explicitly without a success-shaped result', async () => {
  const host = await fixture();
  try {
    host.setResponse({ status: 302, body: {} });
    await assert.rejects(host.client.read(request));
    assert.equal(host.requests[0].url, RUNTIME_CREDIT_GUARD_READ_PATH);
    assert.equal(host.client.calls[0].status, null);
    assert.ok(host.client.calls[0].error);
    assert.equal(Object.hasOwn(host.client.calls[0], 'responseBody'), true);
    assert.equal(host.client.calls[0].responseBody, null);
  } finally {
    await host.close();
  }
});
