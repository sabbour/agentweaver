import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  apiResponseBody,
  buildCommands,
  caseExitCode,
  compareStatusApiAndRest,
  parseArgs,
  resolveCliPath,
  validateAfterObservation,
  validateInput,
  validateJournalPages,
  verifyBeforeCapture,
  runCase,
} from '../checkpoint-recovery-observation.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(directory, '..', 'checkpoint-recovery-observation.mjs');
const examplePath = path.resolve(directory, '..', 'examples', 'checkpoint-recovery-observation.json');
const example = JSON.parse(await readFile(examplePath, 'utf8'));
const input = validateInput(example);
const operationId = input.expectedRecoveryOperationId;
const rootFenceBefore = 9;
const requestId = input.expectedGateRequestId;
const event = {
  eventId: '00000000-0000-4000-8000-000000000101',
  schemaVersion: 1,
  eventVersion: 1,
  identity: {
    projectId: input.target.projectId,
    runId: input.target.runId,
    sessionId: input.target.sessionId,
  },
  position: 1,
  occurredAt: '2026-11-15T00:00:00Z',
  payload: { kind: 'turn', role: 'assistant', content: { key: 'turn-1', purpose: 'message' } },
  objectReferences: [],
};
const gate = {
  requestId,
  kind: 'question',
  subjectId: 'scope-question',
  authorizedActorId: input.expectedActorId,
  fence: rootFenceBefore,
  allowedChoices: ['continue', 'revise'],
  allowsFreeform: false,
};

function snapshot({ fence = rootFenceBefore, recovery = null, choices = gate.allowedChoices,
  allowsFreeform = gate.allowsFreeform, gateRequestId = requestId, actorId = input.expectedActorId,
  stateVersion = 7 } = {}) {
  const activeGate = { ...gate, fence, requestId: gateRequestId, authorizedActorId: actorId,
    allowedChoices: choices, allowsFreeform };
  const blocker = {
    kind: 'awaitingInput',
    requestId: gateRequestId,
    choices,
    allowsFreeform,
    prompt: 'Choose an option.',
  };
  const checkpointRecovery = recovery;
  const runStatus = {
    projectId: input.target.projectId,
    runId: input.target.runId,
    rootSessionId: input.target.sessionId,
    executionFence: fence,
    stateVersion,
    executionState: 'active',
    ...(checkpointRecovery ? { checkpointRecovery } : {}),
  };
  const sessionStatus = {
    identity: {
      projectId: input.target.projectId,
      runId: input.target.runId,
      sessionId: input.target.sessionId,
    },
    rootSessionId: input.target.sessionId,
    executionFence: fence,
    stateVersion,
    lifecycle: 'active',
    blockers: [blocker],
    ...(checkpointRecovery ? { checkpointRecovery } : {}),
  };
  const decisions = {
    stateVersion,
    executionFence: fence,
    pendingGate: activeGate,
  };
  const tree = {
    rootSessionId: input.target.sessionId,
    nodes: [{
      identity: sessionStatus.identity,
      rootSessionId: input.target.sessionId,
      executionFence: fence,
    }],
  };
  const events = [{
    events: [structuredClone(event)],
    nextCursor: 'opaque-cursor',
    hasMore: false,
  }];
  return { context: {}, runStatus, sessionStatus, decisions, tree, events };
}

function beforeCaptureRecords(capture) {
  const commands = buildCommands(input);
  const { outputs } = capture;
  const api = value => JSON.stringify({ status: 200, body: value });
  const rest = value => JSON.stringify({ status: 200, body: value });
  const stdoutById = {
    'auth-status': JSON.stringify({ isSignedIn: true, tenantId: input.target.tenantId }),
    'api-operations': JSON.stringify([
      'readRunStatus', 'readSessionStatus', 'readDecisions', 'readSessionTree', 'replayRunEvents',
    ]),
    'rest-authorization-context': rest(outputs.context),
    'run-status': api(outputs.runStatus),
    'run-status-rest': rest(outputs.runStatus),
    'session-status': api(outputs.sessionStatus),
    'session-status-rest': rest(outputs.sessionStatus),
    decisions: api(outputs.decisions),
    'session-tree': api(outputs.tree),
  };
  for (const command of commands) {
    if (command.id.startsWith('api-describe-')) stdoutById[command.id] = '{}';
  }
  const record = (id, args, stdout = stdoutById[id] ?? '') => ({
    id,
    args: args.map((value, index) => (
      args[index - 1] === '--query' && value.startsWith('cursor=') ? 'cursor=[REDACTED]' : value
    )),
    startedAt: '2026-11-15T00:00:00.000Z',
    completedAt: '2026-11-15T00:00:00.000Z',
    durationMs: 0,
    exitCode: 0,
    signal: null,
    stdout,
    stderr: '',
    timedOut: false,
    error: null,
  });
  const records = commands.map((command, index) => {
    const commandId = index < 8
      ? command.id
      : `initial-${command.id === 'rest-authorization-context'
        ? 'authorization-context'
        : command.id}`;
    return record(commandId, command.args, stdoutById[command.id] ?? '');
  });
  const replayArgs = [
    'api', 'invoke', 'replayRunEvents', '--path', `projectId=${input.target.projectId}`,
    '--path', `runId=${input.target.runId}`, '--scope', 'api.read', '--scope',
    'projects.orchestrator', '--gateway-url', input.target.gatewayUrl,
    '--broker-issuer', input.target.brokerIssuer, '--client-id', input.target.clientId,
    '--api-audience', input.target.apiAudience, '--tenant-id', input.target.tenantId,
    '--project-id', input.target.projectId, '--run-id', input.target.runId,
    '--query', 'limit=100',
  ];
  records.push(record(
    'initial-journal-page-1',
    replayArgs,
    api(outputs.events[0]),
  ));
  records.push(
    record('initial-final-run-status', commands.find(command => command.id === 'run-status').args,
      api(outputs.runStatus)),
    record('initial-final-run-status-rest', commands.find(command => command.id === 'run-status-rest').args,
      rest(outputs.runStatus)),
    record('initial-final-session-status', commands.find(command => command.id === 'session-status').args,
      api(outputs.sessionStatus)),
    record('initial-final-session-status-rest', commands.find(command => command.id === 'session-status-rest').args,
      rest(outputs.sessionStatus)),
    record('initial-final-decisions', commands.find(command => command.id === 'decisions').args,
      api(outputs.decisions)),
  );
  return records;
}

function beforeCapture() {
  const before = snapshot();
  const capture = {
    formatVersion: 1,
    status: 'captured',
    caseId: input.caseId,
    caseKind: input.caseKind,
    mode: 'before-capture',
    sourceCommit: input.sourceCommit,
    targetCommit: input.targetCommit,
    apphostSha256: input.apphostSha256,
    scriptSha256: 'd'.repeat(64),
    actorId: input.expectedActorId,
    target: input.target,
    input: { sha256: 'e'.repeat(64), gateRequestId: requestId },
    gate: {
      requestId,
      actorId: input.expectedActorId,
      choices: gate.allowedChoices,
      allowsFreeform: false,
      fence: rootFenceBefore,
    },
    outputs: {
      context: {
        actorId: input.expectedActorId,
        tenantId: input.target.tenantId,
        boundProjectId: input.target.projectId,
        boundRunId: input.target.runId,
      },
      runStatus: before.runStatus,
      sessionStatus: before.sessionStatus,
      decisions: before.decisions,
      tree: before.tree,
      events: before.events,
    },
    journal: { pagesRead: 1, eventCount: 1, lastPosition: 1 },
  };
  const commandRecords = beforeCaptureRecords(capture);
  capture.commandsSha256 = createHash('sha256')
    .update(`${commandRecords.map(record => JSON.stringify(record)).join('\n')}\n`)
    .digest('hex');
  return { capture, commandRecords };
}

function afterSnapshot(overrides = {}) {
  const recovery = {
    operationId,
    phase: 'ready',
    currentStep: 'ready',
    sourceCheckpointId: 'checkpoint-9',
    sourceCheckpointRevision: 3,
    previousExecutionFence: rootFenceBefore,
    executionFence: rootFenceBefore + 1,
    previousRuntimeInstanceId: '00000000-0000-4000-8000-000000000201',
    runtimeInstanceId: '00000000-0000-4000-8000-000000000202',
    recoveryMaterialMode: 'nativeCache',
    gateRequestId: requestId,
    gateAuthorizedActorId: input.expectedActorId,
    decisionStateVersion: 8,
    ...overrides,
  };
  return snapshot({
    fence: rootFenceBefore + 1,
    recovery,
    stateVersion: 8,
    ...(overrides.gateRequestId ? { gateRequestId: overrides.gateRequestId } : {}),
    ...(overrides.actorId ? { actorId: overrides.actorId } : {}),
    ...(overrides.allowedChoices ? { choices: overrides.allowedChoices } : {}),
    ...(overrides.allowsFreeform !== undefined ? { allowsFreeform: overrides.allowsFreeform } : {}),
  });
}

test('case input accepts only finite target, actor, recovery and pending-gate fields', () => {
  assert.equal(input.mode, 'before-capture');
  assert.equal(input.caseKind, 'pending-gate');
  assert.throws(() => validateInput({ ...example, bearerToken: 'do-not-log-this' }), /schema/);
  assert.throws(() => validateInput({ ...example, command: 'agentweaver rest' }), /schema/);
  assert.throws(() => validateInput({ ...example, route: '/private' }), /schema/);
  assert.throws(() => validateInput({ ...example, providerUrl: 'https://provider.example' }), /schema/);
  assert.throws(() => validateInput({
    ...example,
    target: { ...example.target, sessionId: 'bad/session/id' },
  }), /schema/);
  assert.throws(() => validateInput({
    ...example,
    mode: 'after-observation',
    beforeCaptureSha256: 'not-a-hash',
  }), /schema/);
});

test('finite commands discover and describe reads before invoking them', () => {
  const commands = buildCommands(input);
  const ids = commands.map(command => command.id);
  assert.deepEqual(ids.slice(0, 8), [
    'version',
    'auth-status',
    'api-operations',
    'api-describe-readRunStatus',
    'api-describe-readSessionStatus',
    'api-describe-readDecisions',
    'api-describe-readSessionTree',
    'api-describe-replayRunEvents',
  ]);
  assert.ok(commands.some(command => command.args.includes('replayRunEvents')));
  assert.ok(commands.every(command => !command.args.some(argument => /recoverRun|answerGate|cancelProjectRun/.test(argument))));
  for (const command of commands.slice(2, 8)) {
    assert.ok(!command.args.includes('--project-id'));
    assert.ok(!command.args.includes('--run-id'));
  }
  const sessionRead = commands.find(command => command.id === 'session-status');
  assert.ok(sessionRead.args.includes(`sessionId=${input.target.sessionId}`));
  assert.ok(commands.find(command => command.id === 'run-status-rest')
    .args.includes(`/api/v1/projects/${input.target.projectId}/runs/${input.target.runId}/coordination/status`));
});

test('API invocation response bodies require HTTP 200 and an object body', () => {
  const response = { status: 200, headers: {}, evidence: {}, body: { executionFence: 10 } };
  assert.equal(apiResponseBody(response, 'status'), response.body);
  assert.throws(() => apiResponseBody({ ...response, status: 403 }, 'status'), /HTTP 200/);
  assert.throws(() => apiResponseBody({ ...response, body: [] }, 'status'), /JSON body/);
  assert.throws(() => compareStatusApiAndRest(response.body, { executionFence: 11 }, 'run status'),
    error => error.status === 'failed');
});

test('journal paging requires typed session-journal envelopes and rejects outbox payloads', () => {
  const pages = [
    {
      events: [structuredClone(event)],
      nextCursor: 'cursor-1',
      hasMore: true,
    },
    {
      events: [{ ...structuredClone(event), eventId: '00000000-0000-4000-8000-000000000102', position: 2 }],
      nextCursor: 'cursor-2',
      hasMore: false,
    },
  ];
  assert.deepEqual(validateJournalPages(pages, input), {
    pagesRead: 2,
    eventCount: 2,
    lastPosition: 2,
  });
  assert.throws(() => validateJournalPages([{ operationId, status: 'ready' }], input), /page shape/);
  const wrongScope = structuredClone(pages);
  wrongScope[0].events[0].identity.runId = 'other-run';
  assert.throws(() => validateJournalPages(wrongScope, input), /scope/);
  const duplicate = structuredClone(pages);
  duplicate[1].events[0].eventId = event.eventId;
  assert.throws(() => validateJournalPages(duplicate, input), /duplicate/);
  const missingPage = structuredClone(pages);
  missingPage[0].hasMore = false;
  assert.throws(() => validateJournalPages(missingPage, input), /incomplete/);
  const missingPosition = structuredClone(pages);
  missingPosition[1].events[0].position = 3;
  assert.throws(() => validateJournalPages(missingPosition, input), /order/);
  const malformedPayload = structuredClone(pages);
  malformedPayload[0].events[0].payload = { kind: 'turn' };
  assert.throws(() => validateJournalPages(malformedPayload, input), /envelope/);
  const nullableLength = JSON.parse(JSON.stringify(pages));
  nullableLength[0].events[0].payload.content.byteLength = null;
  assert.deepEqual(validateJournalPages(nullableLength, input), {
    pagesRead: 2,
    eventCount: 2,
    lastPosition: 2,
  });
});

test('before-capture evidence must match the captured decision and status surfaces', () => {
  const inputAfter = validateInput({
    ...example,
    mode: 'after-observation',
    beforeCaptureSha256: 'f'.repeat(64),
  });
  const { capture, commandRecords } = beforeCapture();
  assert.equal(verifyBeforeCapture(
    capture,
    inputAfter,
    inputAfter.beforeCaptureSha256,
    capture.scriptSha256,
    capture.apphostSha256,
    capture.input.sha256,
    commandRecords,
    capture.commandsSha256,
  ), capture);
  assert.throws(() => verifyBeforeCapture(
    { ...capture, status: 'passed' },
    inputAfter,
    inputAfter.beforeCaptureSha256,
    capture.scriptSha256,
    capture.apphostSha256,
    capture.input.sha256,
    commandRecords,
    capture.commandsSha256,
  ), /missing, incomplete/);
  const changedChoices = structuredClone(capture);
  changedChoices.outputs.decisions.pendingGate.allowedChoices = ['approve'];
  assert.throws(() => verifyBeforeCapture(
    changedChoices,
    inputAfter,
    inputAfter.beforeCaptureSha256,
    capture.scriptSha256,
    capture.apphostSha256,
    capture.input.sha256,
    commandRecords,
    capture.commandsSha256,
  ), /gate|matching/);
  assert.throws(() => verifyBeforeCapture(
    { ...capture, journal: { ...capture.journal, eventCount: 2 } },
    inputAfter,
    inputAfter.beforeCaptureSha256,
    capture.scriptSha256,
    capture.apphostSha256,
    capture.input.sha256,
    commandRecords,
    capture.commandsSha256,
  ), /journal summary/);
});

test('standalone fabricated before summaries are blocked without retained input and commands', () => {
  const inputAfter = validateInput({
    ...example,
    mode: 'after-observation',
    beforeCaptureSha256: 'f'.repeat(64),
  });
  const { capture, commandRecords } = beforeCapture();
  assert.throws(() => verifyBeforeCapture(
    capture,
    inputAfter,
    inputAfter.beforeCaptureSha256,
    capture.scriptSha256,
    capture.apphostSha256,
    capture.input.sha256,
    null,
    capture.commandsSha256,
  ), error => error.status === 'blocked');
  assert.throws(() => verifyBeforeCapture(
    capture,
    inputAfter,
    inputAfter.beforeCaptureSha256,
    capture.scriptSha256,
    capture.apphostSha256,
    'f'.repeat(64),
    commandRecords,
    capture.commandsSha256,
  ), error => error.status === 'blocked');
});

test('ready recovery passes only with the same pending gate and next-fence material', () => {
  const inputAfter = validateInput({
    ...example,
    mode: 'after-observation',
    beforeCaptureSha256: 'f'.repeat(64),
  });
  const { capture: before } = beforeCapture();
  const after = afterSnapshot();
  const final = structuredClone(after);
  final.runStatus.observedAt = 'later';
  final.sessionStatus.observedAt = 'later';
  const result = validateAfterObservation(inputAfter, before, after, final);
  assert.equal(result.recovery.operationId, operationId);
  assert.deepEqual(result.gate.choices, gate.allowedChoices);
  assert.equal(result.gate.allowsFreeform, false);

  assert.throws(() => validateAfterObservation(inputAfter, null, after, final), error => error.status === 'blocked');
  const wrongOperation = afterSnapshot({ operationId: '00000000-0000-4000-8000-000000000099' });
  assert.throws(() => validateAfterObservation(
    inputAfter, before, wrongOperation, structuredClone(wrongOperation),
  ), error => error.status === 'blocked');
  for (const change of [
    { gateRequestId: 'different-gate' },
    { actorId: 'different-actor' },
    { allowedChoices: ['approve'] },
    { allowsFreeform: true },
  ]) {
    const changed = afterSnapshot(change);
    assert.throws(() => validateAfterObservation(
      inputAfter, before, changed, structuredClone(changed),
    ), error => error.status === 'failed');
  }
  const changedRuntime = afterSnapshot({
    runtimeInstanceId: '00000000-0000-4000-8000-000000000201',
  });
  assert.throws(() => validateAfterObservation(
    inputAfter, before, changedRuntime, structuredClone(changedRuntime),
  ), error => error.status === 'failed');
  const changedFence = afterSnapshot({ executionFence: rootFenceBefore + 2 });
  assert.throws(() => validateAfterObservation(
    inputAfter, before, changedFence, structuredClone(changedFence),
  ), error => error.status === 'failed');
  const missingMaterial = afterSnapshot({ recoveryMaterialMode: null });
  assert.throws(() => validateAfterObservation(
    inputAfter, before, missingMaterial, structuredClone(missingMaterial),
  ), error => error.status === 'blocked');
  assert.throws(() => validateAfterObservation(
    inputAfter, before, after, { ...final, decisions: { ...final.decisions, stateVersion: 9 } },
  ), error => error.status === 'blocked');
  const gateAnswered = structuredClone(after);
  gateAnswered.runStatus.stateVersion += 1;
  gateAnswered.sessionStatus.stateVersion += 1;
  gateAnswered.decisions.stateVersion += 1;
  gateAnswered.decisions.pendingGate = null;
  gateAnswered.sessionStatus.blockers = [];
  assert.throws(() => validateAfterObservation(
    inputAfter, before, after, gateAnswered,
  ), error => error.status === 'blocked' && caseExitCode(error.status) === 2);
});

test('dry run validates the apphost hash without starting the apphost or writing evidence', async () => {
  const scratch = path.join(directory, `dry-run-${randomUUID()}`);
  const cliName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  const cliPath = path.join(scratch, cliName);
  const inputPath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch);
  try {
    await writeFile(cliPath, 'not an executable');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    await writeFile(inputPath, JSON.stringify({ ...example, apphostSha256 }));
    const run = spawnSync(process.execPath, [
      scriptPath, '--input', inputPath, '--output', outputPath, '--dry-run',
    ], {
      cwd: path.resolve(directory, '..', '..', '..', '..'),
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(run.status, 0, run.stderr);
    assert.equal(JSON.parse(run.stdout).status, 'validated-not-run');
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
    await mkdir(outputPath);
    const existing = spawnSync(process.execPath, [
      scriptPath, '--input', inputPath, '--output', outputPath, '--dry-run',
    ], {
      cwd: path.resolve(directory, '..', '..', '..', '..'),
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(existing.status, 2);
    assert.match(existing.stderr, /already exists/);
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
});

test('non-dry command flow reaches discovery and retains verifiable before evidence', async () => {
  const scratch = path.join(directory, `discovery-${randomUUID()}`);
  const cliName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  const cliPath = path.join(scratch, cliName);
  const inputPath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch);
  try {
    await writeFile(cliPath, 'fake apphost');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    await writeFile(inputPath, JSON.stringify({ ...example, apphostSha256 }));
    const calls = [];
    const captureFixture = beforeCapture().capture;
    const commandRecords = beforeCaptureRecords(captureFixture);
    let commandIndex = 0;
    const commandRunner = (_cliPath, args) => {
      calls.push(args);
      const { id, args: _args, ...record } = commandRecords[commandIndex++];
      return {
        ...record,
      };
    };
    const result = await runCase(
      { inputPath, outputPath, dryRun: false },
      { AGENTWEAVER_CLI_PATH: cliPath },
      commandRunner,
    );
    assert.equal(result.status, 'captured');
    assert.ok(calls.some(args => args[0] === 'api' && args[1] === 'operations'));
    assert.equal(calls.filter(args => args[0] === 'api' && args[1] === 'describe').length, 5);
    assert.equal(commandIndex, commandRecords.length);
    const afterInputPath = path.join(scratch, 'after-case.json');
    const afterOutputPath = path.join(scratch, 'after-evidence');
    const savedCapture = JSON.parse(await readFile(result.capturePath, 'utf8'));
    const retainedInputBytes = await readFile(path.join(path.dirname(result.capturePath), 'case-input.json'));
    const retainedCommandsBytes = await readFile(path.join(path.dirname(result.capturePath), 'commands.jsonl'));
    assert.equal(savedCapture.input.sha256, createHash('sha256').update(retainedInputBytes).digest('hex'));
    assert.equal(savedCapture.commandsSha256, createHash('sha256').update(retainedCommandsBytes).digest('hex'));
    const retainedRecords = retainedCommandsBytes.toString('utf8').trimEnd().split(/\r?\n/).map(line => JSON.parse(line));
    const contextRecord = retainedRecords.find(record => record.id === 'initial-authorization-context');
    const contextArgs = buildCommands(input).find(command => command.id === 'rest-authorization-context').args;
    assert.deepEqual(contextRecord.args, contextArgs.map((value, index) => (
      contextArgs[index - 1] === '--gateway-url' ? `${value}/` : value
    )));
    assert.equal(savedCapture.caseId, input.caseId);
    assert.equal(savedCapture.mode, 'before-capture');
    assert.equal(savedCapture.caseKind, input.caseKind);
    assert.equal(savedCapture.actorId, input.expectedActorId);
    assert.deepEqual(validateInput({ ...example, target: savedCapture.target }).target, input.target);
    assert.equal(savedCapture.apphostSha256, apphostSha256);
    assert.equal(savedCapture.scriptSha256,
      createHash('sha256').update(await readFile(scriptPath)).digest('hex'));
    assert.equal(savedCapture.input.gateRequestId, input.expectedGateRequestId);
    await writeFile(afterInputPath, JSON.stringify({
      ...example,
      mode: 'after-observation',
      apphostSha256,
      expectedRecoveryOperationId: randomUUID(),
      beforeCaptureSha256: result.captureSha256,
    }));
    const dryRun = await runCase(
      {
        inputPath: afterInputPath,
        outputPath: afterOutputPath,
        beforeEvidencePath: result.capturePath,
        dryRun: true,
      },
      { AGENTWEAVER_CLI_PATH: cliPath },
    );
    assert.equal(dryRun.status, 'validated-not-run');
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
});

test('command flow distinguishes a stable lost gate from an intervening answer', async () => {
  const scratch = path.join(directory, `gate-flow-${randomUUID()}`);
  const cliName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  const cliPath = path.join(scratch, cliName);
  const beforeInputPath = path.join(scratch, 'before-case.json');
  const beforeOutputPath = path.join(scratch, 'before-evidence');
  const afterInputPath = path.join(scratch, 'after-case.json');
  await mkdir(scratch);
  try {
    await writeFile(cliPath, 'fake apphost');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    await writeFile(beforeInputPath, JSON.stringify({ ...example, apphostSha256 }));
    const before = beforeCapture().capture;
    const beforeRecords = beforeCaptureRecords(before);
    let beforeIndex = 0;
    const beforeResult = await runCase(
      { inputPath: beforeInputPath, outputPath: beforeOutputPath, dryRun: false },
      { AGENTWEAVER_CLI_PATH: cliPath },
      () => {
        const { id: _id, args: _args, ...record } = beforeRecords[beforeIndex++];
        return record;
      },
    );
    assert.equal(beforeResult.status, 'captured');

    const actualOperationId = randomUUID();
    const afterInput = {
      ...example,
      mode: 'after-observation',
      apphostSha256,
      expectedRecoveryOperationId: actualOperationId,
      beforeCaptureSha256: beforeResult.captureSha256,
    };
    await writeFile(afterInputPath, JSON.stringify(afterInput));
    const initial = afterSnapshot({ operationId: actualOperationId });
    initial.context = before.outputs.context;

    async function runObservation(outputName, first = initial, final = first) {
      const records = beforeCaptureRecords({ outputs: first });
      const replaceFinal = (id, value) => {
        const record = records.find(item => item.id === id);
        record.stdout = JSON.stringify({ status: 200, body: value });
      };
      replaceFinal('initial-final-run-status', final.runStatus);
      replaceFinal('initial-final-run-status-rest', final.runStatus);
      replaceFinal('initial-final-session-status', final.sessionStatus);
      replaceFinal('initial-final-session-status-rest', final.sessionStatus);
      replaceFinal('initial-final-decisions', final.decisions);
      let index = 0;
      return runCase(
        {
          inputPath: afterInputPath,
          outputPath: path.join(scratch, outputName),
          beforeEvidencePath: beforeResult.capturePath,
          dryRun: false,
        },
        { AGENTWEAVER_CLI_PATH: cliPath },
        () => {
          const { id: _id, args: _args, ...record } = records[index++];
          return record;
        },
      );
    }

    const stableLostGate = structuredClone(initial);
    stableLostGate.decisions.pendingGate = null;
    stableLostGate.sessionStatus.blockers = [];
    const failed = await runObservation('stable-lost-gate', stableLostGate);
    assert.equal(failed.status, 'failed');
    assert.equal(caseExitCode(failed.status), 1);

    const answered = structuredClone(initial);
    answered.runStatus.stateVersion += 1;
    answered.sessionStatus.stateVersion += 1;
    answered.decisions.stateVersion += 1;
    answered.decisions.pendingGate = null;
    answered.sessionStatus.blockers = [];
    const blocked = await runObservation('answered-during-observation', initial, answered);
    assert.equal(blocked.status, 'blocked');
    assert.equal(caseExitCode(blocked.status), 2);
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
});

test('arguments and apphost path reject unsupported options and non-native executables', () => {
  assert.throws(() => parseArgs(['--input', 'case.json', '--output', 'evidence', '--token', 'secret']), /Use --input/);
  assert.throws(() => parseArgs(['--input', 'case.json', '--input', 'other.json', '--output', 'evidence']), /Use --input/);
  assert.throws(() => resolveCliPath('agentweaver.exe'), /absolute path/);
  assert.throws(() => resolveCliPath(path.resolve(directory, 'node.exe')), /native/);
});
