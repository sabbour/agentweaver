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

test('child and effect inputs reject cross-kind fields and duplicate operation IDs', () => {
  const { expectedGateRequestId: _, ...common } = example;
  const child = { ...common, caseKind: 'child-join' };
  const effect = {
    ...common,
    caseKind: 'committed-effect',
    expectedEffectOperationId: '00000000-0000-4000-8000-000000000004',
  };
  assert.equal(validateInput(child).caseKind, 'child-join');
  assert.equal(validateInput(effect).caseKind, 'committed-effect');
  assert.throws(() => validateInput({ ...child, expectedGateRequestId: requestId }), /schema/);
  assert.throws(() => validateInput({ ...child, children: [] }), /schema/);
  assert.throws(() => validateInput({ ...effect, receipt: {} }), /schema/);
  assert.throws(() => validateInput({
    ...effect, expectedEffectOperationId: effect.expectedRecoveryOperationId,
  }), /schema/);
});

test('child and effect examples use the supported input schema', async () => {
  for (const [file, kind] of [
    ['checkpoint-recovery-child-join-observation.json', 'child-join'],
    ['checkpoint-recovery-committed-effect-observation.json', 'committed-effect'],
  ]) {
    const value = JSON.parse(await readFile(path.resolve(directory, '..', 'examples', file), 'utf8'));
    assert.equal(validateInput(value).caseKind, kind);
  }
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

const childMessage = '00000000-0000-4000-8000-000000000311';
const effectId = '00000000-0000-4000-8000-00000000031a';

function childSnapshot({ after = false, effect = false, fixed = false } = {}) {
  const fence = after ? 10 : 9;
  const version = after ? 8 : 7;
  const data = snapshot({ fence, stateVersion: version });
  data.context = {
    contractVersion: 1, issuer: 'https://issuer.example', actorId: input.expectedActorId,
    tenantId: input.target.tenantId, membershipRevision: 2,
    boundProjectId: input.target.projectId, boundRunId: input.target.runId,
    effectiveAuthority: [{ resourceType: 'project', resourceId: input.target.projectId,
      permissions: [{ permission: 'readRunSelection', roleRevision: 1 }] }],
  };
  data.decisions.pendingGate = null;
  data.sessionStatus.blockers = [];
  Object.assign(data.sessionStatus, { kind: 'coordinator', detached: false, parentSessionId: null,
    runtimeStartup: { acceptedSelectionHash: 'a'.repeat(64) } });
  Object.assign(data.tree.nodes[0], { kind: 'coordinator', lifecycle: 'active', detached: false,
    parentSessionId: null, archivedAt: null, stateVersion: version, logicalTurnOrdinal: 1 });
  data.tree.nodes.push({
    identity: { ...data.sessionStatus.identity, sessionId: 'child-1' },
    rootSessionId: input.target.sessionId, parentSessionId: input.target.sessionId,
    kind: 'childWork', detached: false, lifecycle: 'active', archivedAt: null,
    executionFence: fence, stateVersion: 2, logicalTurnOrdinal: 1,
  });
  data.sessionStatus.executionRecovery = {
    state: 'available', unavailableCode: null,
    checkpointId: after ? 'recovery-checkpoint' : 'checkpoint-9',
    checkpointRevision: after ? 4 : 3, workPlanId: 'plan-1',
    checkpointDecisionStateVersion: version, currentDecisionStateVersion: version,
    steps: [
      { stepId: 'implement', workItemId: fixed ? null : 'work-1', associationId: 'work-1',
        checkpointStatus: 'running', readiness: null, effectRecoveryState: 'mayRepeat' },
      { stepId: 'later', workItemId: 'work-2', associationId: 'work-2',
        checkpointStatus: 'pending', readiness: 'waitingOnDependencies', effectRecoveryState: null },
    ],
    pendingChildDispatches: [{ associationId: 'work-1', childSessionId: 'child-1',
      messageId: childMessage, effectRecoveryState: 'mayRepeat' }],
    platformGateOperations: [], failedDependencyIds: [], pendingGate: null,
  };
  if (effect) {
    data.sessionStatus.executionRecovery.steps.push({
      stepId: 'publish', workItemId: null, associationId: null,
      checkpointStatus: after ? 'succeeded' : 'running', readiness: null,
      effectRecoveryState: after ? 'terminalReceiptRecorded' : 'mayRepeat',
    });
    data.sessionStatus.executionRecovery.platformGateOperations.push({
      stepId: 'publish', gate: 'openPullRequest', operationId: effectId,
      evidenceState: after ? 'terminalReceiptRecorded' : 'intentOnly',
      effectRecoveryState: after ? null : 'mayRepeat',
    });
    data.sessionStatus.executionRecovery.steps.push({
      stepId: 'earlier-child', workItemId: 'work-completed', associationId: 'work-completed',
      checkpointStatus: 'succeeded', readiness: null, effectRecoveryState: 'checkpointResultRecorded',
    });
    data.tree.nodes.push({
      identity: { ...data.sessionStatus.identity, sessionId: 'child-completed' },
      rootSessionId: input.target.sessionId, parentSessionId: input.target.sessionId,
      kind: 'childWork', detached: false, lifecycle: 'completed', archivedAt: null,
      executionFence: fence, stateVersion: 3, logicalTurnOrdinal: 1,
    });
  }
  if (after) {
    const recovery = {
      operationId, phase: 'ready', currentStep: 'ready', boundaryKind: 'childJoin',
      dispatchHeld: false, childManifestSha256: 'b'.repeat(64),
      sourceCheckpointId: 'checkpoint-9', sourceCheckpointRevision: 3,
      sourceCheckpointHash: 'c'.repeat(64), recoveryCheckpointId: 'recovery-checkpoint',
      recoveryCheckpointRevision: 4, recoveryCheckpointHash: 'd'.repeat(64),
      previousExecutionFence: 9, executionFence: 10, previousRegistrationRevision: 1,
      previousRuntimeInstanceId: '00000000-0000-4000-8000-000000000201',
      runtimeInstanceId: '00000000-0000-4000-8000-000000000202',
      recoveryMaterialMode: 'nativeCache', decisionStateVersion: 8,
    };
    data.runStatus.checkpointRecovery = recovery;
    data.sessionStatus.checkpointRecovery = structuredClone(recovery);
  }
  return data;
}

function childRecords(initial, final = initial) {
  const records = beforeCaptureRecords({ outputs: initial });
  const commands = buildCommands(input);
  if (initial.events.length > 1) {
    const first = records.findIndex(record => record.id === 'initial-journal-page-1');
    for (let index = 1; index < initial.events.length; index += 1) {
      records.splice(first + index, 0, {
        ...records[first], id: `initial-journal-page-${index + 1}`,
        stdout: JSON.stringify({ status: 200, body: initial.events[index] }),
      });
    }
  }
  for (const [id, value] of [
    ['initial-final-run-status', final.runStatus], ['initial-final-run-status-rest', final.runStatus],
    ['initial-final-session-status', final.sessionStatus], ['initial-final-session-status-rest', final.sessionStatus],
    ['initial-final-decisions', final.decisions],
  ]) records.find(record => record.id === id).stdout = JSON.stringify({ status: 200, body: value });
  const template = records.at(-1);
  records.push(
    { ...template, id: 'initial-final-authorization-context',
      args: commands.find(command => command.id === 'rest-authorization-context').args,
      stdout: JSON.stringify({ status: 200, body: final.context }) },
    { ...template, id: 'initial-final-session-tree',
      args: commands.find(command => command.id === 'session-tree').args,
      stdout: JSON.stringify({ status: 200, body: final.tree }) },
  );
  return records;
}

test('child and effect observations use retained commands and stable source-bound reads', async t => {
  const scratch = path.join(directory, `child-flow-${randomUUID()}`);
  await mkdir(scratch);
  try {
    const cliPath = path.join(scratch, process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver');
    await writeFile(cliPath, 'fake apphost');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    const { expectedGateRequestId: _, ...common } = example;
    let serial = 0;
    let retainedCommands = [];
    async function run(kind, phase, fixture, beforeResult, final = fixture, inputChanges = {}, mutateRecords = () => {}) {
      const n = serial++;
      const value = { ...common, caseKind: kind, apphostSha256,
        ...(kind === 'committed-effect' ? { expectedEffectOperationId: effectId } : {}),
        mode: phase, ...(beforeResult ? { beforeCaptureSha256: beforeResult.captureSha256 } : {}),
        ...inputChanges };
      const inputPath = path.join(scratch, `input-${n}.json`);
      const outputPath = path.join(scratch, `output-${n}`);
      await writeFile(inputPath, JSON.stringify(value));
      const records = childRecords(fixture, final);
      mutateRecords(records);
      let index = 0;
      const result = await runCase({ inputPath, outputPath, beforeEvidencePath: beforeResult?.capturePath },
        { AGENTWEAVER_CLI_PATH: cliPath }, (_cli, args) => {
          assert.ok(!args.some(item => /recoverRun|answerGate|cancelProjectRun/.test(item)));
          assert.ok(index < records.length, `Unexpected command: ${args.join(' ')}`);
          const { id: _id, args: _args, ...record } = records[index++];
          return record;
        });
      assert.deepEqual(JSON.parse(await readFile(path.join(outputPath, 'result.json'), 'utf8')), result);
      if (index) {
        const retained = (await readFile(path.join(outputPath, 'commands.jsonl'), 'utf8'))
          .trimEnd().split('\n').map(line => JSON.parse(line));
        retainedCommands = retained;
        for (const record of retained.filter(item => /status-rest$/.test(item.id))) {
          assert.deepEqual(JSON.parse(record.stdout),
            JSON.parse(records.find(item => item.id === record.id).stdout));
        }
      }
      return result;
    }

    const before = await run('child-join', 'before-capture', childSnapshot());
    assert.equal(before.status, 'captured', before.reason);
    const after = childSnapshot({ after: true });
    const passed = await run('child-join', 'after-observation', after, before);
    assert.equal(passed.status, 'passed', passed.reason);
    assert.equal(passed.members[0].messageId, childMessage);
    assert.equal(passed.members[0].outcome, 'pending');
    assert.equal(passed.fullPlanContentProof.status, 'blocked');
    assert.equal(passed.dependencyGraphProof.status, 'blocked');
    assert.equal(passed.committedRecoveryEventProof.status, 'blocked');
    const pagedSource = childSnapshot();
    pagedSource.events[0].hasMore = true;
    pagedSource.events[0].nextCursor = 'cursor-1';
    pagedSource.events.push({ events: [{ ...structuredClone(event), position: 2, eventId: randomUUID() }],
      nextCursor: null, hasMore: false });
    const pagedBefore = await run('child-join', 'before-capture', pagedSource);
    assert.equal(pagedBefore.status, 'captured', pagedBefore.reason);
    assert.equal(pagedBefore.journal.pagesRead, 2);
    const pagedAfter = await run('child-join', 'after-observation', after, pagedBefore);
    assert.equal(pagedAfter.status, 'passed', pagedAfter.reason);

    const joined = structuredClone(after);
    joined.sessionStatus.executionRecovery.checkpointId = 'later-checkpoint';
    joined.sessionStatus.executionRecovery.checkpointRevision = 5;
    joined.sessionStatus.executionRecovery.pendingChildDispatches = [];
    Object.assign(joined.sessionStatus.executionRecovery.steps[0],
      { checkpointStatus: 'succeeded', effectRecoveryState: 'checkpointResultRecorded' });
    joined.tree.nodes[1].lifecycle = 'completed';
    const joinResult = await run('child-join', 'after-observation', joined, before);
    assert.equal(joinResult.status, 'passed', joinResult.reason);
    assert.equal(joinResult.members[0].outcome, 'checkpointResultRecorded');
    assert.equal(joinResult.members[0].checkpointId, 'later-checkpoint');

    const reordered = structuredClone(after);
    reordered.tree.nodes.reverse();
    reordered.sessionStatus.executionRecovery.steps.reverse();
    reordered.sessionStatus.executionRecovery.steps[0].readiness = 'dependencyReady';
    const reorderResult = await run('child-join', 'after-observation', reordered, before);
    assert.equal(reorderResult.status, 'passed', reorderResult.reason);
    const fixedBefore = await run('child-join', 'before-capture', childSnapshot({ fixed: true }));
    const fixed = await run('child-join', 'after-observation', childSnapshot({ after: true, fixed: true }), fixedBefore);
    assert.equal(fixed.status, 'passed', fixed.reason);
    assert.equal(fixed.members[0].workItemId, null);

    const effectBefore = await run('committed-effect', 'before-capture', childSnapshot({ effect: true }));
    assert.equal(effectBefore.status, 'captured', effectBefore.reason);
    const effectAfter = childSnapshot({ after: true, effect: true });
    const receipt = await run('committed-effect', 'after-observation', effectAfter, effectBefore);
    assert.equal(receipt.status, 'passed', receipt.reason);
    assert.equal(receipt.effect.operationId, effectId);
    assert.equal(receipt.effect.evidenceState, 'terminalReceiptRecorded');
    assert.equal(receipt.physicalNoDuplicateProof.status, 'blocked');
    await t.test('unrelated terminal BuildTest and selected operation identity', async () => {
      const addBuild = data => {
        data.sessionStatus.executionRecovery.steps.push({
          stepId: 'build', workItemId: null, associationId: null,
          checkpointStatus: 'succeeded', readiness: null, effectRecoveryState: 'terminalReceiptRecorded',
        });
        data.sessionStatus.executionRecovery.platformGateOperations.unshift({
          stepId: 'build', gate: 'buildTest', operationId: '00000000-0000-4000-8000-000000000313',
          evidenceState: 'terminalReceiptRecorded', effectRecoveryState: null,
        });
      };
      const source = childSnapshot({ effect: true });
      const current = childSnapshot({ after: true, effect: true });
      addBuild(source);
      addBuild(current);
      const capture = await run('committed-effect', 'before-capture', source);
      assert.equal(capture.status, 'captured', capture.reason);
      const result = await run('committed-effect', 'after-observation', current, capture);
      assert.equal(result.status, 'passed', result.reason);
      assert.equal(result.effect.operationId, effectId);
      assert.equal(result.effect.evidenceState, 'terminalReceiptRecorded');
      for (const proof of ['fullPlanContentProof', 'dependencyGraphProof',
        'committedRecoveryEventProof', 'physicalNoDuplicateProof']) {
        assert.equal(result[proof].status, 'blocked', proof);
      }
      for (const duplicate of [
        { ...current.sessionStatus.executionRecovery.platformGateOperations[1] },
        { ...current.sessionStatus.executionRecovery.platformGateOperations[0], operationId: effectId.toUpperCase() },
        { ...current.sessionStatus.executionRecovery.platformGateOperations[0], stepId: 'publish' },
      ]) {
        const changed = structuredClone(current);
        changed.sessionStatus.executionRecovery.platformGateOperations.push(duplicate);
        const rejected = await run('committed-effect', 'after-observation', changed, capture);
        assert.equal(rejected.status, 'failed', rejected.reason);
      }
      const changed = structuredClone(current);
      changed.sessionStatus.executionRecovery.platformGateOperations[0].evidenceState = 'unknown';
      const unrelated = await run('committed-effect', 'after-observation', changed, capture);
      assert.equal(unrelated.status, 'blocked', unrelated.reason);
      const duplicateSource = structuredClone(source);
      duplicateSource.sessionStatus.executionRecovery.platformGateOperations.push({
        ...duplicateSource.sessionStatus.executionRecovery.platformGateOperations[0], stepId: 'publish',
      });
      const ambiguous = await run('committed-effect', 'before-capture', duplicateSource);
      assert.equal(ambiguous.status, 'blocked', ambiguous.reason);
    });
    await t.test('empty owner UUIDs are blocked', async t => {
      const zero = '00000000-0000-0000-0000-000000000000';
      for (const field of ['previousRuntimeInstanceId', 'runtimeInstanceId']) {
        await t.test(field, async () => {
          const current = structuredClone(after);
          current.runStatus.checkpointRecovery[field] = zero;
          current.sessionStatus.checkpointRecovery[field] = zero;
          const result = await run('child-join', 'after-observation', current, before);
          assert.equal(result.status, 'blocked', `${field}: ${result.reason}`);
        });
      }
      for (const phase of ['before-capture', 'after-observation']) {
        await t.test(`${phase} message`, async () => {
          const fixture = phase === 'before-capture' ? childSnapshot() : structuredClone(after);
          fixture.sessionStatus.executionRecovery.pendingChildDispatches[0].messageId = zero;
          const result = await run('child-join', phase, fixture, phase === 'before-capture' ? undefined : before);
          assert.equal(result.status, 'blocked', result.reason);
        });
      }
      await t.test('selected operation IDs', async () => {
        for (const [kind, field] of [['child-join', 'expectedRecoveryOperationId'],
          ['committed-effect', 'expectedEffectOperationId']]) {
          await assert.rejects(run(kind, 'before-capture', childSnapshot({ effect: kind === 'committed-effect' }),
            undefined, undefined, { [field]: zero }), /schema/);
        }
        const recovery = structuredClone(after);
        recovery.runStatus.checkpointRecovery.operationId = zero;
        recovery.sessionStatus.checkpointRecovery.operationId = zero;
        assert.equal((await run('child-join', 'after-observation', recovery, before)).status, 'blocked');
        const effect = structuredClone(effectAfter);
        effect.sessionStatus.executionRecovery.platformGateOperations[0].operationId = zero;
        assert.equal((await run('committed-effect', 'after-observation', effect, effectBefore)).status, 'blocked');
        assert.equal(validateInput({ ...example, expectedRecoveryOperationId: zero }).expectedRecoveryOperationId, zero);
      });
    });
    await t.test('sequential API and REST progress is blocked', async t => {
      for (const id of ['initial-run-status-rest', 'initial-session-status-rest',
        'initial-final-run-status-rest', 'initial-final-session-status-rest']) {
        await t.test(id, async () => {
          const result = await run('child-join', 'after-observation', after, before, after, {}, records => {
            const record = records.find(item => item.id === id);
            const envelope = JSON.parse(record.stdout);
            envelope.body.stateVersion += 1;
            record.stdout = JSON.stringify(envelope);
          });
          assert.equal(result.status, 'blocked', `${id}: ${result.reason}`);
          assert.match(result.reason, /changed during observation/);
        });
      }
    });
    await t.test('stable status contradictions fail only with complete bound reads', async t => {
      for (const [kind, current, capture] of [
        ['child-join', after, before], ['committed-effect', effectAfter, effectBefore],
      ]) {
        for (const [status, label] of [['run', 'Run status'], ['session', 'Root-session status']]) {
          await t.test(`${kind} ${status}`, async () => {
            const change = (records, ids, mutate = body => { body.checkpointRecovery.reasonCode = 'different'; }) => {
              for (const id of ids) {
                const record = records.find(item => item.id === id);
                const envelope = JSON.parse(record.stdout);
                mutate(envelope.body);
                record.stdout = JSON.stringify(envelope);
              }
            };
            const initialId = `initial-${status}-status-rest`;
            const finalId = `initial-final-${status}-status-rest`;
            const stable = await run(kind, 'after-observation', current, capture, current, {},
              records => change(records, [initialId, finalId]));
            assert.equal(stable.status, 'failed', stable.reason);
            assert.equal(caseExitCode(stable.status), 1);
            assert.equal(stable.reason, `${label} returned different API and REST status bodies.`);
            assert.equal(retainedCommands.at(-1).id, 'initial-final-session-tree');
            assert.ok(retainedCommands.some(record => record.id === finalId));
            for (const id of [initialId, finalId]) {
              const record = retainedCommands.find(item => item.id === id);
              assert.equal(JSON.parse(record.stdout).body.checkpointRecovery.reasonCode, 'different');
              const apiId = id.replace('-status-rest', '-status');
              assert.notEqual(JSON.parse(retainedCommands.find(item => item.id === apiId).stdout)
                .body.checkpointRecovery.reasonCode, 'different');
            }
            for (const id of [initialId, finalId]) {
              const progress = await run(kind, 'after-observation', current, capture, current, {},
                records => change(records, [id]));
              assert.equal(progress.status, 'blocked', progress.reason);
              assert.equal(caseExitCode(progress.status), 2);
              assert.match(progress.reason, /changed during observation/);
              assert.equal(retainedCommands.at(-1).id, 'initial-final-session-tree');
            }
            const missing = await run(kind, 'after-observation', current, capture, current, {}, records => {
              change(records, [initialId]);
              records.find(item => item.id === finalId).exitCode = 1;
            });
            assert.equal(missing.status, 'blocked', missing.reason);
            assert.equal(caseExitCode(missing.status), 2);
            assert.match(missing.reason, /did not complete successfully/);
            const unbound = await run(kind, 'after-observation', current, capture, current, {},
              records => change(records, [initialId, finalId], body => { body.executionFence = 11; }));
            assert.equal(unbound.status, 'blocked', unbound.reason);
            assert.match(unbound.reason, /not bound/);
            for (const [field, value] of [
              ['operationId', '00000000-0000-4000-8000-000000000999'],
              ['sourceCheckpointId', 'other'],
              ['sourceCheckpointRevision', 2],
              ['sourceCheckpointHash', 'e'.repeat(64)],
              ['recoveryCheckpointId', 'other'],
              ['recoveryCheckpointHash', 'e'.repeat(64)],
              ['childManifestSha256', 'e'.repeat(64)],
              ['previousExecutionFence', 8],
              ['executionFence', 11],
              ['runtimeInstanceId', '00000000-0000-4000-8000-000000000999'],
              ['gateAuthorizedActorId', 'other'],
              ['supersedesOperationId', '00000000-0000-4000-8000-000000000999'],
              ['supersededByOperationId', '00000000-0000-4000-8000-000000000999'],
              ['supersededByReceiptId', '00000000-0000-4000-8000-000000000999'],
              ['supersededByPayloadHash', 'e'.repeat(64)],
            ]) {
              const result = await run(kind, 'after-observation', current, capture, current, {},
                records => change(records, [initialId, finalId], body => {
                  body.checkpointRecovery.reasonCode = 'different';
                  body.checkpointRecovery[field] = value;
                }));
              assert.equal(result.status, 'blocked', `${field}: ${result.reason}`);
            }
            const missingRecovery = await run(kind, 'after-observation', current, capture, current, {},
              records => change(records, [initialId, finalId], body => { delete body.checkpointRecovery; }));
            assert.equal(missingRecovery.status, 'blocked', missingRecovery.reason);
            const changedState = await run(kind, 'after-observation', current, capture, current, {},
              records => change(records, [initialId, finalId], body => { body.stateVersion += 1; }));
            assert.equal(changedState.status, 'blocked', changedState.reason);
            const differentBodies = structuredClone(current);
            differentBodies.sessionStatus.checkpointRecovery.reasonCode = 'different';
            const noPass = await run(kind, 'after-observation', differentBodies, capture);
            assert.equal(noPass.status, 'blocked', noPass.reason);
            const missingTree = await run(kind, 'after-observation', current, capture, current, {}, records => {
              change(records, [initialId, finalId]);
              records.find(item => item.id === 'initial-final-session-tree').exitCode = 1;
            });
            assert.equal(missingTree.status, 'blocked', missingTree.reason);
            assert.match(missingTree.reason, /final-session-tree did not complete successfully/);
            const changedAuthority = structuredClone(current);
            changedAuthority.context.membershipRevision += 1;
            const mixed = await run(kind, 'after-observation', current, capture, changedAuthority, {},
              records => change(records, [initialId, finalId]));
            assert.equal(mixed.status, 'blocked', mixed.reason);
            assert.match(mixed.reason, /Authorization, tree, checkpoint, or recovery changed/);
          });
        }
      }
      const source = childSnapshot();
      const sourceContradiction = await run('child-join', 'before-capture', source, undefined, source, {}, records => {
        for (const id of ['initial-run-status-rest', 'initial-final-run-status-rest']) {
          const record = records.find(item => item.id === id);
          const envelope = JSON.parse(record.stdout);
          envelope.body.observedAt = 'different';
          record.stdout = JSON.stringify(envelope);
        }
      });
      assert.equal(sourceContradiction.status, 'failed', sourceContradiction.reason);
      assert.equal(retainedCommands.at(-1).id, 'initial-final-session-tree');
      const awaitingReceipt = structuredClone(effectAfter);
      Object.assign(awaitingReceipt.sessionStatus.executionRecovery.platformGateOperations[0],
        { evidenceState: 'committedAwaitingReceipt', effectRecoveryState: 'reconcileCommitted' });
      Object.assign(awaitingReceipt.sessionStatus.executionRecovery.steps[2],
        { checkpointStatus: 'running', effectRecoveryState: 'reconcileCommitted' });
      const contradictionWhilePending = await run('committed-effect', 'after-observation',
        awaitingReceipt, effectBefore, awaitingReceipt, {}, records => {
          for (const id of ['initial-run-status-rest', 'initial-final-run-status-rest']) {
            const record = records.find(item => item.id === id);
            const envelope = JSON.parse(record.stdout);
            envelope.body.observedAt = 'different';
            record.stdout = JSON.stringify(envelope);
          }
        });
      assert.equal(contradictionWhilePending.status, 'failed', contradictionWhilePending.reason);
      assert.match(contradictionWhilePending.reason, /^Run status returned different/);
      const wrongSource = structuredClone(after);
      wrongSource.runStatus.checkpointRecovery.sourceCheckpointId = 'other';
      wrongSource.sessionStatus.checkpointRecovery.sourceCheckpointId = 'other';
      const sourceUnbound = await run('child-join', 'after-observation', wrongSource, before, wrongSource, {}, records => {
        for (const id of ['initial-run-status-rest', 'initial-final-run-status-rest']) {
          const record = records.find(item => item.id === id);
          const envelope = JSON.parse(record.stdout);
          envelope.body.observedAt = 'different';
          record.stdout = JSON.stringify(envelope);
        }
      });
      assert.equal(sourceUnbound.status, 'blocked', sourceUnbound.reason);
      assert.match(sourceUnbound.reason, /source, plan, versions, or checkpoint lineage/);
    });
    await t.test('settled previous fence fails but unbound evidence blocks', async () => {
      const current = structuredClone(after);
      current.runStatus.checkpointRecovery.previousExecutionFence = 8;
      current.sessionStatus.checkpointRecovery.previousExecutionFence = 8;
      const result = await run('child-join', 'after-observation', current, before);
      assert.equal(result.status, 'failed', result.reason);
      current.runStatus.checkpointRecovery.sourceCheckpointId = 'other';
      current.sessionStatus.checkpointRecovery.sourceCheckpointId = 'other';
      const unbound = await run('child-join', 'after-observation', current, before);
      assert.equal(unbound.status, 'blocked', unbound.reason);
      current.runStatus.checkpointRecovery.sourceCheckpointId = 'checkpoint-9';
      current.sessionStatus.checkpointRecovery.sourceCheckpointId = 'checkpoint-9';
      current.sessionStatus.runtimeStartup.acceptedSelectionHash = 'e'.repeat(64);
      const changedSelection = await run('child-join', 'after-observation', current, before);
      assert.equal(changedSelection.status, 'blocked', changedSelection.reason);
      current.sessionStatus.runtimeStartup.acceptedSelectionHash = 'a'.repeat(64);
      delete current.runStatus.checkpointRecovery.previousExecutionFence;
      delete current.sessionStatus.checkpointRecovery.previousExecutionFence;
      const missing = await run('child-join', 'after-observation', current, before);
      assert.equal(missing.status, 'blocked', missing.reason);
    });
    const pending = structuredClone(effectAfter);
    Object.assign(pending.sessionStatus.executionRecovery.platformGateOperations[0],
      { evidenceState: 'committedAwaitingReceipt', effectRecoveryState: 'reconcileCommitted' });
    Object.assign(pending.sessionStatus.executionRecovery.steps[2],
      { checkpointStatus: 'running', effectRecoveryState: 'reconcileCommitted' });
    const pendingResult = await run('committed-effect', 'after-observation', pending, effectBefore);
    assert.equal(pendingResult.status, 'blocked');
    assert.match(pendingResult.reason, /awaits reconciliation/);

    for (const [name, mutate, expected] of [
      ['source ID', x => { x.runStatus.checkpointRecovery.sourceCheckpointId = 'other'; }, 'blocked'],
      ['source revision', x => { x.runStatus.checkpointRecovery.sourceCheckpointRevision = 4; }, 'blocked'],
      ['recovery revision', x => { x.runStatus.checkpointRecovery.recoveryCheckpointRevision = 5; }, 'blocked'],
      ['checkpoint ID', x => { x.sessionStatus.executionRecovery.checkpointId = 'other'; }, 'blocked'],
      ['plan', x => { x.sessionStatus.executionRecovery.workPlanId = 'other'; }, 'blocked'],
      ['version', x => { x.decisions.stateVersion = 9; }, 'blocked'],
      ['projection version', x => { x.sessionStatus.executionRecovery.checkpointDecisionStateVersion = 7; }, 'blocked'],
      ['recovery version', x => { x.runStatus.checkpointRecovery.decisionStateVersion = 9; }, 'blocked'],
      ['missing hash', x => { delete x.runStatus.checkpointRecovery.childManifestSha256; }, 'blocked'],
      ['missing runtime', x => { delete x.runStatus.checkpointRecovery.runtimeInstanceId; }, 'blocked'],
      ['missing hold', x => { delete x.runStatus.checkpointRecovery.dispatchHeld; }, 'blocked'],
      ['empty material', x => { x.runStatus.checkpointRecovery.recoveryMaterialMode = ''; }, 'blocked'],
      ['unsupported material', x => { x.runStatus.checkpointRecovery.recoveryMaterialMode = 'other'; }, 'failed'],
      ['held', x => { x.runStatus.checkpointRecovery.dispatchHeld = true; }, 'failed'],
      ['fence', x => { x.runStatus.checkpointRecovery.executionFence = 11; }, 'failed'],
      ['runtime', x => { x.runStatus.checkpointRecovery.runtimeInstanceId = x.runStatus.checkpointRecovery.previousRuntimeInstanceId; }, 'failed'],
      ['message', x => { x.sessionStatus.executionRecovery.pendingChildDispatches[0].messageId = randomUUID(); }, 'failed'],
      ['child identity', x => { x.sessionStatus.executionRecovery.pendingChildDispatches[0].childSessionId = 'different-child'; }, 'failed'],
      ['association identity', x => { x.sessionStatus.executionRecovery.pendingChildDispatches[0].associationId = 'different-work'; }, 'failed'],
      ['lost child', x => { x.tree.nodes.pop(); }, 'failed'],
      ['duplicate tuple', x => { x.sessionStatus.executionRecovery.pendingChildDispatches.push(structuredClone(x.sessionStatus.executionRecovery.pendingChildDispatches[0])); }, 'failed'],
      ['missing tuple', x => { x.sessionStatus.executionRecovery.pendingChildDispatches = []; }, 'blocked'],
      ['missing step', x => { x.sessionStatus.executionRecovery.steps.shift(); }, 'blocked'],
      ['unsafe revision', x => { x.sessionStatus.executionRecovery.checkpointRevision = Number.MAX_SAFE_INTEGER + 1; }, 'blocked'],
      ['fractional version', x => { x.decisions.stateVersion = 8.5; }, 'blocked'],
      ['unrelated result', x => { Object.assign(x.sessionStatus.executionRecovery.steps[1],
        { checkpointStatus: 'succeeded', effectRecoveryState: 'checkpointResultRecorded' }); }, 'blocked'],
      ['changed actor', x => { x.context.actorId = 'different'; }, 'blocked'],
      ['changed membership', x => { x.context.membershipRevision += 1; }, 'blocked'],
      ['changed authority', x => { x.context.effectiveAuthority[0].permissions[0].roleRevision += 1; }, 'blocked'],
      ['changed selection', x => { x.sessionStatus.runtimeStartup.acceptedSelectionHash = 'e'.repeat(64); }, 'blocked'],
    ]) {
      const changed = structuredClone(after);
      mutate(changed);
      changed.sessionStatus.checkpointRecovery = structuredClone(changed.runStatus.checkpointRecovery);
      const result = await run('child-join', 'after-observation', changed, before);
      assert.equal(result.status, expected, `${name}: ${result.reason}`);
      if (name === 'message') assert.match(result.reason, new RegExp(childMessage));
    }
    const unstable = structuredClone(after);
    unstable.tree.nodes[1].stateVersion += 1;
    const unstableResult = await run('child-join', 'after-observation', after, before, unstable);
    assert.equal(unstableResult.status, 'blocked');
    assert.match(unstableResult.reason, /changed during observation/);

    for (const [name, mutate, expected] of [
      ['changed operation', x => { x.platformGateOperations[0].operationId = randomUUID(); }, 'failed'],
      ['wrong gate', x => { x.platformGateOperations[0].gate = 'merge'; }, 'failed'],
      ['wrong step', x => { x.platformGateOperations[0].stepId = 'different'; }, 'failed'],
      ['missing operation', x => { x.platformGateOperations[0].operationId = null; }, 'blocked'],
      ['duplicate operation', x => { x.platformGateOperations.push(structuredClone(x.platformGateOperations[0])); }, 'failed'],
      ['failed receipt', x => { x.steps[2].checkpointStatus = 'failed'; }, 'failed'],
      ['unknown', x => { x.platformGateOperations[0].evidenceState = 'unknown'; }, 'blocked'],
      ['intent only', x => { Object.assign(x.platformGateOperations[0],
        { evidenceState: 'intentOnly', effectRecoveryState: 'mayRepeat' }); }, 'blocked'],
    ]) {
      const changed = structuredClone(effectAfter);
      mutate(changed.sessionStatus.executionRecovery);
      const result = await run('committed-effect', 'after-observation', changed, effectBefore);
      assert.equal(result.status, expected, `${name}: ${result.reason}`);
    }
    for (const [label, change] of [
      ['decision overflow', x => {
        x.runStatus.stateVersion = Number.MAX_SAFE_INTEGER;
        x.sessionStatus.stateVersion = Number.MAX_SAFE_INTEGER;
        x.decisions.stateVersion = Number.MAX_SAFE_INTEGER;
        x.sessionStatus.executionRecovery.currentDecisionStateVersion = Number.MAX_SAFE_INTEGER;
        x.sessionStatus.executionRecovery.checkpointDecisionStateVersion = Number.MAX_SAFE_INTEGER;
      }],
      ['fence overflow', x => {
        x.runStatus.executionFence = Number.MAX_SAFE_INTEGER;
        x.sessionStatus.executionFence = Number.MAX_SAFE_INTEGER;
        x.decisions.executionFence = Number.MAX_SAFE_INTEGER;
        x.tree.nodes.forEach(node => { node.executionFence = Number.MAX_SAFE_INTEGER; });
      }],
      ['revision overflow', x => { x.sessionStatus.executionRecovery.checkpointRevision = Number.MAX_SAFE_INTEGER; }],
    ]) {
      const source = childSnapshot();
      change(source);
      const capture = await run('child-join', 'before-capture', source);
      assert.equal(capture.status, 'captured', `${label}: ${capture.reason}`);
      const result = await run('child-join', 'after-observation', after, capture);
      assert.equal(result.status, 'blocked', label);
      assert.match(result.reason, /cannot advance safely/, label);
    }
    for (const value of [0, -1, 1.5, '7', null, Number.MAX_SAFE_INTEGER + 1]) {
      const source = childSnapshot();
      source.context.membershipRevision = value;
      const result = await run('child-join', 'before-capture', source);
      assert.equal(result.status, 'blocked', `membership revision ${value}`);
    }
    const dryInputPath = path.join(scratch, 'dry-input.json');
    const dryOutputPath = path.join(scratch, 'dry-output');
    await writeFile(dryInputPath, JSON.stringify({ ...common, caseKind: 'child-join', apphostSha256 }));
    const neverRun = () => { throw new Error('dry run invoked the apphost'); };
    const dryBefore = await runCase({ inputPath: dryInputPath, outputPath: dryOutputPath, dryRun: true },
      { AGENTWEAVER_CLI_PATH: cliPath }, neverRun);
    assert.equal(dryBefore.status, 'validated-not-run');
    await assert.rejects(access(dryOutputPath), { code: 'ENOENT' });
    await writeFile(dryInputPath, JSON.stringify({ ...common, caseKind: 'child-join', apphostSha256,
      mode: 'after-observation', beforeCaptureSha256: before.captureSha256 }));
    const dryAfter = await runCase({ inputPath: dryInputPath, outputPath: dryOutputPath, dryRun: true,
      beforeEvidencePath: before.capturePath }, { AGENTWEAVER_CLI_PATH: cliPath }, neverRun);
    assert.equal(dryAfter.status, 'validated-not-run');
    await assert.rejects(access(dryOutputPath), { code: 'ENOENT' });
    const changedTarget = await run('child-join', 'after-observation', after, before, after, {
      target: { ...example.target, projectId: 'other-project' },
    });
    assert.equal(changedTarget.status, 'blocked');
    assert.match(changedTarget.reason, /retained before-capture input/);
    const commandsPath = path.join(path.dirname(before.capturePath), 'commands.jsonl');
    const originalCommands = await readFile(commandsPath, 'utf8');
    const originalCapture = JSON.parse(await readFile(before.capturePath, 'utf8'));
    for (const [label, mutate] of [
      ['changed raw status', records => {
        const record = records.find(item => item.id === 'initial-session-status');
        const body = JSON.parse(record.stdout);
        body.body.executionRecovery.pendingChildDispatches[0].messageId = randomUUID();
        record.stdout = JSON.stringify(body);
      }],
      ['missing final tree', records => { records.pop(); }],
      ['changed command', records => {
        records.find(item => item.id === 'initial-session-status').args[2] = 'answerGate';
      }],
      ['mixed initial status', records => {
        const record = records.find(item => item.id === 'initial-run-status-rest');
        const body = JSON.parse(record.stdout);
        body.body.stateVersion += 1;
        record.stdout = JSON.stringify(body);
      }],
      ['mixed final status', records => {
        const record = records.find(item => item.id === 'initial-final-run-status-rest');
        const body = JSON.parse(record.stdout);
        body.body.stateVersion += 1;
        record.stdout = JSON.stringify(body);
      }],
    ]) {
      const records = originalCommands.trimEnd().split('\n').map(line => JSON.parse(line));
      mutate(records);
      const commandsText = `${records.map(record => JSON.stringify(record)).join('\n')}\n`;
      await writeFile(commandsPath, commandsText);
      const capture = structuredClone(originalCapture);
      capture.commandsSha256 = createHash('sha256').update(commandsText).digest('hex');
      const captureText = `${JSON.stringify(capture)}\n`;
      await writeFile(before.capturePath, captureText);
      const changedBefore = { ...before, captureSha256: createHash('sha256').update(captureText).digest('hex') };
      const tampered = await run('child-join', 'after-observation', after, changedBefore);
      assert.equal(tampered.status, 'blocked', label);
      assert.match(tampered.reason, /retained|final-session-tree/i, label);
    }
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
