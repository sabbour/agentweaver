import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { AgentweaverClient } from '../lib/client.mjs';
import { RuntimeObservationClient } from '../lib/runtime-observation-client.mjs';

const operationId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const attemptId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const provisionAttemptId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const cleanupAttemptId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
const runtimeInstanceId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const environmentFence = {
  owner: {
    tenantId: 'tenant-a',
    projectId: 'project-a',
    runId: 'run-a',
    environmentId: 'environment-a',
  },
  lifecycleGeneration: 3,
};
const environmentCreation = {
  snapshot: { fence: environmentFence, state: 'Active' },
  replayed: false,
};
const owner = {
  projectId: 'project-a',
  runId: 'run-a',
  rootSessionId: 'root-a',
  sessionId: 'session-a',
  executionFence: 4,
  tenantId: 'tenant-a',
};
const runtimeObservation = {
  contractVersion: 1,
  registration: {
    state: 'Available',
    value: {
      runtimeInstanceId,
      revision: 2,
      state: 'Active',
      expiresAt: '2026-10-14T12:00:00Z',
    },
  },
  execution: {
    state: 'Available',
    value: {
      contractVersion: 2,
      observation: {
        contractVersion: 1,
        rootSessionId: owner.rootSessionId,
        sessionId: owner.sessionId,
        executionFence: owner.executionFence,
        workPlanId: 'plan-a',
        associationId: 'association-a',
        checkpointRevision: 2,
        checkpointId: 'checkpoint-a',
        checkpointDecisionStateVersion: 1,
        currentDecisionStateVersion: 1,
        status: 'Succeeded',
        resultDigest: { sha256: 'a'.repeat(64), utf8ByteLength: 0 },
      },
      checkpointParent: { hasParentMetadata: true, parentCheckpointId: null },
    },
  },
  creditGuard: { state: 'Unknown', value: null },
  dispatches: { state: 'Available', value: { items: [], truncated: false } },
  toolReservations: { state: 'Available', value: { items: [], truncated: false } },
};
const providerAttempts = {
  contractVersion: 1,
  currentFence: environmentFence,
  operationId,
  attempts: [{
    contractVersion: 1,
    attemptId,
    operationId,
    lifecycleGeneration: 2,
    resourceGeneration: 7,
    providerFencingGeneration: 6,
    effectKind: 'provision',
    state: 'inProgress',
    requestFingerprint: 'b'.repeat(64),
    lateResourceFingerprint: null,
    failureCode: null,
    startedAt: '2026-10-14T10:00:00Z',
    outcomeRecordedAt: null,
    resolvedAt: null,
  }],
};
const lateResourceCleanups = {
  contractVersion: 1,
  currentFence: environmentFence,
  items: [{
    sourceFence: {
      ...environmentFence,
      lifecycleGeneration: 2,
    },
    operationId,
    resourceGeneration: 7,
    providerFencingGeneration: 6,
    currentFencingGeneration: 7,
    resourceFingerprint: 'c'.repeat(64),
    resource: {
      seam: 'sandbox',
      providerId: 'sandbox-provider',
      resourceId: 'sandbox-resource-a',
      generation: 7,
    },
    provisionAttempt: {
      contractVersion: 1,
      attemptId: provisionAttemptId,
      operationId,
      lifecycleGeneration: 2,
      resourceGeneration: 7,
      providerFencingGeneration: 6,
      effectKind: 'provision',
      state: 'resultRecorded',
      requestFingerprint: 'b'.repeat(64),
      lateResourceFingerprint: null,
      failureCode: null,
      startedAt: '2026-10-14T10:00:00Z',
      outcomeRecordedAt: '2026-10-14T10:01:00Z',
      resolvedAt: null,
    },
    releaseAttempt: {
      contractVersion: 1,
      attemptId: cleanupAttemptId,
      operationId,
      lifecycleGeneration: 3,
      resourceGeneration: 7,
      providerFencingGeneration: 6,
      effectKind: 'lateResourceRelease',
      state: 'completed',
      requestFingerprint: 'd'.repeat(64),
      lateResourceFingerprint: 'c'.repeat(64),
      failureCode: null,
      startedAt: '2026-10-14T10:02:00Z',
      outcomeRecordedAt: '2026-10-14T10:03:00Z',
      resolvedAt: '2026-10-14T10:03:00Z',
    },
    createdAt: '2026-10-14T10:01:00Z',
    updatedAt: '2026-10-14T10:03:00Z',
    claimExpiresAt: null,
    releaseReceipt: {
      resource: {
        seam: 'sandbox',
        providerId: 'sandbox-provider',
        resourceId: 'sandbox-resource-a',
        generation: 7,
      },
      idempotencyKey: `late-${'c'.repeat(64)}`,
      disposition: 'released',
    },
    providerObservation: null,
  }],
  hasMore: false,
};

async function fixture({ mutate } = {}) {
  const requests = [];
  let lifecycleReads = 0;
  const server = createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    requests.push({
      url: request.url,
      headers: request.headers,
      body: Buffer.concat(chunks).toString(),
    });
    response.setHeader('Content-Type', 'application/json');
    response.setHeader('Cache-Control', 'private, no-store');
    if (request.url === '/openapi/v1.json') {
      response.end(JSON.stringify({
        openapi: '3.1.0',
        paths: {
          '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-observation/v1': {
            get: {
              operationId: 'readRuntimeObservationV1',
              parameters: [
                { in: 'path', name: 'projectId', required: true },
                { in: 'path', name: 'runId', required: true },
                { in: 'path', name: 'sessionId', required: true },
              ],
            },
          },
        },
      }));
      return;
    }
    if (request.url.startsWith('/api/v1/projects/')) {
      response.end(JSON.stringify(mutate === 'invalid-runtime'
        ? { ...runtimeObservation, unexpected: true }
        : runtimeObservation));
      return;
    }
    if (request.url.endsWith('/sandbox/v1/lifecycle')) {
      lifecycleReads += 1;
      const fence = mutate === 'changed-fence' && lifecycleReads > 1
        ? { ...environmentFence, lifecycleGeneration: 4 }
        : environmentFence;
      response.end(JSON.stringify({ fence, state: 'active' }));
      return;
    }
    if (request.url.endsWith('/provider-attempts')) {
      response.end(JSON.stringify(providerAttempts));
      return;
    }
    if (request.url.endsWith('/late-resource-cleanups')) {
      response.end(JSON.stringify(lateResourceCleanups));
      return;
    }
    response.writeHead(404);
    response.end(JSON.stringify({ error: 'not-found' }));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const baseUrl = `http://127.0.0.1:${server.address().port}/`;
  const api = new AgentweaverClient({
    baseUrl,
    tenantId: owner.tenantId,
    authProvider: {
      origin: new URL(baseUrl).origin,
      getAuthorization: async () => 'Bearer loopback-test-token',
    },
  });
  return {
    api,
    requests,
    close: () => new Promise((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
      server.closeAllConnections();
    }),
  };
}

test('reads current runtime and Environment owner evidence with exact paths, fences, and no-store', async () => {
  const host = await fixture();
  try {
    await host.api.discover();
    const consumer = new RuntimeObservationClient({ api: host.api });
    const runtime = await consumer.readRuntimeObservationV1(owner);
    const environment = await consumer.readEnvironmentEvidence({
      environmentCreation,
      owner,
      operationId,
    });

    assert.equal(runtime.creditGuard.state, 'Unknown');
    assert.equal(runtime.execution.value.observation.sessionId, owner.sessionId);
    assert.equal(environment.environmentId, 'environment-a');
    assert.equal(environment.fence.lifecycleGeneration, 3);
    assert.equal(environment.providerAttempts.attempts[0].state, 'inProgress');
    assert.equal(environment.lateResourceCleanups.items[0].resource.resourceId, 'sandbox-resource-a');
    assert.equal(environment.lateResourceCleanups.items[0].releaseReceipt.disposition, 'released');
    assert.equal(environment.lateResourceCleanups.hasMore, false);
    const requests = host.requests.slice(1);
    assert.ok(requests.every(request => request.headers['cache-control'] === 'no-store'));
    assert.ok(requests.some(request => request.url ===
      '/api/v1/projects/project-a/runs/run-a/coordination/sessions/session-a/runtime-observation/v1'));
    assert.ok(requests.some(request => request.url.endsWith(
      `/environments/environment-a/sandbox/v1/operations/${operationId}/provider-attempts`)));
    assert.ok(requests.some(request => request.url.endsWith(
      '/environments/environment-a/sandbox/v1/late-resource-cleanups')));
    assert.doesNotMatch(JSON.stringify(host.api.calls), /loopback-test-token/);
  } finally {
    await host.close();
  }
});

test('rejects malformed runtime observations and Environment fence changes', async () => {
  const malformed = await fixture({ mutate: 'invalid-runtime' });
  try {
    await malformed.api.discover();
    const consumer = new RuntimeObservationClient({ api: malformed.api });
    await assert.rejects(consumer.readRuntimeObservationV1(owner), /runtime observation contract/);
  } finally {
    await malformed.close();
  }

  const changed = await fixture({ mutate: 'changed-fence' });
  try {
    await changed.api.discover();
    const consumer = new RuntimeObservationClient({ api: changed.api });
    await assert.rejects(consumer.readEnvironmentEvidence({
      environmentCreation,
      owner,
      operationId,
    }), /changed while Environment evidence/);
  } finally {
    await changed.close();
  }
});
