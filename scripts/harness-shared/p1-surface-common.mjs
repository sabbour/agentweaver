import { randomUUID } from 'node:crypto';
import { redact } from './redaction.mjs';

/** Error used when the requested surface cannot prove a required P1 precondition. */
export class P1BlockedError extends Error {
  /** Create a recognizable blocked result while preserving the supplied reason. */
  constructor(message) {
    super(message);
    this.name = 'P1BlockedError';
  }
}

/** Convert the injected runner clock to an ISO timestamp or reject an invalid clock value. */
export function p1Timestamp(clock) {
  const value = clock();
  const date = value instanceof Date ? value : new Date(value);
  if (!Number.isFinite(date.getTime())) throw new Error('The P1 runner clock returned an invalid time.');
  return date.toISOString();
}

/** Require an API evidence record to identify one operation and an allowed HTTP status. */
export function requireP1Http(call, expected, operationId) {
  if (!call || call.operationId !== operationId || !expected.includes(call.status)) {
    throw new Error(`The live ${operationId} operation returned an unexpected HTTP result.`);
  }
  return call.transientResponseBody;
}

/** Fail closed unless every client is bound to the selected environment and intended actor. */
export function validateP1AdapterBindings({ api, deniedApi, mcp, ui }, environment, fixtureScope, surface) {
  const origin = new URL(environment.baseUrl).origin;
  const deniedIdentity = fixtureScope.deniedActor;
  if (!api || deniedIdentity && !deniedApi || !deniedIdentity && deniedApi
    || surface === 'mcp' && !mcp || surface === 'ui' && !ui) {
    throw new P1BlockedError(deniedIdentity
      ? 'The API, planned denied-actor API, and selected-surface adapters are required.'
      : 'The API and selected-surface adapters are required; no denied-actor adapter is allowed without a planned actor.');
  }
  if (api.target?.origin !== origin || api.tenantId !== environment.tenantId
    || api.authProvider?.origin !== origin || api.authProvider.issuer !== environment.issuer
    || api.authProvider.audience !== environment.audience || api.authProvider.actorId !== environment.actorId) {
    throw new P1BlockedError('The API client is not bound to the selected Environment origin, tenant, and actor.');
  }
  if (deniedIdentity && (
    deniedApi.target?.origin !== origin || deniedApi.tenantId !== deniedIdentity.tenantId
    || deniedApi.authProvider?.origin !== origin || deniedApi.authProvider.issuer !== environment.issuer
    || deniedApi.authProvider.audience !== environment.audience
    || deniedApi.authProvider.actorId !== deniedIdentity.actorId
  )) {
    throw new P1BlockedError('The denied-actor API client is not bound to its selected Environment identity.');
  }
  if (surface === 'mcp' && (mcp.target?.origin !== origin || mcp.target.pathname !== '/mcp'
    || mcp.tenantId !== environment.tenantId
    || mcp.authProvider?.origin !== origin || mcp.authProvider.issuer !== environment.issuer
    || mcp.authProvider.audience !== environment.audience || mcp.authProvider.actorId !== environment.actorId)) {
    throw new P1BlockedError('The MCP client is not bound to the selected Environment origin, tenant, and actor.');
  }
  if (surface === 'ui' && (ui.target?.origin !== origin || ui.gateway?.origin !== origin
    || ui.tenantId !== environment.tenantId || ui.identity?.issuer !== environment.issuer
    || ui.identity?.audience !== environment.audience || ui.identity?.actorId !== environment.actorId)) {
    throw new P1BlockedError('The borrowed UI adapter is not bound to the selected Environment origin and identity.');
  }
}

/** Read run, session, and decision snapshots concurrently and verify their shared execution fence. */
export async function readP1OwnerSnapshots(api, fences) {
  const [runCall, sessionCall, decisionsCall] = await Promise.all([
    api.invoke('readRunStatus', { pathParameters: { projectId: fences.projectId, runId: fences.runId } }),
    api.invoke('readSessionStatus', {
      pathParameters: { projectId: fences.projectId, runId: fences.runId, sessionId: fences.sessionId },
    }),
    api.invoke('readDecisions', {
      pathParameters: { projectId: fences.projectId, runId: fences.runId, sessionId: fences.sessionId },
    }),
  ]);
  const run = requireP1Http(runCall, [200], 'readRunStatus');
  const session = requireP1Http(sessionCall, [200], 'readSessionStatus');
  const decisions = requireP1Http(decisionsCall, [200], 'readDecisions');
  if (run?.projectId !== fences.projectId || run.runId !== fences.runId
    || run.rootSessionId !== fences.rootSessionId || run.executionFence !== fences.executionFence
    || !Number.isSafeInteger(run.stateVersion) || run.stateVersion < 0
    || session?.identity?.projectId !== fences.projectId
    || session.identity.runId !== fences.runId || session.identity.sessionId !== fences.sessionId
    || session.rootSessionId !== fences.rootSessionId || session.executionFence !== fences.executionFence
    || decisions?.executionFence !== fences.executionFence
    || !Number.isSafeInteger(decisions.stateVersion) || decisions.stateVersion < 0) {
    throw new P1BlockedError('Current owner snapshots do not match the pinned project, run, session, and execution fence.');
  }
  return { run, session, decisions };
}

/** Match the pending owner gate and blocker to the runner-owned request and current actor. */
export function requireP1OwnedGate(snapshot, input) {
  const gate = snapshot.decisions.pendingGate;
  const blocker = snapshot.session.blockers?.find(item => item.requestId === input.pins.fixtureScope.gate?.requestId);
  const expectedRequestId = input.pins.fixtureScope.gate?.requestId;
  const gateKinds = new Map([
    ['awaitingInput', ['question']],
    ['awaitingOutcomeConfirmation', ['outcomeConfirmation']],
    ['awaitingPlanApproval', ['generatedWorkflowConfirmation', 'workPlanConfirmation', 'scopeChangeConfirmation']],
    ['awaitingApproval', ['approval']],
  ]);
  if (typeof expectedRequestId !== 'string' || !expectedRequestId
    || gate?.requestId !== expectedRequestId || gate.authorizedActorId !== input.environment.actorId
    || gate.fence !== snapshot.run.executionFence
    || !Array.isArray(gate.allowedChoices) || typeof gate.allowsFreeform !== 'boolean') {
    throw new P1BlockedError('The current owner snapshot does not expose the exact fixture-owned gate for this actor and fence.');
  }
  if (!blocker || !gateKinds.get(blocker.kind)?.includes(gate.kind)) {
    throw new P1BlockedError('The exact fixture-owned gate is missing or its owner blocker kind does not match.');
  }
  return { gate, blocker };
}

/** Accept only a typed, accepted decision receipt for the next state version and same execution fence. */
export function requireP1DecisionReceipt(receipt, expectedStateVersion, executionFence) {
  if (typeof receipt?.accepted !== 'boolean' || typeof receipt.decisionId !== 'string'
    || !receipt.decisionId || !Number.isSafeInteger(receipt.stateVersion)
    || !Number.isSafeInteger(receipt.executionFence) || !Array.isArray(receipt.issues)
    || receipt.issues.some(issue => typeof issue?.code !== 'string'
      || typeof issue.path !== 'string' || typeof issue.message !== 'string')) {
    throw new Error('The owner action did not return its typed decision receipt.');
  }
  if (!receipt.accepted) throw new Error('The owner explicitly denied the P1 gate action.');
  if (receipt.executionFence !== executionFence
    || receipt.stateVersion !== expectedStateVersion + 1) {
    throw new Error('The owner decision receipt does not match the current fence and next state version.');
  }
  return receipt;
}

/** Generate one nonempty idempotency key for a mutating P1 request. */
export function newP1IdempotencyKey(createIdempotencyKey = randomUUID) {
  const key = createIdempotencyKey();
  if (typeof key !== 'string' || !key.trim()) {
    throw new Error('The P1 runner did not produce a nonempty idempotency key.');
  }
  return key;
}

/**
 * Poll decision snapshots until the exact gate resolves or the bounded timeout expires.
 * A changed execution fence or regressed state version stops the wait instead of accepting stale data.
 */
export async function waitForP1GateResolution(api, fences, requestId, expectedVersion, {
  clock = () => Date.now(),
  sleep = delay => new Promise(resolve => setTimeout(resolve, delay)),
  timeoutMs = 5000,
}) {
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 30000) {
    throw new Error('The gate wait must stay within the 30-second P1 bound.');
  }
  const deadline = clock() + timeoutMs;
  while (clock() <= deadline) {
    const call = await api.invoke('readDecisions', {
      pathParameters: { projectId: fences.projectId, runId: fences.runId, sessionId: fences.sessionId },
    });
    const decisions = requireP1Http(call, [200], 'readDecisions');
    if (decisions.executionFence !== fences.executionFence
      || !Number.isSafeInteger(decisions.stateVersion) || decisions.stateVersion < expectedVersion) {
      throw new P1BlockedError('The gate resolution changed or lost its pinned owner fence.');
    }
    if (decisions.pendingGate?.requestId !== requestId) return decisions;
    await sleep(Math.min(100, Math.max(0, deadline - clock())));
  }
  throw new Error('The owner did not confirm the fixture-owned gate resolution before the bounded wait expired.');
}

/** Pair a captured MCP tools/call request with its JSON-RPC response and return the request ID. */
function requireMcpCorrelation(mcp, toolName, protocolStart) {
  const records = mcp.protocol.slice(protocolStart);
  const request = records.find(record => record.direction === 'request'
    && record.message?.method === 'tools/call' && record.message.params?.name === toolName);
  if (request?.message?.id === undefined || request?.message?.id === null) {
    throw new Error('The MCP replay call has no actual JSON-RPC request ID.');
  }
  const response = records.find(record => record.direction === 'response'
    && record.message?.id === request.message.id);
  if (!response) throw new Error('The MCP replay result has no matching JSON-RPC response ID.');
  return request.message.id;
}

/**
 * Validate arguments against the discovered MCP schema, call the tool, and verify captured protocol evidence.
 * Returns the owner's structured result with its matching JSON-RPC correlation ID.
 */
export async function callP1McpTool(mcp, toolName, arguments_) {
  const tool = mcp.tools.get(toolName);
  if (!tool) throw new P1BlockedError(`The live MCP menu is missing ${toolName}.`);
  const schema = tool.inputSchema;
  const properties = schema?.properties;
  if (!properties || typeof properties !== 'object' || Array.isArray(properties)) {
    throw new P1BlockedError(`The live MCP menu has no argument schema for ${toolName}.`);
  }
  const unexpected = Object.keys(arguments_).filter(name => !Object.hasOwn(properties, name));
  const missing = (schema.required ?? []).filter(name => !Object.hasOwn(arguments_, name));
  if (unexpected.length || missing.length || schema.additionalProperties !== false) {
    throw new P1BlockedError(`The ${toolName} arguments do not match its discovered live schema.`);
  }
  const protocolStart = mcp.protocol.length;
  const call = await mcp.callTool(toolName, arguments_);
  const result = call.transientResult;
  const structured = result?.structuredContent;
  const protocolRecords = mcp.protocol.slice(protocolStart);
  const requests = protocolRecords.filter(record => record.direction === 'request'
    && record.message?.method === 'tools/call' && record.message.params?.name === toolName);
  if (requests.length !== 1) throw new Error(`The ${toolName} result is not paired with one actual JSON-RPC request.`);
  const correlationId = requireMcpCorrelation(mcp, toolName, protocolStart);
  const responses = protocolRecords.filter(record => record.direction === 'response'
    && record.message?.id === correlationId);
  if (responses.length !== 1 || !call.result
    || JSON.stringify(responses[0].message.result) !== JSON.stringify(call.result)) {
    throw new Error(`The ${toolName} structured result does not match its correlated JSON-RPC response.`);
  }
  if (!structured || typeof structured.status !== 'number'
    || !Object.hasOwn(structured, 'ownerResponse')) {
    throw new Error(`The ${toolName} result has no typed owner response.`);
  }
  return {
    call,
    isError: result.isError,
    status: structured.status,
    ownerResponse: structured.ownerResponse,
    correlationId,
  };
}

const P1_JOURNAL_PAGE_LIMIT = 100;
const P1_JOURNAL_PAGE_LIMIT_MAX = 20;
const P1_EVENT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Read successive owner pages and enforce bounded, ordered, unique event IDs and positions. */
async function replayP1JournalPages(readPage, fences, surface) {
  const eventIds = new Set();
  const positions = new Set();
  const cursors = new Set();
  let cursor = null;
  let lastPosition = 0;
  let correlationId;
  for (let pageNumber = 0; pageNumber < P1_JOURNAL_PAGE_LIMIT_MAX; pageNumber++) {
    const pageCall = await readPage(cursor);
    if (pageCall.isError || pageCall.status !== 200) {
      throw new Error(`The ${surface} replay did not return an accepted owner event page.`);
    }
    const page = pageCall.page;
    if (!Array.isArray(page?.events) || typeof page.hasMore !== 'boolean'
      || page.nextCursor !== null && (typeof page.nextCursor !== 'string'
        || !page.nextCursor || page.nextCursor.length > 8192 || /[\r\n\0]/.test(page.nextCursor))) {
      throw new Error('The actual owner replay page does not match the event-page contract.');
    }
    if (page.events.length === 0) {
      throw new Error(pageNumber === 0
        ? 'The actual owner event journal is empty.'
        : 'The actual owner replay cursor advanced to an empty page.');
    }
    if (page.events.length > P1_JOURNAL_PAGE_LIMIT) {
      throw new Error('The actual owner replay page exceeds the requested event limit.');
    }
    for (const event of page.events) {
      const identity = event?.identity;
      if (typeof event?.eventId !== 'string' || !P1_EVENT_ID.test(event.eventId)
        || !identity || Object.keys(identity).sort().join(',') !== 'projectId,runId,sessionId'
        || identity.projectId !== fences.projectId || identity.runId !== fences.runId
        || typeof identity.sessionId !== 'string' || !identity.sessionId
        || !Number.isSafeInteger(event.position) || event.position < 1
        || eventIds.has(event.eventId) || positions.has(event.position) || event.position <= lastPosition) {
        throw new Error('The actual owner replay contains a foreign, repeated, or reordered event identity.');
      }
      eventIds.add(event.eventId);
      positions.add(event.position);
      lastPosition = event.position;
    }
    if (!page.nextCursor || page.nextCursor === cursor || cursors.has(page.nextCursor)) {
      throw new Error(`The actual ${surface} replay cursor did not advance.`);
    }
    cursors.add(page.nextCursor);
    correlationId = pageCall.correlationId ?? correlationId;
    if (!page.hasMore) return { eventCount: eventIds.size, pageCount: pageNumber + 1, correlationId };
    cursor = page.nextCursor;
  }
  throw new Error('The actual owner replay exceeded the 20-page bound.');
}

/** Replay and verify the bounded event cursor chain through the live API endpoint. */
export async function replayP1ApiJournal(api, fences) {
  return replayP1JournalPages(async cursor => {
    const call = await api.invoke('replayRunEvents', {
      pathParameters: { projectId: fences.projectId, runId: fences.runId },
      query: { limit: P1_JOURNAL_PAGE_LIMIT, ...(cursor === null ? {} : { cursor }) },
    });
    return {
      status: call.status,
      page: requireP1Http(call, [200], 'replayRunEvents'),
      correlationId: call.traceId,
    };
  }, fences, 'API');
}

/** Replay and verify the bounded event cursor chain through the live MCP tool. */
export async function replayP1Journal(mcp, fences, tenantId) {
  return replayP1JournalPages(async cursor => {
    const call = await callP1McpTool(mcp, 'agentweaver_replayRunEvents', {
      projectId: fences.projectId,
      runId: fences.runId,
      limit: P1_JOURNAL_PAGE_LIMIT,
      tenantSelector: tenantId,
      ...(cursor === null ? {} : { cursor }),
    });
    return {
      isError: call.isError,
      status: call.status,
      page: call.ownerResponse,
      correlationId: call.correlationId,
    };
  }, fences, 'MCP');
}

/** Prove the selected distinct actor is authorized but denied access to the pinned run. */
export async function requireP1DeniedApiActor(deniedApi, input) {
  const identity = input.pins.fixtureScope.deniedActor;
  if (!identity || typeof identity.actorId !== 'string' || !identity.actorId
    || typeof identity.tenantId !== 'string' || !identity.tenantId
    || identity.actorId === input.environment.actorId && identity.tenantId === input.environment.tenantId
    || deniedApi.tenantId !== identity.tenantId) {
    throw new P1BlockedError('A distinct authorized-but-denied actor fixture is required.');
  }
  await deniedApi.discover();
  const call = await deniedApi.invoke('readRunStatus', {
    pathParameters: { projectId: input.pins.ownerFences.projectId, runId: input.pins.ownerFences.runId },
  });
  if (call.status !== 403) {
    throw new Error(call.status === 401
      ? 'The denied-actor token was rejected as unauthenticated; HTTP 403 was required.'
      : `The denied-actor read returned HTTP ${call.status}; HTTP 403 was required.`);
  }
  return { operationId: 'readRunStatus', status: call.status };
}

/** Redact a thrown value before including it in durable P1 result evidence. */
export function sanitizeP1Failure(error) {
  const safe = redact(error);
  return typeof safe === 'string' ? { message: safe } : safe;
}
