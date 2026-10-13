import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { once } from 'node:events';
import { AgentweaverClient } from '../lib/client.mjs';
import { runP1ApiJourney } from '../lib/p1-journey.mjs';
import { runP1McpJourney } from '../../mcp-harness/lib/p1-journey.mjs';
import { runP1UiJourney } from '../../ui-harness/lib/p1-journey.mjs';

const IDS = {
  projectId: 'controlled-project',
  runId: 'controlled-run',
  rootSessionId: 'controlled-root',
  sessionId: 'controlled-session',
  executionFence: 9,
};
const SOURCE_SHA = 'a'.repeat(40);
const SOURCE_TREE = 'b'.repeat(40);
const PROVIDER_SOURCE = 'c'.repeat(40);
const HOST_REGISTRY_DIGEST = `sha256:${'d'.repeat(64)}`;
const HOST_ARCHIVE_SHA = 'e'.repeat(64);
const HOST_MANIFEST_DIGEST = `sha256:${'f'.repeat(64)}`;
const HOST_CONFIG_DIGEST = `sha256:${'1'.repeat(64)}`;
const P1_SERVICE_COMPONENTS = [
  ['Agentweaver.Identity.Broker', 'identity-broker'],
  ['Agentweaver.EventsAndSessions', 'events-and-sessions'],
  ['Agentweaver.Gateway', 'gateway'],
  ['Agentweaver.Mcp', 'mcp'],
  ['Agentweaver.Web', 'web'],
  ['Agentweaver.Orchestrator', 'orchestrator'],
  ['Agentweaver.Environment', 'environment'],
  ['Agentweaver.Knowledge', 'knowledge'],
  ['Agentweaver.Projects.Config', 'projects-config'],
];

function routePath(suffix = '') {
  return `/api/v1/projects/{projectId}/runs/{runId}/coordination${suffix}`;
}

function openApiDocument() {
  const paths = {
    [routePath('/status')]: { get: { operationId: 'readRunStatus' } },
    [routePath('/sessions/{sessionId}/status')]: { get: { operationId: 'readSessionStatus' } },
    [routePath('/sessions/{sessionId}/decisions')]: { get: { operationId: 'readDecisions' } },
    [routePath('/sessions/{sessionId}/tree')]: { get: { operationId: 'readSessionTree' } },
    [routePath('/sessions/{sessionId}/decisions/gates/{requestId}/answer')]: {
      post: { operationId: 'answerGate', requestBody: { required: true } },
    },
    [routePath('/sessions/{sessionId}/decisions/gates/{requestId}/approve')]: {
      post: { operationId: 'approveGate', requestBody: { required: true } },
    },
    [routePath('/sessions/{sessionId}/decisions/gates/{requestId}/reject')]: {
      post: { operationId: 'rejectGate', requestBody: { required: true } },
    },
    [routePath('/sessions/{sessionId}/messages')]: {
      post: { operationId: 'sendMessage', requestBody: { required: true } },
    },
    '/api/v1/projects/{projectId}/runs/{runId}/events': {
      get: {
        operationId: 'replayRunEvents',
        parameters: [
          { name: 'cursor', in: 'query' },
          { name: 'limit', in: 'query' },
        ],
      },
    },
  };
  return { openapi: '3.1.0', paths };
}

function eventPage(events = [{
  eventId: '00000000-0000-4000-8000-000000000001',
  identity: {
    projectId: IDS.projectId,
    runId: IDS.runId,
    sessionId: IDS.sessionId,
  },
  position: 1,
}], { nextCursor = 'cursor-1', hasMore = false } = {}) {
  return { events, nextCursor, hasMore };
}

async function host({
  deniedStatus = 403,
  gate = true,
  eventPages = [eventPage()],
  apiEventPages = eventPages,
  replayOperation = true,
} = {}) {
  const requests = [];
  let resolvedGate = false;
  let apiPageIndex = 0;
  const server = createServer(async (request, response) => {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const body = chunks.length ? JSON.parse(Buffer.concat(chunks).toString()) : null;
    requests.push({
      method: request.method,
      url: request.url,
      authorization: request.headers.authorization,
      body,
    });
    response.setHeader('Content-Type', 'application/json');
    response.setHeader('X-Correlation-Id', request.headers['x-correlation-id'] ?? 'controlled-trace');
    const url = new URL(request.url, 'http://localhost');
    if (url.pathname === '/openapi/v1.json') {
      const document = openApiDocument();
      if (!replayOperation) delete document.paths['/api/v1/projects/{projectId}/runs/{runId}/events'];
      response.end(JSON.stringify(document));
      return;
    }
    if (url.pathname === '/api/v1/projects/controlled-project/runs/controlled-run/events') {
      const page = apiEventPages[apiPageIndex++] ?? apiEventPages.at(-1);
      response.end(JSON.stringify(page));
      return;
    }
    if (request.headers.authorization === 'Bearer denied') {
      response.statusCode = deniedStatus;
      response.end(JSON.stringify({ code: 'forbidden' }));
      return;
    }
    const isRunStatus = url.pathname.endsWith('/coordination/status');
    if (isRunStatus) {
      response.end(JSON.stringify({
        projectId: IDS.projectId,
        runId: IDS.runId,
        rootSessionId: IDS.rootSessionId,
        executionFence: IDS.executionFence,
        logicalTurnOrdinal: 2,
        executionState: 'active',
        stateVersion: 4,
      }));
      return;
    }
    if (url.pathname.endsWith('/coordination/sessions/controlled-session/status')) {
      response.end(JSON.stringify({
        identity: { projectId: IDS.projectId, runId: IDS.runId, sessionId: IDS.sessionId },
        rootSessionId: IDS.rootSessionId,
        executionFence: IDS.executionFence,
        blockers: gate && !resolvedGate ? [{
          kind: 'awaitingInput', requestId: 'controlled-gate', choices: ['yes'],
          allowsFreeform: false, prompt: null,
        }] : [],
      }));
      return;
    }
    if (url.pathname.endsWith('/coordination/sessions/controlled-session/decisions')) {
      response.end(JSON.stringify({
        executionFence: IDS.executionFence,
        stateVersion: resolvedGate ? 6 : 5,
        pendingGate: gate && !resolvedGate ? {
          requestId: 'controlled-gate',
          kind: 'question',
          subjectId: 'controlled-subject',
          authorizedActorId: 'allowed-actor',
          fence: IDS.executionFence,
          allowedChoices: ['yes'],
          allowsFreeform: false,
        } : null,
      }));
      return;
    }
    if (url.pathname.endsWith('/coordination/sessions/controlled-root/tree')) {
      response.end(JSON.stringify({
        rootSessionId: IDS.rootSessionId,
        nodes: [IDS.rootSessionId, IDS.sessionId, 'controlled-recipient'].map(sessionId => ({
          identity: { projectId: IDS.projectId, runId: IDS.runId, sessionId },
          lifecycle: 'active',
        })),
      }));
      return;
    }
    if (url.pathname.endsWith('/coordination/sessions/controlled-session/decisions/gates/controlled-gate/answer')) {
      resolvedGate = true;
      response.end(JSON.stringify({
        accepted: true,
        decisionId: 'controlled-decision',
        stateVersion: 6,
        executionFence: IDS.executionFence,
        issues: [],
      }));
      return;
    }
    if (url.pathname.endsWith('/coordination/sessions/controlled-session/messages')) {
      response.statusCode = 202;
      response.end(JSON.stringify({
        ownerMessageId: 'controlled-message',
        recipientSessionId: 'controlled-recipient',
        status: 'accepted',
        requestId: null,
      }));
      return;
    }
    response.statusCode = 404;
    response.end(JSON.stringify({ code: 'not_found' }));
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const baseUrl = `http://127.0.0.1:${server.address().port}/`;
  const makeClient = (token, tenantId) => new AgentweaverClient({
    baseUrl,
    tenantId,
    authProvider: {
      origin: new URL(baseUrl).origin,
      issuer: 'https://identity.controlled.test/',
      audience: 'agentweaver-controlled',
      actorId: token === 'allowed' ? 'allowed-actor' : 'denied-actor',
      getAuthorization: async () => `Bearer ${token}`,
    },
  });
  const toolNames = [
    'readRunStatus', 'readSessionStatus', 'readDecisions', 'readSessionTree',
    'answerGate', 'approveGate', 'rejectGate', 'sendMessage', 'replayRunEvents',
  ];
  let pageIndex = 0;
  let rpcId = 0;
  const mcp = {
    target: new URL('/mcp', baseUrl),
    tenantId: 'controlled-tenant',
    authProvider: {
      origin: new URL(baseUrl).origin,
      issuer: 'https://identity.controlled.test/',
      audience: 'agentweaver-controlled',
      actorId: 'allowed-actor',
    },
    tools: new Map(toolNames.map(name => [`agentweaver_${name}`, {
      inputSchema: {
        type: 'object',
        properties: {
          projectId: { type: 'string' },
          runId: { type: 'string' },
          sessionId: { type: 'string' },
          requestId: { type: 'string' },
          cursor: { type: 'string' },
          limit: { type: 'integer' },
          tenantSelector: { type: 'string' },
          body: { type: 'object' },
        },
        required: ['projectId', 'runId', 'tenantSelector'],
        additionalProperties: false,
      },
    }])),
    protocol: [],
    async discoverTools() {},
    async callTool(toolName, args) {
      const id = ++rpcId;
      this.protocol.push({
        direction: 'request',
        message: { id, method: 'tools/call', params: { name: toolName, arguments: args } },
      });
      let status = 200;
      let ownerResponse = eventPages[pageIndex++] ?? eventPages.at(-1);
      if (toolName === 'agentweaver_answerGate') {
        resolvedGate = true;
        ownerResponse = {
          accepted: true,
          decisionId: 'controlled-mcp-decision',
          stateVersion: 6,
          executionFence: IDS.executionFence,
          issues: [],
        };
      } else if (toolName === 'agentweaver_sendMessage') {
        status = 202;
        ownerResponse = {
          ownerMessageId: 'controlled-mcp-message',
          recipientSessionId: 'controlled-recipient',
          status: 'accepted',
          requestId: null,
        };
      }
      const result = {
        isError: false,
        structuredContent: { status, ownerResponse },
      };
      this.protocol.push({ direction: 'response', message: { id, result } });
      return { toolName, result, transientResult: result };
    },
  };
  let uiOpened = false;
  let uiClosed = false;
  let uiReplayCount = 0;
  const ui = {
    target: new URL(baseUrl),
    gateway: new URL('/api/v1', baseUrl),
    tenantId: 'controlled-tenant',
    identity: {
      issuer: 'https://identity.controlled.test/',
      audience: 'agentweaver-controlled',
      actorId: 'allowed-actor',
    },
    async openRun() { uiOpened = true; },
    async gate() {
      resolvedGate = true;
      return {
        status: 200,
        transientResponseBody: {
          accepted: true,
          decisionId: 'controlled-ui-decision',
          stateVersion: 6,
          executionFence: IDS.executionFence,
          issues: [],
        },
      };
    },
    async sendMessage() {
      return {
        status: 202,
        transientResponseBody: {
          ownerMessageId: 'controlled-ui-message',
          recipientSessionId: 'controlled-recipient',
          status: 'accepted',
        },
      };
    },
    async replayJournal() {
      uiReplayCount++;
      return { events: [eventPage().events[0]], pageCount: 1, transcriptContentAvailable: false };
    },
    async close() { uiClosed = true; },
  };
  return {
    baseUrl,
    requests,
    api: makeClient('allowed', 'controlled-tenant'),
    deniedApi: makeClient('denied', 'denied-tenant'),
    mcp,
    ui,
    uiState: () => ({ opened: uiOpened, closed: uiClosed }),
    uiReplayCount: () => uiReplayCount,
    close: () => new Promise((resolve, reject) => {
      server.close(error => error ? reject(error) : resolve());
      server.closeAllConnections();
    }),
  };
}

function input(baseUrl, journey = 'journal') {
  const invocationId = 'controlled-acceptance-invocation';
  const scope = {
    kind: 'p1-runner-owned',
    status: 'ready',
    invocationId,
    fixtureId: 'controlled-fixture',
    ...IDS,
    expiresAt: '2026-10-14T00:00:00.000Z',
    allowedJourneys: [journey],
    deniedActor: { actorId: 'denied-actor', tenantId: 'denied-tenant' },
    ownershipReceipts: [IDS.projectId, IDS.runId, IDS.rootSessionId, IDS.sessionId].map(resourceId => ({
      resourceType: 'owner-resource',
      resourceId,
      invocationId,
      creationReceipt: { resourceId, created: true },
      observedReceipt: { resourceId, state: 'active' },
      ownerFence: IDS.executionFence,
    })),
  };
  if (journey.startsWith('gate-')) scope.gate = { requestId: 'controlled-gate', answer: 'yes' };
  if (journey === 'message') {
    scope.message = {
      senderSessionId: IDS.sessionId,
      recipientSessionId: 'controlled-recipient',
      text: 'P1 controlled message',
    };
  }
  return {
    executionIntent: 'execute',
    journey,
    deploymentEvidence: {
      publicationPath: 'artifacts/p1/publication.json',
      manifestPath: 'releases/foundation.json',
      agentHostArchivePath: 'artifacts/p1/Agentweaver.AgentHost.oci.tar',
      integrationDeploymentReceiptPath: 'artifacts/p1/integration-deployment.json',
    },
    environment: {
      baseUrl,
      issuer: 'https://identity.controlled.test/',
      audience: 'agentweaver-controlled',
      actorId: 'allowed-actor',
      tenantId: 'controlled-tenant',
    },
    pins: {
      context: 'p1-control',
      namespace: 'agentweaver-v1-p0',
      providerReceipt: {
        configMapName: 'provider-catalog-v1',
        sourceCommit: PROVIDER_SOURCE,
        uid: 'controlled-provider-uid',
        resourceVersion: '12',
      },
      runtimeReceipt: { status: 'passed' },
      storageReceipt: { status: 'passed' },
      sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
      imageReceipt: {
        registryImage: `ghcr.io/sabbour/agentweaver/agenthost@${HOST_REGISTRY_DIGEST}`,
        registryDigest: HOST_REGISTRY_DIGEST,
        archiveSha256: HOST_ARCHIVE_SHA,
        manifestDigest: HOST_MANIFEST_DIGEST,
        configDigest: HOST_CONFIG_DIGEST,
        compressedPullBytes: 1024,
      },
      ownerFences: IDS,
      fixtureScope: scope,
    },
  };
}

const SOURCE_EVIDENCE = {
  source: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
  integrationDeployment: {
    status: 'deployed',
    sourceSha: SOURCE_SHA,
    sourceTree: SOURCE_TREE,
    manifestSha256: 'a'.repeat(64),
    publicationSha256: 'b'.repeat(64),
    readbackSha256: 'c'.repeat(64),
    completedAt: '2026-10-13T00:00:00.000Z',
    target: { clusterName: 'controlled-cluster', namespace: 'agentweaver-v1-p0' },
  },
  providerCatalog: {
    name: 'provider-catalog-v1',
    sourceCommit: PROVIDER_SOURCE,
    uid: 'controlled-provider-uid',
    resourceVersion: '12',
  },
  services: {
    status: 'passed',
    observedAt: '2026-10-13T00:00:00.000Z',
    sourceSha: SOURCE_SHA,
    sourceTree: SOURCE_TREE,
    context: 'p1-control',
    namespace: 'agentweaver-v1-p0',
    providerCatalog: {
      name: 'provider-catalog-v1',
      sourceCommit: PROVIDER_SOURCE,
      uid: 'controlled-provider-uid',
      resourceVersion: '12',
    },
    services: P1_SERVICE_COMPONENTS.map(([componentId, name]) => ({
      componentId,
      deployment: {
        name,
        uid: `deployment-${name}-uid`,
        generation: 2,
        observedGeneration: 2,
        desiredReplicas: 1,
      },
      replicaSet: { name: `${name}-replicaset`, uid: `replicaset-${name}-uid` },
      pods: [{
        name: `${name}-pod`,
        uid: `pod-${name}-uid`,
        phase: 'Running',
        ready: true,
        image: `ghcr.io/sabbour/agentweaver/${name}@sha256:${'2'.repeat(64)}`,
        imageID: `ghcr.io/sabbour/agentweaver/${name}@sha256:${'2'.repeat(64)}`,
        nodeName: 'controlled-node',
        registry: {
          manifestDigest: `sha256:${'2'.repeat(64)}`,
          platformDigest: `sha256:${'2'.repeat(64)}`,
          configDigest: `sha256:${'3'.repeat(64)}`,
          sourceSha: SOURCE_SHA,
          sourceTree: SOURCE_TREE,
          platform: 'linux/amd64',
        },
      }],
    })),
  },
  agentHostImageMeasurement: {
    status: 'passed',
    kind: 'agenthost-image',
    schemaVersion: 1,
    sourceSha: SOURCE_SHA,
    sourceTree: SOURCE_TREE,
    image: {
      digest: HOST_MANIFEST_DIGEST,
      configDigest: HOST_CONFIG_DIGEST,
      platform: 'linux/amd64',
      compressedPullBytes: 1024,
    },
    registryImage: `ghcr.io/sabbour/agentweaver/agenthost@${HOST_REGISTRY_DIGEST}`,
    registryDigest: HOST_REGISTRY_DIGEST,
    archiveSha256: HOST_ARCHIVE_SHA,
    compressedPullBytes: 1024,
  },
  agentHostRuntime: { status: 'blocked', reason: 'controlled fixture' },
};
const CLOCK = () => Date.parse('2026-10-13T00:00:00.000Z');

test('P1 API journal journey checks a real denied-actor 403 and correlated Gateway replay evidence', async () => {
  const fixture = await host();
  try {
    const result = await runP1ApiJourney(input(fixture.baseUrl), {
      ...fixture,
      mcp: undefined,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surfaceStatus, 'passed');
    assert.equal(result.fullP1Status, 'blocked');
    assert.equal(result.sourceEvidence.integrationDeployment.status, 'deployed');
    assert.deepEqual(
      result.sourceEvidence.services.services.map(service => service.componentId),
      P1_SERVICE_COMPONENTS.map(([componentId]) => componentId),
    );
    assert.deepEqual(result.observations.journal, { eventCount: 1, pageCount: 1 });
    assert.ok(result.steps.some(step => step.id === 'valid-denied-actor-403' && step.httpStatus === 403));
    const journalStep = result.steps.find(step => step.id === 'committed-run-journal');
    assert.equal(journalStep.operationId, 'replayRunEvents');
    assert.ok(journalStep.correlationId);
    assert.ok(fixture.requests.some(request =>
      request.method === 'GET' && request.url.startsWith('/api/v1/projects/controlled-project/runs/controlled-run/events')));
    assert.equal(fixture.mcp.protocol.some(item =>
      item.message?.params?.name === 'agentweaver_replayRunEvents'), false);
    assert.doesNotMatch(JSON.stringify(result), /Bearer|allowed-actor|controlled-tenant/);
    assert.ok(fixture.requests.some(request => request.authorization === 'Bearer denied' && request.method === 'GET'));
  } finally {
    await fixture.close();
  }
});

test('P1 API journal follows the discovered cursor chain across Gateway pages', async () => {
  const secondEvent = {
    ...eventPage().events[0],
    eventId: '00000000-0000-4000-8000-000000000002',
    position: 2,
  };
  const fixture = await host({
    apiEventPages: [
      eventPage(undefined, { nextCursor: 'cursor-1', hasMore: true }),
      eventPage([secondEvent], { nextCursor: 'cursor-2', hasMore: false }),
    ],
  });
  try {
    const result = await runP1ApiJourney(input(fixture.baseUrl), {
      ...fixture,
      mcp: undefined,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surfaceStatus, 'passed');
    assert.deepEqual(result.observations.journal, { eventCount: 2, pageCount: 2 });
    const replayRequests = fixture.requests.filter(request => request.url.includes('/runs/controlled-run/events'));
    assert.equal(replayRequests.length, 2);
    assert.equal(new URL(replayRequests[0].url, fixture.baseUrl).searchParams.get('limit'), '100');
    assert.equal(new URL(replayRequests[0].url, fixture.baseUrl).searchParams.has('cursor'), false);
    assert.equal(new URL(replayRequests[1].url, fixture.baseUrl).searchParams.get('cursor'), 'cursor-1');
    assert.equal(fixture.mcp.protocol.length, 0);
  } finally {
    await fixture.close();
  }
});

test('P1 API journal blocks a missing Gateway operation and foreign or invalid page evidence', async () => {
  const missing = await host({ replayOperation: false });
  try {
    const result = await runP1ApiJourney(input(missing.baseUrl), {
      ...missing,
      mcp: undefined,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surfaceStatus, 'blocked');
    assert.match(result.failure.message, /api:replayRunEvents/);
    assert.equal(missing.requests.some(request => request.url.includes('/runs/controlled-run/events')), false);
    assert.equal(missing.mcp.protocol.length, 0);
  } finally {
    await missing.close();
  }

  const event = eventPage().events[0];
  for (const apiEventPages of [
    [eventPage([{ ...event, identity: { ...event.identity, runId: 'foreign-run' } }])],
    [eventPage([{ ...event, eventId: 'not-a-guid' }])],
    [
      eventPage(undefined, { nextCursor: 'cursor-1', hasMore: true }),
      eventPage([{ ...event, eventId: '00000000-0000-4000-8000-000000000002', position: 2 }], {
        nextCursor: 'cursor-1',
        hasMore: false,
      }),
    ],
  ]) {
    const fixture = await host({ apiEventPages });
    try {
      const result = await runP1ApiJourney(input(fixture.baseUrl), {
        ...fixture,
        mcp: undefined,
        sourceEvidence: SOURCE_EVIDENCE,
        clock: CLOCK,
      });
      assert.equal(result.surfaceStatus, 'failed');
      assert.match(result.failure.message, /foreign, repeated, or reordered event identity|cursor did not advance/);
    } finally {
      await fixture.close();
    }
  }
});

test('P1 API gate answer blocks caller-supplied ownership receipts before network access', async () => {
  const fixture = await host();
  try {
    const result = await runP1ApiJourney(input(fixture.baseUrl, 'gate-answer'), {
      ...fixture,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surfaceStatus, 'blocked');
    assert.match(result.failure.message, /fixture created by this runner invocation/);
    assert.equal(fixture.requests.length, 0);
    assert.equal(fixture.requests.some(request => request.method === 'POST'), false);
  } finally {
    await fixture.close();
  }
});

test('P1 API message blocks caller-supplied ownership receipts before network access', async () => {
  const fixture = await host();
  try {
    const result = await runP1ApiJourney(input(fixture.baseUrl, 'message'), {
      ...fixture,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surfaceStatus, 'blocked');
    assert.match(result.failure.message, /fixture created by this runner invocation/);
    assert.equal(fixture.requests.length, 0);
    assert.equal(fixture.requests.some(request => request.method === 'POST'), false);
  } finally {
    await fixture.close();
  }
});

test('expired fixture input blocks before any network or MCP call', async () => {
  const fixture = await host();
  try {
    const requestCount = fixture.requests.length;
    let discoveryCalls = 0;
    fixture.mcp.discoverTools = async () => { discoveryCalls++; };
    const config = input(fixture.baseUrl);
    config.pins.fixtureScope.expiresAt = '2026-10-12T23:59:59.000Z';
    const result = await runP1ApiJourney(config, {
      ...fixture,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surfaceStatus, 'blocked');
    assert.equal(fixture.requests.length, requestCount);
    assert.equal(discoveryCalls, 0);
  } finally {
    await fixture.close();
  }
});

test('a denied-actor 401 is not mistaken for a valid forbidden result', async () => {
  const fixture = await host({ deniedStatus: 401 });
  try {
    const result = await runP1ApiJourney(input(fixture.baseUrl), {
      ...fixture,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surfaceStatus, 'failed');
    assert.match(result.failure.message, /HTTP 403 was required/);
  } finally {
    await fixture.close();
  }
});

test('the MCP runner rejects caller ownership claims before tool discovery', async () => {
  const fixture = await host();
  try {
    let discoveryCalls = 0;
    fixture.mcp.discoverTools = async () => { discoveryCalls++; };
    const result = await runP1McpJourney(input(fixture.baseUrl, 'gate-answer'), {
      ...fixture,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surface, 'mcp');
    assert.equal(result.surfaceStatus, 'blocked');
    assert.match(result.failure.message, /fixture created by this runner invocation/);
    assert.equal(discoveryCalls, 0);
    assert.equal(fixture.mcp.protocol.length, 0);
    assert.equal(fixture.mcp.protocol.some(item =>
      ['agentweaver_answerGate', 'agentweaver_approveGate', 'agentweaver_rejectGate', 'agentweaver_sendMessage']
        .includes(item.message?.params?.name)), false);
  } finally {
    await fixture.close();
  }
});

test('the UI runner rejects caller ownership claims before opening the page', async () => {
  const fixture = await host();
  try {
    const result = await runP1UiJourney(input(fixture.baseUrl, 'gate-answer'), {
      ...fixture,
      mcp: undefined,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surface, 'ui');
    assert.equal(result.surfaceStatus, 'blocked');
    assert.match(result.failure.message, /fixture created by this runner invocation/);
    assert.equal(fixture.requests.some(request => request.method === 'POST'), false);
    assert.equal(fixture.uiReplayCount(), 0);
    assert.deepEqual(fixture.uiState(), { opened: false, closed: true });
  } finally {
    await fixture.close();
  }
});

test('the UI runner opens only the pinned run, checks the journal, and closes only its borrowed adapter', async () => {
  const fixture = await host();
  try {
    const result = await runP1UiJourney(input(fixture.baseUrl), {
      ...fixture,
      mcp: undefined,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surface, 'ui');
    assert.equal(result.surfaceStatus, 'passed');
    assert.equal(result.observations.uiJournalEventCount, 1);
    assert.deepEqual(result.observations.journal, { eventCount: 1, pageCount: 1 });
    assert.equal(fixture.uiReplayCount(), 1);
    assert.equal(fixture.mcp.protocol.some(item =>
      item.message?.params?.name === 'agentweaver_replayRunEvents'), false);
    assert.deepEqual(fixture.uiState(), { opened: true, closed: true });
    assert.equal(result.observations.adapterCleanup.ui, 'closed');
  } finally {
    await fixture.close();
  }
});

test('a shared app actor remains valid for a distinct denied tenant scope when the owner returns 403', async () => {
  const fixture = await host();
  try {
    fixture.deniedApi.authProvider.actorId = 'allowed-actor';
    const config = input(fixture.baseUrl);
    config.pins.fixtureScope.deniedActor.actorId = 'allowed-actor';
    const result = await runP1ApiJourney(config, {
      ...fixture,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surface, 'api');
    assert.equal(result.surfaceStatus, 'passed');
  } finally {
    await fixture.close();
  }
});

test('P1 MCP journal uses the discovered replay tool instead of the Gateway API route', async () => {
  const fixture = await host();
  try {
    const result = await runP1McpJourney(input(fixture.baseUrl), {
      ...fixture,
      sourceEvidence: SOURCE_EVIDENCE,
      clock: CLOCK,
    });
    assert.equal(result.surfaceStatus, 'passed');
    assert.deepEqual(result.observations.journal, { eventCount: 1, pageCount: 1 });
    const journalStep = result.steps.find(step => step.id === 'committed-run-journal');
    assert.equal(journalStep.toolName, 'agentweaver_replayRunEvents');
    assert.ok(journalStep.correlationId);
    assert.ok(fixture.mcp.protocol.some(item =>
      item.message?.params?.name === 'agentweaver_replayRunEvents'));
    assert.equal(fixture.requests.some(request =>
      new URL(request.url, fixture.baseUrl).pathname.endsWith('/runs/controlled-run/events')), false);
  } finally {
    await fixture.close();
  }
});

const invalidJournalPages = [
  [eventPage([], { nextCursor: null, hasMore: false })],
  [
    eventPage(undefined, { nextCursor: 'cursor-1', hasMore: true }),
    eventPage([{
      ...eventPage().events[0],
      eventId: '00000000-0000-4000-8000-000000000002',
      position: 2,
    }], {
      nextCursor: 'cursor-1',
      hasMore: false,
    }),
  ],
];

async function assertInvalidJournalPages(runJourney, expectedSurface, assertMcp) {
  for (const eventPages of invalidJournalPages) {
    const fixture = await host({ eventPages });
    try {
      const result = await runJourney(input(fixture.baseUrl), {
        ...fixture,
        sourceEvidence: SOURCE_EVIDENCE,
        clock: CLOCK,
      });
      assert.equal(result.surface, expectedSurface);
      assert.equal(result.surfaceStatus, 'failed');
      assert.match(result.failure.message, /journal is empty|cursor did not advance/);
      if (assertMcp) {
        assert.ok(fixture.mcp.protocol.some(item =>
          item.message?.params?.name === 'agentweaver_replayRunEvents'));
        assert.equal(fixture.requests.some(request =>
          new URL(request.url, fixture.baseUrl).pathname.endsWith('/runs/controlled-run/events')), false);
      }
    } finally {
      await fixture.close();
    }
  }
}

test('empty or repeated API journal cursors fail instead of producing P1 evidence', async () => {
  await assertInvalidJournalPages(runP1ApiJourney, 'api', false);
});

test('empty or repeated MCP journal cursors fail instead of producing P1 evidence', async () => {
  await assertInvalidJournalPages(runP1McpJourney, 'mcp', true);
});
