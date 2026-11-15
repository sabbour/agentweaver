import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  buildReadCommands,
  caseExitCode,
  childEnvironment,
  classifyNegativeResponse,
  parseArgs,
  recoveryRequest,
  requestKeys,
  resolveCliPath,
  runCase,
  validateInput,
  validateJournalPages,
} from '../stale-recovery-fence.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const examplePath = path.resolve(directory, '..', 'examples', 'stale-recovery-fence.json');
const example = JSON.parse(await readFile(examplePath, 'utf8'));
const operationId = '00000000-0000-4000-8000-000000000003';
const previousRuntimeId = '00000000-0000-4000-8000-000000000201';
const currentRuntimeId = '00000000-0000-4000-8000-000000000202';
const event = {
  eventId: '00000000-0000-4000-8000-000000000101',
  schemaVersion: 1,
  eventVersion: 1,
  identity: {
    projectId: example.target.projectId,
    runId: example.target.runId,
    sessionId: example.target.sessionId,
  },
  position: 1,
  occurredAt: '2026-11-15T00:00:00Z',
  payload: { kind: 'turn', role: 'assistant', content: { key: 'turn-1', purpose: 'message' } },
  objectReferences: [],
};
const recovery = {
  operationId,
  phase: 'ready',
  currentStep: 'ready',
  sourceCheckpointId: 'checkpoint-9',
  sourceCheckpointRevision: 3,
  previousExecutionFence: 4,
  executionFence: 5,
  previousRuntimeInstanceId: previousRuntimeId,
  runtimeInstanceId: currentRuntimeId,
  previousRegistrationRevision: 1,
  registrationRevision: 2,
  recoveryMaterialMode: 'nativeCache',
  decisionStateVersion: 7,
  dispatchHeld: false,
};
const pendingGate = {
  requestId: 'recovery-gate',
  kind: 'question',
  subjectId: 'scope-question',
  authorizedActorId: example.expectedActorId,
  fence: 5,
  allowedChoices: ['continue', 'revise'],
  allowsFreeform: false,
};

function fixtureState(change = {}) {
  const currentRecovery = { ...recovery, ...change.recovery };
  const fence = change.fence ?? 5;
  const gate = change.gate === undefined
    ? { ...pendingGate, fence, ...(change.gateFields ?? {}) }
    : change.gate;
  const runStatus = {
    projectId: example.target.projectId,
    runId: example.target.runId,
    rootSessionId: example.target.sessionId,
    executionFence: fence,
    stateVersion: 8,
    executionState: 'active',
    checkpointRecovery: currentRecovery,
  };
  const sessionStatus = {
    identity: {
      projectId: example.target.projectId,
      runId: example.target.runId,
      sessionId: example.target.sessionId,
    },
    rootSessionId: example.target.sessionId,
    executionFence: fence,
    stateVersion: 9,
    lifecycle: 'active',
    checkpointRecovery: structuredClone(currentRecovery),
  };
  const decisions = {
    stateVersion: 7,
    executionFence: fence,
    pendingGate: gate,
  };
  const nodes = [{
    identity: sessionStatus.identity,
    rootSessionId: example.target.sessionId,
    executionFence: fence,
    stateVersion: 9,
  }, ...(change.children ?? [])];
  return {
    runStatus,
    sessionStatus,
    decisions,
    tree: { rootSessionId: example.target.sessionId, nodes },
    page: change.page ?? { events: [structuredClone(event)], nextCursor: 'cursor-1', hasMore: false },
  };
}

function response(status, body) {
  return { status, body };
}

function record(stdout, overrides = {}) {
  return {
    exitCode: 0,
    signal: null,
    stdout: typeof stdout === 'string' ? stdout : JSON.stringify(stdout),
    stderr: '',
    timedOut: false,
    error: null,
    ...overrides,
  };
}

async function harness(t, options = {}) {
  const scratch = path.join(directory, `stale-recovery-${randomUUID()}`);
  const cliName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  const cliPath = path.join(scratch, cliName);
  const inputPath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch);
  await writeFile(cliPath, 'controlled native apphost');
  const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
  const input = {
    ...example,
    apphostSha256,
    expectedRecoveryOperationId: operationId,
    caseInvocationId: `invocation-${randomUUID()}`,
  };
  await writeFile(inputPath, JSON.stringify(input));
  t.after(() => rm(scratch, { recursive: true, force: true }));
  const calls = [];
  let stateReads = 0;
  let treeReads = 0;
  let postCount = 0;
  const initialState = fixtureState(options.initialChange);
  const env = {
    AGENTWEAVER_CLI_PATH: cliPath,
    AGENTWEAVER_GATEWAY_URL: 'https://ambient.invalid',
    AGENTWEAVER_PROJECT_ID: 'ambient-project',
    AGENTWEAVER_RUN_ID: 'ambient-run',
  };

  function commandRunner(_cliPath, args, childEnv) {
    calls.push({ args: [...args], env: childEnv });
    if (args[0] === '--version') return record('1.0.0\n');
    if (args[0] === 'auth') {
      return record(JSON.stringify({
        isSignedIn: true,
        tenantId: input.target.tenantId,
        accessToken: 'controlled-secret-token',
      }));
    }
    if (args[0] === 'api' && args[1] === 'operations') {
      return record(JSON.stringify([
        ...['readRunStatus', 'readSessionStatus', 'readDecisions', 'readSessionTree',
          'replayRunEvents', 'recoverRun'],
      ]));
    }
    if (args[0] === 'api' && args[1] === 'describe') return record(JSON.stringify({ operationId: args[2] }));
    if (args[0] === 'rest' && args[2] === 'GET') {
      return record(response(200, {
        actorId: input.expectedActorId,
        tenantId: input.target.tenantId,
        boundProjectId: input.target.projectId,
        boundRunId: input.target.runId,
      }));
    }
    if (args[0] === 'api' && args[1] === 'invoke' && args[2] === 'recoverRun') {
      postCount += 1;
      if (options.apiRecord) return options.apiRecord;
      return record(response(options.apiStatus ?? 409, options.apiBody ?? { error: 'execution_recovery_stale' }),
        { exitCode: options.apiExitCode ?? 1, ...(options.apiOverrides ?? {}) });
    }
    if (args[0] === 'rest' && args[2] === 'POST') {
      postCount += 1;
      if (options.restRecord) return options.restRecord;
      return record(response(options.restStatus ?? 409, options.restBody ?? { error: 'execution_recovery_stale' }),
        { exitCode: options.restExitCode ?? 1, ...(options.restOverrides ?? {}) });
    }
    if (args[0] === 'api' && args[1] === 'invoke') {
      const operation = args[2];
      let state = initialState;
      if (operation === 'readSessionTree') {
        treeReads += 1;
        if (options.changedDuringFirstSnapshot && treeReads >= 2) {
          state = fixtureState(options.changedDuringFirstSnapshot);
        }
      }
      if (operation === 'readRunStatus') {
        stateReads += 1;
        if (options.changedAfterApi && stateReads >= 3) state = fixtureState(options.changedAfterApi);
        if (options.changedAfterRest && stateReads >= 5) state = fixtureState(options.changedAfterRest);
      }
      if (operation === 'readSessionStatus' || operation === 'readDecisions' || operation === 'readSessionTree') {
        if (options.changedAfterApi && stateReads >= 3) state = fixtureState(options.changedAfterApi);
        if (options.changedAfterRest && stateReads >= 5) state = fixtureState(options.changedAfterRest);
      }
      if (operation === 'replayRunEvents') {
        if (options.outboxPage) return record(response(200, options.outboxPage));
        const pageState = options.changedAfterApi && stateReads >= 3
          ? fixtureState(options.changedAfterApi)
          : options.changedAfterRest && stateReads >= 5
            ? fixtureState(options.changedAfterRest)
            : initialState;
        return record(response(200, pageState.page));
      }
      const body = operation === 'readRunStatus' ? state.runStatus
        : operation === 'readSessionStatus' ? state.sessionStatus
          : operation === 'readDecisions' ? state.decisions
            : state.tree;
      return record(response(200, body));
    }
    throw new Error(`Unexpected command: ${args.join(' ')}`);
  }
  return {
    input,
    inputPath,
    outputPath,
    cliPath,
    env,
    calls,
    postCount: () => postCount,
    commandRunner,
    run: (overrides = {}) => runCase(
      { inputPath, outputPath, dryRun: false, ...overrides },
      env,
      commandRunner,
    ),
  };
}

test('input, permission, and fixed request shape are strict', () => {
  const input = validateInput({
    ...example,
    expectedRecoveryOperationId: operationId,
    caseInvocationId: 'case-001',
  });
  const keys = requestKeys(input);
  assert.notEqual(keys.api, keys.rest);
  assert.notEqual(keys.api, input.originalRecoveryIdempotencyKey);
  assert.deepEqual(recoveryRequest(input, keys.api, 4, 8), {
    executionFence: 4,
    expectedRunStateVersion: 8,
    idempotencyKey: keys.api,
    causeCode: 'stale_recovery_fence_case',
    reference: 'case-001',
  });
  assert.throws(() => validateInput({ ...example, approvedExecutionPermission: false }), /permission/);
  assert.throws(() => validateInput({
    ...example,
    expectedRecoveryOperationId: operationId,
    caseInvocationId: 'case-001',
    originalRecoveryIdempotencyKey: 'stale-recovery-fence:case-001:api',
  }), /successful recovery key/);
  assert.throws(() => validateInput({ ...example, body: { executionFence: 1 } }), /schema/);
  assert.throws(() => validateInput({ ...example, route: '/another-route' }), /schema/);
});

test('commands use explicit target binding and only the fixed recovery route', () => {
  const input = validateInput({
    ...example,
    expectedRecoveryOperationId: operationId,
    caseInvocationId: 'case-002',
  });
  const reads = buildReadCommands(input, 'before');
  assert.deepEqual(reads.map(item => item.args[2]), [
    'readRunStatus', 'readSessionStatus', 'readDecisions', 'readSessionTree',
  ]);
  assert.ok(reads.every(item => item.args.includes(`projectId=${input.target.projectId}`)));
  assert.ok(reads.every(item => item.args.includes(`runId=${input.target.runId}`)));
  assert.deepEqual(childEnvironment({
    AGENTWEAVER_GATEWAY_URL: 'https://ambient.invalid',
    AGENTWEAVER_PROJECT_ID: 'ambient-project',
    KEEP_ME: 'safe',
  }), { KEEP_ME: 'safe' });
  assert.throws(() => parseArgs(['--input', 'x', '--output', 'y', '--token', 'secret']), /Use --input/);
  assert.throws(() => resolveCliPath('agentweaver.exe'), /absolute path/);
  assert.throws(() => resolveCliPath(path.resolve(directory, 'node.exe')), /native/);
});

test('only an exact 409 stale-owner envelope classifies a negative request', () => {
  assert.deepEqual(classifyNegativeResponse(
    record(response(409, { error: 'execution_recovery_stale' }), { exitCode: 1 }),
    'api invoke',
  ), { httpStatus: 409, error: 'execution_recovery_stale', exitCode: 1 });
  for (const [status, error] of [
    [401, 'unauthorized'],
    [403, 'forbidden'],
    [404, 'not_found'],
    [400, 'invalid_request'],
    [409, 'execution_recovery_conflict'],
  ]) {
    assert.throws(() => classifyNegativeResponse(record(response(status, { error })), 'request'),
      errorValue => errorValue.status === 'blocked');
  }
  for (const failure of [
    record('', { exitCode: 1 }),
    record('not json', { exitCode: 1 }),
    record(response(409, {}), { exitCode: 1 }),
    record('', { exitCode: null, timedOut: true, error: 'timed out' }),
  ]) {
    assert.throws(() => classifyNegativeResponse(failure, 'request'), errorValue => errorValue.status === 'blocked');
  }
  for (const status of [200, 201, 202, 204, 299]) {
    assert.throws(() => classifyNegativeResponse(record(response(status, {})), 'request'),
      errorValue => errorValue.status === 'failed' && errorValue.cleanupStatus === 'blocked');
  }
});

test('journal paging requires typed events in the selected run scope', () => {
  const secondEvent = {
    ...structuredClone(event),
    eventId: '00000000-0000-4000-8000-000000000102',
    position: 2,
  };
  const pages = [
    { events: [structuredClone(event)], nextCursor: 'cursor-1', hasMore: true },
    { events: [secondEvent], nextCursor: 'cursor-2', hasMore: false },
  ];
  const input = validateInput({
    ...example,
    expectedRecoveryOperationId: operationId,
    caseInvocationId: 'journal-case',
  });
  assert.deepEqual(validateJournalPages([
    { events: [structuredClone(event)], nextCursor: 'cursor-1', hasMore: false },
  ], input), {
    pagesRead: 1,
    eventCount: 1,
    lastPosition: 1,
    events: [event],
  });
  assert.deepEqual(validateJournalPages(pages, input), {
    pagesRead: 2,
    eventCount: 2,
    lastPosition: 2,
    events: [event, secondEvent],
  });
  const repeatedCursor = structuredClone(pages);
  repeatedCursor[1].nextCursor = 'cursor-1';
  assert.throws(() => validateJournalPages(repeatedCursor, input), /pages or cursors/);
  const incompleteContinuation = structuredClone(pages);
  incompleteContinuation[0].nextCursor = null;
  assert.throws(() => validateJournalPages(incompleteContinuation, input), /pages or cursors/);
  assert.throws(() => validateJournalPages([
    { events: [structuredClone(event)], nextCursor: 'cursor-1', hasMore: true },
  ], input), /pages or cursors/);
  const outboxPayload = [{ operationId, eventType: 'orchestrator.run.checkpoint_recovery_pending' }];
  assert.throws(() => validateJournalPages(outboxPayload, input), /pages or cursors/);
  const malformed = structuredClone(pages);
  malformed[0].events[0].payload = { kind: 'turn', role: 'assistant' };
  assert.throws(() => validateJournalPages(malformed, input), /invalid type/);
  const wrongScope = structuredClone(pages);
  wrongScope[0].events[0].identity.runId = 'other-run';
  assert.throws(() => validateJournalPages(wrongScope, input), /scope/);
});

test('two exact stale denials pass with typed journal pages and unchanged bindings', async t => {
  const subject = await harness(t);
  const result = await subject.run();
  assert.equal(result.status, 'passed', result.reason);
  assert.equal(result.cleanupStatus, 'not-required');
  assert.equal(result.publicRequestPredicate,
    'Both public recovery requests using the prior fence were rejected, and the observable bindings stayed unchanged.');
  assert.deepEqual(result.attempts.map(attempt => [attempt.id, attempt.httpStatus, attempt.error]), [
    ['stale-request-api-invoke', 409, 'execution_recovery_stale'],
    ['stale-request-rest', 409, 'execution_recovery_stale'],
  ]);
  assert.equal(result.operationOutboxInventoryProof.status, 'blocked');
  assert.equal(result.nativeNoSendProof, 'not-produced');
  assert.equal(result.physicalEffectProof, 'not-produced');
  const apiCall = subject.calls.find(call => call.args[0] === 'api'
    && call.args[1] === 'invoke' && call.args[2] === 'recoverRun');
  const restCall = subject.calls.find(call => call.args[0] === 'rest' && call.args[2] === 'POST');
  const apiRequest = JSON.parse(apiCall.args[apiCall.args.indexOf('--body') + 1]);
  const restRequest = JSON.parse(restCall.args[restCall.args.indexOf('--body') + 1]);
  assert.deepEqual(Object.keys(apiRequest), [
    'executionFence', 'expectedRunStateVersion', 'idempotencyKey', 'causeCode', 'reference',
  ]);
  assert.equal(apiRequest.executionFence, 4);
  assert.equal(apiRequest.expectedRunStateVersion, 8);
  assert.notEqual(apiRequest.idempotencyKey, restRequest.idempotencyKey);
  assert.equal(restCall.args[restCall.args.indexOf('--url') + 1],
    `/api/v1/projects/${subject.input.target.projectId}/runs/${subject.input.target.runId}/coordination/recovery`);
  assert.deepEqual({ ...restRequest, idempotencyKey: apiRequest.idempotencyKey }, apiRequest);
  assert.ok(subject.calls.some(call => call.args.includes('api') && call.args.includes('describe')));
  const commandLog = await readFile(path.join(subject.outputPath, 'commands.jsonl'), 'utf8');
  assert.doesNotMatch(commandLog, /controlled-secret-token/);
  assert.equal(caseExitCode(result.status), 0);
});

test('missing permission, unsettled recovery, same fence, and held child send no POST', async t => {
  const permissionSubject = await harness(t);
  const deniedInput = { ...permissionSubject.input, approvedExecutionPermission: false };
  await writeFile(permissionSubject.inputPath, JSON.stringify(deniedInput));
  await assert.rejects(permissionSubject.run(), /permission/);
  assert.equal(permissionSubject.postCount(), 0);

  for (const change of [
    { recovery: { phase: 'pending' } },
    { recovery: { operationId: 'not-a-uuid' } },
    { recovery: { previousExecutionFence: 5 } },
    { recovery: { dispatchHeld: true, boundaryKind: 'childJoin' } },
    { children: [{
      identity: {
        projectId: example.target.projectId,
        runId: example.target.runId,
        sessionId: example.target.sessionId,
      },
      rootSessionId: example.target.sessionId,
      executionFence: 5,
      stateVersion: 9,
    }] },
  ]) {
    const subject = await harness(t, { initialChange: change });
    const result = await subject.run();
    assert.equal(result.status, 'blocked');
    assert.equal(subject.postCount(), 0);
  }
});

test('outbox-shaped data is not accepted as a session-journal page', async t => {
  const subject = await harness(t, {
    outboxPage: { operationId, eventType: 'orchestrator.run.checkpoint_recovery_pending' },
  });
  const result = await subject.run();
  assert.equal(result.status, 'blocked');
  assert.equal(subject.postCount(), 0);
});

test('first uncertain delivery stops before REST and retains the original request key', async t => {
  const subject = await harness(t, {
    apiRecord: record('', { exitCode: null, timedOut: true, error: 'timed out' }),
  });
  const result = await subject.run();
  assert.equal(result.status, 'blocked');
  assert.equal(subject.postCount(), 1);
  assert.deepEqual(result.requestKeys, requestKeys(validateInput(subject.input)));
  assert.equal(result.attempts.length, 1);
  assert.equal(result.attempts[0].classification, 'blocked');
  assert.match(result.reason, /uncertain/);
  const apiCall = subject.calls.find(call => call.args[0] === 'api'
    && call.args[1] === 'invoke' && call.args[2] === 'recoverRun');
  const request = JSON.parse(apiCall.args[apiCall.args.indexOf('--body') + 1]);
  assert.equal(request.idempotencyKey, result.requestKeys.api);
  assert.ok(!subject.calls.some(call => call.args[0] === 'rest' && call.args[2] === 'POST'));
});

test('API status and error code mismatches block without issuing REST', async t => {
  for (const [apiStatus, apiBody] of [
    [401, { error: 'unauthorized' }],
    [403, { error: 'forbidden' }],
    [404, { error: 'not_found' }],
    [400, { error: 'invalid_request' }],
    [409, { error: 'execution_recovery_conflict' }],
  ]) {
    const subject = await harness(t, { apiStatus, apiBody });
    const result = await subject.run();
    assert.equal(result.status, 'blocked');
    assert.equal(subject.postCount(), 1);
  }
});

test('an accepted stale request fails and leaves cleanup blocked', async t => {
  for (const apiStatus of [200, 202]) {
    const subject = await harness(t, { apiStatus, apiBody: { executionFence: 6 } });
    const result = await subject.run();
    assert.equal(result.status, 'failed');
    assert.equal(result.cleanupStatus, 'blocked');
    assert.equal(subject.postCount(), 1);
  }
});

test('REST mismatches and uncertain delivery stop without a retry', async t => {
  for (const options of [
    { restStatus: 401, restBody: { error: 'unauthorized' } },
    { restStatus: 409, restBody: { error: 'execution_recovery_conflict' } },
    { restRecord: record('', { exitCode: null, timedOut: true, error: 'timed out' }) },
  ]) {
    const subject = await harness(t, options);
    const result = await subject.run();
    assert.equal(result.status, 'blocked');
    assert.equal(subject.postCount(), 2);
    assert.equal(result.attempts.length, 2);
  }
});

test('authority, runtime, gate, child, and journal changes block before the second request', async t => {
  for (const changedAfterApi of [
    { fence: 6, recovery: { operationId: '00000000-0000-4000-8000-000000000099', executionFence: 6 } },
    { recovery: { runtimeInstanceId: '00000000-0000-4000-8000-000000000203' } },
    { gateFields: { authorizedActorId: 'other-actor' } },
    { children: [{
      identity: { projectId: example.target.projectId, runId: example.target.runId, sessionId: 'child-1' },
      rootSessionId: example.target.sessionId,
      executionFence: 5,
      stateVersion: 1,
    }] },
    { page: { events: [], nextCursor: null, hasMore: false } },
  ]) {
    const subject = await harness(t, { changedAfterApi });
    const result = await subject.run();
    assert.equal(result.status, 'blocked');
    assert.equal(subject.postCount(), 1);
  }
});

test('a tree change during the initial snapshot blocks before the first request', async t => {
  const subject = await harness(t, {
    changedDuringFirstSnapshot: { children: [{
      identity: { projectId: example.target.projectId, runId: example.target.runId, sessionId: 'child-2' },
      rootSessionId: example.target.sessionId,
      executionFence: 5,
      stateVersion: 1,
    }] },
  });
  const result = await subject.run();
  assert.equal(result.status, 'blocked');
  assert.equal(subject.postCount(), 0);
});

test('dry run checks the exact apphost and writes no evidence or commands', async t => {
  const subject = await harness(t);
  const result = await subject.run({ dryRun: true });
  assert.equal(result.status, 'validated-not-run');
  assert.equal(subject.calls.length, 0);
  await assert.rejects(access(subject.outputPath), { code: 'ENOENT' });
});

test('an apphost hash mismatch blocks before any CLI command', async t => {
  const subject = await harness(t);
  await writeFile(subject.inputPath, JSON.stringify({
    ...subject.input,
    apphostSha256: 'f'.repeat(64),
  }));
  await assert.rejects(subject.run(), /apphost SHA-256 hash/);
  assert.equal(subject.calls.length, 0);
});
