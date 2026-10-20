import { randomUUID } from 'node:crypto';
import { createP1AcceptanceResult } from './p1-acceptance-result.mjs';
import { requireJourneyCapabilities } from './journey-capabilities.mjs';
import { validateP1JourneyInput } from './p1-journey-input.mjs';
import { isP1RunnerOwnedFixtureInput } from './p1-fixture-setup.mjs';
import {
  P1BlockedError,
  callP1McpTool,
  newP1IdempotencyKey,
  p1Timestamp,
  readP1OwnerSnapshots,
  replayP1ApiJournal,
  replayP1Journal,
  requireP1DecisionReceipt,
  requireP1DeniedApiActor,
  requireP1Http,
  requireP1OwnedGate,
  sanitizeP1Failure,
  summarizeP1SourceEvidence,
  validateP1AdapterBindings,
  waitForP1GateResolution,
} from './p1-surface-common.mjs';

/** Surface adapters and journey names accepted by the shared P1 execution flow. */
const SURFACES = new Set(['api', 'mcp', 'ui']);
const JOURNEYS = new Set(['gate-answer', 'gate-approve', 'gate-reject', 'message', 'journal']);
const EVENT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Resolve the fixture-owned gate answer against both the current blocker and gate receipt. */
function validateActorAnswer(snapshot, input) {
  const { gate, blocker } = requireP1OwnedGate(snapshot, input);
  let answerBody = {};
  if (input.journey === 'gate-answer') {
    const answer = input.pins.fixtureScope.gate.answer;
    if (typeof answer !== 'string' || !answer.trim()) {
      throw new P1BlockedError('The fixture scope has no exact gate answer.');
    }
    if (blocker.choices.includes(answer) && gate.allowedChoices.includes(answer)) {
      answerBody = { choiceId: answer, freeformAnswer: null };
    } else if (blocker.allowsFreeform && gate.allowsFreeform && !blocker.choices.includes(answer)) {
      answerBody = { choiceId: null, freeformAnswer: answer };
    } else {
      throw new P1BlockedError('The exact fixture-owned gate does not allow its configured answer.');
    }
  } else if (blocker.kind === 'awaitingInput') {
    throw new P1BlockedError('An input gate cannot be approved or rejected.');
  }
  return { gate, blocker, answerBody };
}

/** Submit a gate answer, approval, or rejection through the selected surface adapter. */
async function dispatchGate(surface, adapters, input, snapshot, dependencies) {
  const { gate, answerBody } = validateActorAnswer(snapshot, input);
  const operationId = input.journey === 'gate-answer'
    ? 'answerGate'
    : input.journey === 'gate-approve' ? 'approveGate' : 'rejectGate';
  if (surface === 'ui') {
    await requireUiActionEvidence(dependencies.beforeUiAction, input.journey);
    const call = await adapters.ui.gate({
      sessionId: input.pins.ownerFences.sessionId,
      requestId: gate.requestId,
      action: input.journey === 'gate-answer'
        ? 'answer'
        : input.journey === 'gate-approve' ? 'approve' : 'reject',
      ...(input.journey === 'gate-answer' ? { answer: input.pins.fixtureScope.gate.answer } : {}),
    });
    const receipt = call?.transientResponseBody;
    if (call?.status !== 200) throw new Error('The UI gate action did not return HTTP 200.');
    return { operationId, status: call.status, receipt };
  }

  const body = {
    expectedStateVersion: snapshot.decisions.stateVersion,
    idempotencyKey: newP1IdempotencyKey(dependencies.createIdempotencyKey),
    ...answerBody,
  };
  if (surface === 'api') {
    const call = await adapters.api.invoke(operationId, {
      pathParameters: {
        projectId: input.pins.ownerFences.projectId,
        runId: input.pins.ownerFences.runId,
        sessionId: input.pins.ownerFences.sessionId,
        requestId: gate.requestId,
      },
      body,
    });
    const receipt = requireP1Http(call, [200], operationId);
    return { operationId, status: call.status, receipt };
  }

  const result = await callP1McpTool(adapters.mcp, `agentweaver_${operationId}`, {
    projectId: input.pins.ownerFences.projectId,
    runId: input.pins.ownerFences.runId,
    sessionId: input.pins.ownerFences.sessionId,
    requestId: gate.requestId,
    body,
    tenantSelector: input.environment.tenantId,
  });
  if (result.isError || result.status !== 200) {
    throw new Error(`The MCP ${operationId} tool returned an owner denial or unexpected status.`);
  }
  return { operationId, status: result.status, receipt: result.ownerResponse, correlationId: result.correlationId };
}

/** Read the current owner session tree and require the exact active message participants. */
async function readMessageTree(api, input) {
  const { projectId, runId, rootSessionId } = input.pins.ownerFences;
  const call = await api.invoke('readSessionTree', {
    pathParameters: { projectId, runId, sessionId: rootSessionId },
  });
  const tree = requireP1Http(call, [200], 'readSessionTree');
  if (tree?.rootSessionId !== rootSessionId || !Array.isArray(tree.nodes)
    || tree.nodes.some(node => node.identity?.projectId !== projectId || node.identity?.runId !== runId)) {
    throw new P1BlockedError('The live owner session tree does not match the pinned fixture scope.');
  }
  const message = input.pins.fixtureScope.message;
  for (const sessionId of [message?.senderSessionId, message?.recipientSessionId]) {
    if (typeof sessionId !== 'string' || !sessionId
      || tree.nodes.filter(node => node.identity.sessionId === sessionId && node.lifecycle === 'active').length !== 1) {
      throw new P1BlockedError('Both addressed message sessions must already be active in the runner-owned session tree.');
    }
  }
  if (message.senderSessionId === message.recipientSessionId
    || typeof message.text !== 'string' || !message.text.trim()) {
    throw new P1BlockedError('The fixture scope has no exact addressed-message pair and content.');
  }
  return message;
}

/** Submit an immediate addressed message through the selected API, MCP, or UI surface. */
async function dispatchMessage(surface, adapters, input, dependencies) {
  const message = await readMessageTree(adapters.api, input);
  const operationId = 'sendMessage';
  if (surface === 'ui') {
    await requireUiActionEvidence(dependencies.beforeUiAction, input.journey);
    const call = await adapters.ui.sendMessage({
      rootSessionId: input.pins.ownerFences.rootSessionId,
      senderSessionId: message.senderSessionId,
      recipientSessionId: message.recipientSessionId,
      text: message.text,
    });
    if (!call || ![200, 202].includes(call.status)) {
      throw new Error('The UI message action did not return owner acceptance.');
    }
    return { operationId, status: call.status, receipt: call.transientResponseBody };
  }

  const body = {
    recipientSessionId: message.recipientSessionId,
    idempotencyKey: newP1IdempotencyKey(dependencies.createIdempotencyKey),
    deliveryMode: 'immediate',
    purpose: 'progress',
    kind: 'text',
    payload: { text: message.text.trim() },
  };
  if (surface === 'api') {
    const call = await adapters.api.invoke(operationId, {
      pathParameters: {
        projectId: input.pins.ownerFences.projectId,
        runId: input.pins.ownerFences.runId,
        sessionId: message.senderSessionId,
      },
      body,
    });
    const receipt = requireP1Http(call, [200, 202], operationId);
    return { operationId, status: call.status, receipt };
  }

  const result = await callP1McpTool(adapters.mcp, 'agentweaver_sendMessage', {
    projectId: input.pins.ownerFences.projectId,
    runId: input.pins.ownerFences.runId,
    sessionId: message.senderSessionId,
    body,
    tenantSelector: input.environment.tenantId,
  });
  if (result.isError || ![200, 202].includes(result.status)) {
    throw new Error('The MCP message tool returned an owner denial or unexpected status.');
  }
  return { operationId, status: result.status, receipt: result.ownerResponse, correlationId: result.correlationId };
}

/** Require durable action-start evidence before allowing a UI write that cannot be retried safely. */
async function requireUiActionEvidence(beforeUiAction, journey) {
  if (typeof beforeUiAction !== 'function') {
    throw new P1BlockedError('UI writes require durable action-start evidence to prevent duplicate retries.');
  }
  await beforeUiAction({ journey });
}

/** Validate the actual accepted-message receipt for the selected recipient. */
function requireMessageReceipt(receipt, recipientSessionId) {
  if (typeof receipt?.ownerMessageId !== 'string' || !receipt.ownerMessageId
    || receipt.recipientSessionId !== recipientSessionId
    || !['accepted', 'admitted'].includes(receipt.status)) {
    throw new Error('The message owner did not return its actual accepted-message receipt.');
  }
}

/**
 * Run a bounded P1 journey with current owner checks, selected-surface actions, and durable evidence.
 * Returns a passed, failed, or blocked acceptance result; cleanup and adapter close outcomes are recorded.
 */
export async function runP1SurfaceJourney(surface, input, {
  api,
  deniedApi,
  mcp,
  ui,
  sourceEvidence,
  nativeProbeResults = [],
  clock = () => Date.now(),
  sleep = delay => new Promise(resolve => setTimeout(resolve, delay)),
  createIdempotencyKey = randomUUID,
  beforeUiAction,
  gateTimeoutMs = 5000,
} = {}) {
  if (!SURFACES.has(surface)) throw new Error('A known P1 surface is required.');
  const startedAt = p1Timestamp(clock);
  const steps = [];
  let surfaceStatus = 'blocked';
  let deniedActorUnavailable = false;
  let failure;
  let uiAdapterCloseStatus = surface === 'ui' ? 'not-started' : 'not-required';
  let mcpCloseStatus = surface === 'mcp' && mcp && typeof mcp.close === 'function'
    ? 'not-started' : 'not-required';
  const observations = {};
  const actionReceipts = [];
  let safeSourceEvidence;
  let cleanup = { intent: 'none', status: 'not-required' };
  const adapters = { api, deniedApi, mcp, ui };

  try {
    let validated;
    try {
      validated = validateP1JourneyInput(input, { now: clock });
    } catch (error) {
      throw new P1BlockedError(error.message);
    }
    if (!JOURNEYS.has(input.journey)) throw new P1BlockedError('The selected P1 journey is unsupported.');
    if (input.journey !== 'journal' && !isP1RunnerOwnedFixtureInput(input)) {
      throw new P1BlockedError('Gate and message journeys require a fixture created by this runner invocation; caller-supplied ownership receipts are not authority.');
    }
    const { environment, pins } = validated;
    validateP1AdapterBindings(adapters, environment, pins.fixtureScope, surface);
    safeSourceEvidence = summarizeP1SourceEvidence(sourceEvidence, pins);
    await api.discover();
    if (surface === 'mcp') {
      try {
        await mcp.discoverTools();
      } catch (error) {
        throw new P1BlockedError(`The live MCP tool catalog is unavailable: ${error.message}`);
      }
    }
    let capabilities;
    try {
      capabilities = requireJourneyCapabilities(adapters, input.journey, surface);
    } catch (error) {
      throw new P1BlockedError(error.message);
    }
    const supportingOperations = [
      'readRunStatus', 'readSessionStatus', 'readDecisions',
      ...(input.journey === 'message' ? ['readSessionTree'] : []),
    ];
    const missing = supportingOperations.filter(operation => !api.operations.has(operation));
    if (missing.length) throw new P1BlockedError(`Required P1 API observations are missing: ${missing.join(', ')}.`);
    steps.push({
      id: 'live-capabilities',
      status: 'passed',
      requiredApiOperations: [...new Set([...capabilities.apiOperations, ...supportingOperations])].length,
    });

    if (pins.fixtureScope.deniedActor) {
      const denied = await requireP1DeniedApiActor(deniedApi, input);
      steps.push({ id: 'valid-denied-actor-403', status: 'passed', operationId: denied.operationId, httpStatus: 403 });
    } else {
      deniedActorUnavailable = true;
      steps.push({
        id: 'valid-denied-actor-403',
        status: 'blocked',
        reason: 'Distinct authorized-but-denied actor unavailable: fixturePlan.deniedActor not provided (no second Broker account available)',
      });
    }
    const snapshot = await readP1OwnerSnapshots(api, pins.ownerFences);
    steps.push({ id: 'current-owner-fences', status: 'passed', operationId: 'readRunStatus/readSessionStatus/readDecisions' });
    if (input.journey.startsWith('gate-')) validateActorAnswer(snapshot, input);
    if (input.journey === 'message') await readMessageTree(api, input);
    if (surface === 'ui') await ui.openRun({ projectId: pins.ownerFences.projectId, runId: pins.ownerFences.runId });
    if (input.journey === 'journal') {
      const journal = surface === 'api'
        ? await replayP1ApiJournal(api, pins.ownerFences)
        : surface === 'mcp'
          ? await replayP1Journal(mcp, pins.ownerFences, environment.tenantId)
          : await ui.replayJournal();
      if (surface === 'ui') {
        const events = journal?.events;
        const eventIds = new Set();
        const positions = new Set();
        let lastPosition = Number.POSITIVE_INFINITY;
        if (!Array.isArray(events) || events.length === 0
          || !Number.isSafeInteger(journal.pageCount) || journal.pageCount < 1
          || journal.transcriptContentAvailable !== false) {
          throw new Error('The UI did not return a nonempty committed-event view without transcript content.');
        }
        for (const event of events) {
          const identity = event?.identity;
          if (typeof event?.eventId !== 'string' || !EVENT_ID.test(event.eventId)
            || !identity || Object.keys(identity).sort().join(',') !== 'projectId,runId,sessionId'
            || identity.projectId !== pins.ownerFences.projectId || identity.runId !== pins.ownerFences.runId
            || typeof identity.sessionId !== 'string' || !identity.sessionId
            || !Number.isSafeInteger(event.position) || event.position < 1
            || eventIds.has(event.eventId) || positions.has(event.position) || event.position >= lastPosition) {
            throw new Error('The UI replay contains a foreign, repeated, or unordered event identity.');
          }
          eventIds.add(event.eventId);
          positions.add(event.position);
          lastPosition = event.position;
        }
        observations.uiJournalEventCount = events.length;
        observations.journal = { eventCount: events.length, pageCount: journal.pageCount };
      } else {
        observations.journal = { eventCount: journal.eventCount, pageCount: journal.pageCount };
      }
      steps.push({
        id: 'committed-run-journal',
        status: 'passed',
        ...(surface === 'api' ? { operationId: 'replayRunEvents' } : {}),
        ...(surface === 'mcp' ? { toolName: 'agentweaver_replayRunEvents' } : {}),
        ...(surface === 'ui' ? { uiMethod: 'replayJournal' } : {}),
        ...(journal.correlationId ? { correlationId: journal.correlationId } : {}),
      });
    }
    if (input.journey.startsWith('gate-')) {
      const result = await dispatchGate(surface, adapters, input, snapshot, {
        createIdempotencyKey,
        beforeUiAction,
      });
      actionReceipts.push({
        operationId: result.operationId,
        httpStatus: result.status,
        receipt: result.receipt,
        ...(result.correlationId ? { correlationId: result.correlationId } : {}),
      });
      cleanup = { intent: 'resolve-runner-owned-gate', status: 'verification-pending' };
      requireP1DecisionReceipt(result.receipt, snapshot.decisions.stateVersion, snapshot.run.executionFence);
      await waitForP1GateResolution(api, pins.ownerFences, input.pins.fixtureScope.gate.requestId,
        result.receipt.stateVersion, { clock, sleep, timeoutMs: gateTimeoutMs });
      steps.push({
        id: input.journey,
        status: 'passed',
        operationId: result.operationId,
        httpStatus: result.status,
        ...(result.correlationId ? { correlationId: result.correlationId } : {}),
      });
      cleanup = { intent: 'resolve-runner-owned-gate', status: 'verified-by-owner-snapshot' };
    } else if (input.journey === 'message') {
      const result = await dispatchMessage(surface, adapters, input, {
        createIdempotencyKey,
        beforeUiAction,
      });
      actionReceipts.push({
        operationId: result.operationId,
        httpStatus: result.status,
        receipt: result.receipt,
        ...(result.correlationId ? { correlationId: result.correlationId } : {}),
      });
      cleanup = {
        intent: 'none',
        status: 'acceptance-verification-pending',
        reason: 'The current owner API exposes no delete operation for an accepted addressed message.',
      };
      requireMessageReceipt(result.receipt, input.pins.fixtureScope.message.recipientSessionId);
      steps.push({
        id: 'message',
        status: 'passed',
        operationId: result.operationId,
        httpStatus: result.status,
        acceptedNotDelivered: true,
        ...(result.correlationId ? { correlationId: result.correlationId } : {}),
      });
      cleanup = {
        intent: 'none',
        status: 'not-applicable',
        reason: 'The current owner API exposes no delete operation for an accepted addressed message.',
      };
    } else {
      steps.push({
        id: 'journal',
        status: 'passed',
        ...(surface === 'api' ? { operationId: 'replayRunEvents' } : {}),
        ...(surface === 'mcp' ? { toolName: 'agentweaver_replayRunEvents' } : {}),
        ...(surface === 'ui' ? { uiMethod: 'replayJournal' } : {}),
      });
    }
    surfaceStatus = deniedActorUnavailable ? 'blocked' : 'passed';
  } catch (error) {
    failure = sanitizeP1Failure(error);
    surfaceStatus = error instanceof P1BlockedError ? 'blocked' : 'failed';
  } finally {
    if (surface === 'mcp' && mcp && typeof mcp.close === 'function') {
      try {
        await mcp.close();
        mcpCloseStatus = 'closed';
      } catch (error) {
        mcpCloseStatus = 'failed';
        failure = failure
          ? { message: `${failure.message ?? String(failure)}; MCP adapter cleanup failed: ${sanitizeP1Failure(error).message}` }
          : sanitizeP1Failure(error);
        surfaceStatus = 'failed';
      }
    }
    if (surface === 'ui' && ui && typeof ui.close === 'function') {
      try {
        await ui.close();
        uiAdapterCloseStatus = 'closed';
      } catch (error) {
        uiAdapterCloseStatus = 'failed';
        failure = failure
          ? { message: `${failure.message ?? String(failure)}; UI adapter cleanup failed: ${sanitizeP1Failure(error).message}` }
          : sanitizeP1Failure(error);
        surfaceStatus = 'failed';
      }
    }
  }

  return createP1AcceptanceResult({
    surface,
    surfaceStatus,
    steps,
    sourceEvidence: safeSourceEvidence,
    nativeProbeResults,
    observations: {
      ...observations,
      ...(actionReceipts.length ? { actionReceipts } : {}),
      cleanup,
      adapterCleanup: { mcp: mcpCloseStatus, ui: uiAdapterCloseStatus },
      startedAt,
      completedAt: p1Timestamp(clock),
    },
    failure,
  });
}
