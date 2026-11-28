import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  buildCleanupCommands,
  buildCommands,
  buildGenerateCommand,
  buildJobResultCommand,
  buildJobStatusCommand,
  childEnvironment,
  parseArgs,
  resolveCliPath,
  runCase,
  validateAcceptedSelection,
  validateCallerContext,
  validateGenerationResult,
  validateJobEnvelope,
  validateInput,
} from '../workflow-generation.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(directory, '..', 'workflow-generation.mjs');
const examplePath = path.resolve(directory, '..', 'examples', 'workflow-generation.json');
const example = JSON.parse(await readFile(examplePath, 'utf8'));
const apphostName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
const hash = 'a'.repeat(64);
const turnRoute = '/api/v1/projects/{projectId}/runs/{runId}/sessions/{sessionId}/turns/{eventId}';
const turnOperation = {
  operationId: 'readSessionTurnContent',
  method: 'GET',
  path: turnRoute,
};

function acceptedSelection(input) {
  return {
    projectId: input.target.projectId,
    runId: input.target.runId,
    projectConfigurationRevision: input.expectedSelection.projectConfigurationRevision,
    modelSelection: { reference: input.expectedSelection.modelSelectionReference },
    runLimits: { ...input.expectedSelection.runLimits },
  };
}

function completedEnvelope(input, jobId, status = 'completed') {
  const statusUrl = `/api/v1/projects/${input.target.projectId}/workflows/generation-jobs/${jobId}`;
  const definition = {
    id: 'generated-workflow',
    revision: '1',
    catalogVersion: '1',
    origin: 'generated',
    maximumWorkItems: 1,
    steps: [{
      id: 'plan',
      purpose: 'Create a plan',
      mode: 'fixed',
      order: 0,
      cardinality: { minimum: 1, maximum: 1 },
      dependsOn: [],
      allowedRoles: [],
      allowedPhases: [],
      allowedIsolationChoices: [],
      requiredProviderCapabilities: [],
      modelTaskKind: null,
      fixedWork: null,
      platformGate: null,
    }],
  };
  return {
    jobId,
    status,
    projectId: input.target.projectId,
    runId: input.target.runId,
    sessionId: input.target.sessionId,
    createdAt: '2026-11-17T00:00:00Z',
    updatedAt: '2026-11-17T00:00:01Z',
    statusUrl,
    resultUrl: `${statusUrl}/result`,
    artifact: status === 'completed' ? {
      artifactId: '00000000-0000-0000-0000-000000000000',
      workflowId: definition.id,
      definitionRevision: definition.revision,
    } : null,
    failure: status === 'failed' ? { code: 'workflow_generation_failed', message: 'Generation failed.' } : null,
    definition,
  };
}

function resultFor(input, envelope, definitionSha256Override = null) {
  const definition = envelope.definition;
  const definitionSha256 = definitionSha256Override
    ?? createHash('sha256').update(JSON.stringify(definition)).digest('hex');
  const artifactHash = createHash('sha256')
    .update(`${envelope.jobId.toLowerCase()}\0${definition.id}\0${definition.revision}\0${definitionSha256}`)
    .digest('hex')
    .slice(0, 32);
  const artifactId = [
    artifactHash.slice(0, 8),
    artifactHash.slice(8, 12),
    artifactHash.slice(12, 16),
    artifactHash.slice(16, 20),
    artifactHash.slice(20),
  ].join('-');
  envelope.artifact.artifactId = artifactId;
  const completion = {
    contractVersion: 1,
    requestId: randomUUID(),
    promptSha256: hash,
    outputSha256: hash,
    runtimeInstanceId: randomUUID(),
    registrationRevision: 1,
    registrationSha256: hash,
    executionFence: 1,
    acceptedSelectionHash: hash,
    sourceSha256: hash,
    sdkSessionSha256: hash,
    observeGrantId: randomUUID(),
    observeGrantRevision: 1,
    modelSelectionReference: input.expectedSelection.modelSelectionReference,
    modelId: 'model-1',
    modelSourceMode: 'hostedCopilot',
    reasoningEffort: null,
    maxPromptTokens: input.expectedSelection.runLimits.maxPromptTokens,
    image: { digest: `sha256:${hash}`, platform: 'linux/amd64', version: 1 },
    nativeMessageId: randomUUID(),
    nativeTurnStartEventId: randomUUID(),
    nativeTurnEndEventId: randomUUID(),
    nativeCompletionReceiptEventId: randomUUID(),
    nativeCompletedAt: '2026-11-17T00:00:01Z',
    nativeCursorBeforeSha256: hash,
    nativeCursorAfterSha256: hash,
    nativeEventIntervalSha256: hash,
    nativeEventCount: 2,
    usageEventIds: [],
    successfulToolCount: 0,
    failedToolCount: 0,
    userMaterial: { eventId: randomUUID(), position: 1, kind: 'turnContent', contentSha256: hash, bindingSha256: hash },
    assistantMaterial: { eventId: randomUUID(), position: 2, kind: 'turnContent', contentSha256: hash, bindingSha256: hash },
    cacheMaterial: { eventId: randomUUID(), position: 3, kind: 'sdkCache', contentSha256: hash, bindingSha256: hash },
    cacheBaselineCursorSha256: hash,
    cacheBaselineAfterCursorSha256: hash,
    cacheBaselineEventCount: 2,
    cacheBaselineIsComplete: true,
  };
  return {
    jobId: envelope.jobId,
    artifactId,
    workflowDefinition: definition,
    definitionSha256,
    nativeCompletion: completion,
    configurationRevision: input.expectedSelection.projectConfigurationRevision,
    acceptedSelectionHash: hash,
  };
}

function ok(body, status = 200) {
  return { exitCode: 0, signal: null, stdout: JSON.stringify({ status, body }), stderr: '', timedOut: false, error: null };
}

function plain(body) {
  return { exitCode: 0, signal: null, stdout: JSON.stringify(body), stderr: '', timedOut: false, error: null };
}

function fail() {
  return { exitCode: 1, signal: null, stdout: '', stderr: 'controlled failure', timedOut: false, error: null };
}

const queueFullCode = 'workflow_generation_queue_full';
const failedSubmitStdout = 'raw-submit-response';
const failedSubmitStderr = 'raw-submit-diagnostic';

function queueFull(code = queueFullCode) {
  return {
    ...fail(),
    stdout: JSON.stringify({ status: 503, body: { error: code, detail: failedSubmitStdout } }),
    stderr: failedSubmitStderr,
  };
}

async function withScratch(callback) {
  const scratch = path.join(directory, `workflow-case-${randomUUID()}`);
  await mkdir(scratch);
  try {
    const cliPath = path.join(scratch, apphostName);
    await writeFile(cliPath, 'controlled native apphost');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    const caseInput = validateInput({ ...example, apphostSha256 });
    const casePath = path.join(scratch, 'case.json');
    await writeFile(casePath, JSON.stringify({ ...example, apphostSha256 }));
    await callback({
      scratch,
      cliPath,
      casePath,
      caseInput,
      outputPath: path.join(scratch, 'evidence'),
      env: { PATH: process.env.PATH, AGENTWEAVER_CLI_PATH: cliPath, AGENTWEAVER_RUN_ID: 'stale-run' },
    });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
}

function fakeApphost(input, options = {}) {
  const calls = [];
  const jobId = randomUUID();
  const envelope = completedEnvelope(input, jobId);
  const result = resultFor(input, envelope);
  const storedTurn = {
    projectId: input.target.projectId,
    runId: input.target.runId,
    sessionId: input.target.sessionId,
    eventId: result.nativeCompletion.assistantMaterial.eventId,
    contentSha256: hash,
  };
  options.changeReceipt?.(result.nativeCompletion);
  let statusReads = 0;
  let submits = 0;
  let cancelCount = 0;
  let cleanupStatusReads = 0;
  const runner = (_cliPath, args, env, timeoutMs) => {
    calls.push({ args, env, timeoutMs });
    const operation = args[0] === 'api' && args[1] === 'invoke' ? args[2] : null;
    if (args[0] === '--version') return { ...ok({}), stdout: '1.1.0\n' };
    if (args[0] === '--help') {
      return { ...ok({}), stdout: options.noDigestFlag
        ? 'agentweaver api invoke readSessionTurnContent --turn-content-sha256-extra\n'
        : '  agentweaver api invoke readSessionTurnContent --turn-content-sha256\n' };
    }
    if (args[0] === 'api' && args[1] === 'operations') {
      return plain([
        'getAuthorizationContext', 'getRunSelection', 'generateProjectWorkflow',
        'readProjectWorkflowGenerationJob', 'readProjectWorkflowGenerationResult',
        'cancelProjectRun', 'readRunStatus',
      ].map(operationId => ({ operationId })).concat(options.noTurnRoute ? [] : [turnOperation]));
    }
    if (args[0] === 'api' && args[1] === 'describe' && args[2] === 'readSessionTurnContent') {
      return plain({
        ...turnOperation,
        parameters: ['projectId', 'runId', 'sessionId', 'eventId']
          .map(name => ({
            name, in: 'path', required: true,
            schema: name === 'eventId'
              ? options.eventIdSchema ?? { type: 'string', format: 'uuid' }
              : { type: 'string' },
          })),
        responses: { 200: { content: { 'application/json': {
          schema: { $ref: options.wrongTurnSchema ? '#/components/schemas/Other' : '#/components/schemas/SessionTurnContent' },
        } } } },
      });
    }
    if (args[0] === 'auth' && args[1] === 'status') {
      const writer = args.includes('projects.admin');
      return plain({
        isSignedIn: true,
        tenantId: input.target.tenantId,
        projectId: writer ? null : input.target.projectId,
        runId: writer ? null : input.target.runId,
      });
    }
    switch (operation) {
      case 'getAuthorizationContext':
        if (args.includes('projects.admin')) {
          return ok(options.adminContext ?? {
            actorId: input.expectedActorId,
            tenantId: input.target.tenantId,
            boundProjectId: null,
            boundRunId: null,
          });
        }
        return ok(options.runContext ?? {
          actorId: input.expectedActorId,
          tenantId: input.target.tenantId,
          boundProjectId: input.target.projectId,
          boundRunId: input.target.runId,
        });
      case 'getRunSelection':
        return ok(options.selection ?? acceptedSelection(input));
      case 'generateProjectWorkflow':
        return options.submitResponses?.[submits++] ?? ok(
          options.pendingFirst ? { ...envelope, status: 'queued', artifact: null } : envelope,
          202,
        );
      case 'readProjectWorkflowGenerationJob':
        statusReads += 1;
        if (options.generationFailed) {
          return ok({ ...envelope, status: 'failed', artifact: null, failure: { code: 'workflow_generation_failed' } });
        }
        return ok(statusReads === 1 && options.pendingFirst
          ? { ...envelope, status: 'running', artifact: null }
          : envelope);
      case 'readProjectWorkflowGenerationResult':
        return ok(result);
      case 'readSessionTurnContent':
        return { ...plain({ ...storedTurn, ...options.turnFields }), ...options.turnFailure };
      case 'cancelProjectRun':
        cancelCount += 1;
        if (options.onCancel) return options.onCancel(cancelCount, timeoutMs);
        return options.cleanupFailed ? fail() : ok({ runId: input.target.runId, state: 'cancelled', cancelledAt: '2026-11-17T00:00:02Z' });
      case 'readRunStatus':
        cleanupStatusReads += 1;
        if (options.onRunStatus) return options.onRunStatus(cleanupStatusReads, timeoutMs);
        return ok({
          projectId: input.target.projectId,
          runId: input.target.runId,
          rootSessionId: input.target.sessionId,
          executionFence: 1,
          logicalTurnOrdinal: 0,
          executionState: options.cleanupFailed ? 'running' : 'cancelled',
          stateVersion: 2,
          runtimeCleanup: options.cleanupFailed
            ? { state: 'pending', total: 1, pending: 1, complete: 0 }
            : { state: 'complete', total: 1, pending: 0, complete: 1 },
        });
      default:
        throw new Error(`Unexpected operation: ${args.join(' ')}`);
    }
  };
  return { runner, calls, jobId, storedTurn };
}

async function runFake(context, options = {}, runOptions = {}) {
  let currentTime = runOptions.startTime ?? 1_000_000;
  const sleepCalls = [];
  const clockOptions = {
    now: () => currentTime,
    sleep: async ms => {
      sleepCalls.push(ms);
      currentTime += ms;
    },
    ...runOptions,
  };
  const apphost = fakeApphost(context.caseInput, {
    ...options,
    advanceClock: delta => { currentTime += delta; },
  });
  const exitCode = await runCase(
    { inputPath: context.casePath, outputPath: context.outputPath, dryRun: false },
    context.env,
    {
      runCommand: (cliPath, args, env, timeoutMs) => {
        const res = apphost.runner(cliPath, args, env, timeoutMs);
        if (options.onAfterCommand) {
          options.onAfterCommand(args, timeoutMs, delta => { currentTime += delta; });
        }
        return res;
      },
      jobDeadlineMs: 100,
      pollIntervalMs: 0,
      cleanupDeadlineMs: 90_000,
      cleanupPollIntervalMs: 1_000,
      ...clockOptions,
    },
  );
  const resultText = await readFile(path.join(context.outputPath, 'result.json'), 'utf8');
  const commands = await readFile(path.join(context.outputPath, 'commands.jsonl'), 'utf8');
  return { exitCode, result: JSON.parse(resultText), resultText, commands, sleepCalls, ...apphost };
}

test('case input requires approval, a bound run, a selected model, and finite run limits', () => {
  const parsed = validateInput(example);
  assert.equal(parsed.caseId, 'workflow-generation');
  assert.throws(() => validateInput({ ...example, approvedDisposableRun: false }), /schema/);
  assert.throws(() => validateInput({ ...example, bearerToken: 'do-not-log-this' }), /schema/);
  assert.throws(() => validateInput({
    ...example,
    expectedSelection: {
      ...example.expectedSelection,
      runLimits: { ...example.expectedSelection.runLimits, maxWallTimeSeconds: 0 },
    },
  }), /limits/);
  assert.throws(() => validateInput({
    ...example,
    target: { ...example.target, gatewayUrl: 'https://user:pass@gateway.example.invalid' },
  }), /Gateway URL/);
});

test('commands use run-bound API invoke and separate unbound project-writer authority', () => {
  const input = validateInput(example);
  assert.deepEqual(buildCommands(input).map(command => command.id), [
    'version',
    'help',
    'api-operations',
    'api-describe-turn-digest',
    'auth-status-run',
    'auth-status-project-writer',
    'api-invoke-authorization-context',
    'api-invoke-admin-authorization-context',
    'api-invoke-run-selection',
  ]);
  assert.equal(buildGenerateCommand(input, 'key').args[2], 'generateProjectWorkflow');
  assert.equal(buildJobStatusCommand(input, randomUUID()).args[2], 'readProjectWorkflowGenerationJob');
  assert.equal(buildJobResultCommand(input, randomUUID()).args[2], 'readProjectWorkflowGenerationResult');
  const [cancel, verify] = buildCleanupCommands(input, 'cleanup-key');
  assert.equal(cancel.args[2], 'cancelProjectRun');
  assert.ok(cancel.args.includes('projects.admin'));
  assert.ok(!cancel.args.includes('--project-id'));
  assert.ok(!cancel.args.includes('--run-id'));
  assert.equal(verify.args[2], 'readRunStatus');
  assert.ok(verify.args.includes(input.target.projectId));
  assert.ok(verify.args.includes(input.target.runId));
});

test('target and accepted selection checks reject a different actor, run, model, or budget', () => {
  const input = validateInput(example);
  assert.throws(() => validateCallerContext({ actorId: 'other-actor' }, input), /caller context/);
  assert.throws(() => validateAcceptedSelection({
    ...acceptedSelection(input),
    runLimits: { ...input.expectedSelection.runLimits, maxModelTurns: 2 },
  }, input), /run limits/);
  assert.doesNotThrow(() => validateAcceptedSelection(acceptedSelection(input), input));
});

test('native result hash, artifact ID, selection, and completion evidence must agree', () => {
  const input = validateInput(example);
  const jobId = randomUUID();
  const envelope = completedEnvelope(input, jobId);
  const result = resultFor(input, envelope);
  assert.doesNotThrow(() => validateGenerationResult(result, envelope, input, acceptedSelection(input)));
  assert.throws(() => validateGenerationResult({
    ...result,
    definitionSha256: '0'.repeat(64),
  }, envelope, input, acceptedSelection(input)), /definition hash/);
  assert.throws(() => validateGenerationResult({
    ...result,
    nativeCompletion: { ...result.nativeCompletion, nativeCompletionReceiptEventId: null },
  }, envelope, input, acceptedSelection(input)), /native completion evidence/);
  assert.throws(() => validateJobEnvelope({
    ...envelope,
    statusUrl: envelope.statusUrl.replace('/api/v1/', '/api/'),
  }, input), /public Gateway route/);
});

test('native result hash matches System.Text.Json quote and surrogate escaping', () => {
  const input = validateInput(example);
  const jobId = randomUUID();
  const envelope = completedEnvelope(input, jobId);
  envelope.definition = {
    origin: 'generated',
    id: 'w',
    revision: '1',
    steps: [{ purpose: 'Review "plan" 🚀' }],
  };
  envelope.artifact.workflowId = envelope.definition.id;
  envelope.artifact.definitionRevision = envelope.definition.revision;

  const result = resultFor(
    input,
    envelope,
    '65903a91dea175a26af42cac0cc0989a878731d9ee20cd6668233ce4940a1ecc',
  );

  assert.doesNotThrow(() => validateGenerationResult(result, envelope, input, acceptedSelection(input)));
});

test('case invokes all workflow operations, validates completion, and cancels the disposable run', async () => {
  await withScratch(async context => {
    const { exitCode, result, commands, calls, storedTurn } = await runFake(context, { pendingFirst: true });
    assert.equal(exitCode, 0);
    assert.equal(result.status, 'passed');
    assert.match(result.scope, /verified disposable-run cancellation/);
    assert.equal(result.generation.status, 'passed');
    assert.deepEqual(result.generation.storedTurn, storedTurn);
    assert.equal(result.cleanup.status, 'passed');
    assert.match(commands, /"stepId":"api-invoke-generate-workflow"/);
    assert.match(commands, /"api","invoke","readProjectWorkflowGenerationJob"/);
    assert.match(commands, /"api","invoke","readProjectWorkflowGenerationResult"/);
    assert.doesNotMatch(commands, /Generate one workflow/);
    assert.equal(calls.filter(call => call.args[0] === 'api' && call.args[1] === 'invoke'
      && call.args[2] === 'readProjectWorkflowGenerationJob').length, 2);
    const submits = calls.filter(call => call.args[2] === 'generateProjectWorkflow');
    assert.equal(submits.length, 1);
    const [post] = submits;
    assert.ok(post.args.includes('--project-id'));
    assert.ok(post.args.includes('--run-id'));
    const turn = calls.find(call => call.args[1] === 'invoke' && call.args[2] === 'readSessionTurnContent');
    assert.deepEqual(turn.args.slice(-8), [
      '--path', `projectId=${storedTurn.projectId}`, '--path', `runId=${storedTurn.runId}`,
      '--path', `sessionId=${storedTurn.sessionId}`, '--path', `eventId=${storedTurn.eventId}`,
    ]);
    assert.deepEqual(turn.args.slice(2, 4), ['readSessionTurnContent', '--turn-content-sha256']);
    assert.ok(turn.args.includes('projects.orchestrator'));
    for (const [flag, id] of [['--project-id', storedTurn.projectId], ['--run-id', storedTurn.runId]]) {
      const index = turn.args.indexOf(flag);
      assert.notEqual(index, -1);
      assert.equal(turn.args[index + 1], id);
    }
    const cancel = calls.find(call => call.args[2] === 'cancelProjectRun');
    assert.ok(cancel.args.includes('projects.admin'));
    assert.ok(!cancel.args.includes('--project-id'));
    assert.deepEqual(childEnvironment({
      PATH: 'system-path',
      AGENTWEAVER_PROJECT_ID: 'stale-project',
      AGENTWEAVER_RUN_ID: 'stale-run',
      AGENTWEAVER_CLI_PATH: 'stale-apphost',
    }), { PATH: 'system-path' });
  });
});

function assertSafeSubmitEvidence(commands, submitCalls, expectedCodes) {
  const attempts = commands.trim().split('\n').map(line => JSON.parse(line))
    .filter(record => record.stepId === 'api-invoke-generate-workflow');
  assert.deepEqual(attempts.map(record => [record.attempt, record.code]),
    expectedCodes.map((code, index) => [index + 1, code]));
  for (const attempt of attempts) {
    for (const field of ['stdout', 'stderr', 'arguments', 'error']) {
      assert.equal(Object.hasOwn(attempt, field), false, field);
    }
  }
  for (const { args } of submitCalls) {
    const key = args.find(value => value.startsWith('Idempotency-Key=')).split('=')[1];
    assert.equal(commands.includes(key), false);
  }
  assert.equal(commands.includes(failedSubmitStdout), false);
  assert.equal(commands.includes(failedSubmitStderr), false);
}

test('a full queue retries the exact submit once and polls and reads the accepted job', async () => {
  await withScratch(async context => {
    const { exitCode, result, commands, calls, jobId } = await runFake(context, {
      submitResponses: [queueFull()],
      pendingFirst: true,
    });
    assert.equal(exitCode, 0);
    assert.equal(result.generation.jobId, jobId);
    assert.equal(result.cleanup.status, 'passed');
    const submits = calls.filter(call => call.args[2] === 'generateProjectWorkflow');
    assert.equal(submits.length, 2);
    assert.deepEqual(submits[1].args, submits[0].args);
    assert.deepEqual(calls.slice(-6, -3).map(call => call.args), [
      buildJobStatusCommand(context.caseInput, jobId).args,
      buildJobStatusCommand(context.caseInput, jobId).args,
      buildJobResultCommand(context.caseInput, jobId).args,
    ]);
    assert.deepEqual(calls.slice(-3).map(call => call.args[2]), ['readSessionTurnContent', 'cancelProjectRun', 'readRunStatus']);
    assertSafeSubmitEvidence(commands, submits, [queueFullCode, undefined]);
  });
});

for (const [name, submitResponses, expectedCodes] of [
  ['a second full queue', [queueFull(), queueFull()], [queueFullCode, queueFullCode]],
  ['a different 503 error', [queueFull('service_unavailable')], [undefined]],
  ['a different HTTP status', [{ ...queueFull(), stdout: queueFull().stdout.replace('"status":503', '"status":409') }], [undefined]],
  ['a timeout with partial output', [{ ...queueFull(), stdout: queueFull().stdout.slice(0, -1), timedOut: true }], [undefined]],
  ['a timeout with a complete-looking response', [{ ...queueFull(), timedOut: true }], [undefined]],
  ['malformed output', [{ ...queueFull(), stdout: queueFull().stdout.slice(0, -1) }], [undefined]],
  ['a process error', [{ ...queueFull(), error: failedSubmitStderr }], [undefined]],
  ['a process signal', [{ ...queueFull(), signal: 'SIGTERM' }], [undefined]],
  ['an unexpected exit code', [{ ...queueFull(), exitCode: 0 }], [undefined]],
]) {
  test(`${name} stops generation without polling and still verifies cancellation`, async () => {
    await withScratch(async context => {
      const { exitCode, result, commands, calls } = await runFake(context, { submitResponses });
      assert.equal(exitCode, 1);
      assert.equal(result.generation.status, 'failed');
      assert.equal(result.generation.jobId, null);
      assert.equal(result.cleanup.status, 'passed');
      assert.deepEqual(calls.slice(buildCommands(context.caseInput).length).map(call => call.args[2]), [
        ...submitResponses.map(() => 'generateProjectWorkflow'), 'cancelProjectRun', 'readRunStatus',
      ]);
      const submits = calls.filter(call => call.args[2] === 'generateProjectWorkflow');
      assertSafeSubmitEvidence(commands, submits, expectedCodes);
    });
  });
}

test('missing digest support blocks before submission or cancellation', async t => {
  for (const [name, options] of Object.entries({
    'missing flag': { noDigestFlag: true },
    'missing route': { noTurnRoute: true },
    'wrong response schema': { wrongTurnSchema: true },
    'integer event ID': { eventIdSchema: { type: 'integer' } },
    'event ID without UUID format': { eventIdSchema: { type: 'string' } },
  })) {
    await t.test(name, () => withScratch(async context => {
      const { exitCode, result, calls } = await runFake(context, options);
      assert.equal(exitCode, 2);
      assert.equal(result.generation.status, 'blocked');
      assert.equal(result.cleanup.status, 'not-attempted');
      assert.ok(!calls.some(call => call.args[1] === 'invoke'));
    }));
  }
});

test('bad stored reply evidence fails without resubmission and verifies cancellation', async t => {
  const stdoutText = 'private-reply-from-stdout';
  const stderrText = 'private-reply-from-stderr';
  for (const [name, options] of Object.entries({
    'wrong project': { turnFields: { projectId: 'wrong-project' } },
    'wrong run': { turnFields: { runId: 'wrong-run' } },
    'wrong session': { turnFields: { sessionId: 'wrong-session' } },
    'wrong event': { turnFields: { eventId: randomUUID() } },
    'extra reply text': { turnFields: { text: stdoutText } },
    'material hash disagrees': { changeReceipt: receipt => { receipt.assistantMaterial.contentSha256 = 'b'.repeat(64); } },
    'completion hash disagrees': { changeReceipt: receipt => { receipt.outputSha256 = 'b'.repeat(64); } },
    'read fails': { turnFailure: fail() },
    'read exits nonzero with a matching digest': { turnFailure: { exitCode: 1 } },
    'read times out with reply text in both streams': {
      turnFailure: { ...fail(), exitCode: null, timedOut: true, stdout: stdoutText, stderr: stderrText, error: stderrText },
    },
  })) {
    await t.test(name, () => withScratch(async context => {
      const { exitCode, result, resultText, commands, calls } = await runFake(context, options);
      assert.equal(exitCode, 1);
      assert.equal(result.generation.status, 'failed');
      assert.equal(result.generation.storedTurn, null);
      assert.equal(result.cleanup.status, 'passed');
      assert.deepEqual(calls.slice(buildCommands(context.caseInput).length).map(call => call.args[2]), [
        'generateProjectWorkflow', 'readProjectWorkflowGenerationJob', 'readProjectWorkflowGenerationResult',
        'readSessionTurnContent', 'cancelProjectRun', 'readRunStatus',
      ]);
      const attempt = commands.trim().split('\n').map(line => JSON.parse(line))
        .find(record => record.stepId === 'api-invoke-turn-digest');
      assert.equal(attempt.timedOut, options.turnFailure?.timedOut ?? false);
      for (const field of ['stdout', 'stderr', 'arguments', 'error']) {
        assert.equal(Object.hasOwn(attempt, field), false, field);
      }
      for (const evidence of [commands, resultText]) {
        assert.ok(!evidence.includes(stdoutText));
        assert.ok(!evidence.includes(stderrText));
      }
    }));
  }
});

test('model or budget mismatch blocks generation but still cleans up the verified disposable run', async () => {
  await withScratch(async context => {
    const selection = acceptedSelection(context.caseInput);
    selection.runLimits.maxModelTurns = 2;
    const { exitCode, result, calls } = await runFake(context, { selection });
    assert.equal(exitCode, 2);
    assert.equal(result.status, 'blocked');
    assert.equal(result.generation.status, 'blocked');
    assert.equal(result.cleanup.status, 'passed');
    assert.equal(calls.some(call => call.args[2] === 'generateProjectWorkflow'), false);
    assert.equal(calls.some(call => call.args[2] === 'cancelProjectRun'), true);
  });
});

test('generation and cleanup failures are recorded separately', async () => {
  await withScratch(async context => {
    const { exitCode, result } = await runFake(context, { generationFailed: true, cleanupFailed: true });
    assert.equal(exitCode, 1);
    assert.equal(result.generation.status, 'failed');
    assert.match(result.generation.failure, /workflow_generation_failed/);
    assert.equal(result.cleanup.status, 'failed');
    assert.match(result.cleanup.failure, /pending|cancelled state/);
  });
});

test('native apphost validation and argument parsing reject unsafe execution choices', () => {
  const expectedName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  assert.throws(() => resolveCliPath(expectedName), /absolute path/);
  assert.throws(() => resolveCliPath(path.resolve(directory, 'node.exe')), /native/);
  assert.equal(path.basename(resolveCliPath(path.resolve(directory, expectedName))), expectedName);
  assert.throws(() => parseArgs(['--input', 'case.json', '--output', 'evidence', '--token', 'secret']), /Use --input/);
  assert.throws(() => parseArgs(['--input', 'case.json', '--input', 'other.json', '--output', 'evidence']), /Use --input/);
});

test('dry-run validates the apphost and input without a command or evidence write', async () => {
  await withScratch(async context => {
    const code = await runCase(
      { inputPath: context.casePath, outputPath: context.outputPath, dryRun: true },
      context.env,
      { runCommand: () => { throw new Error('dry-run must not invoke the apphost'); } },
    );
    assert.equal(code, 0);
    await assert.rejects(access(context.outputPath), { code: 'ENOENT' });
  });
});

test('standard cleanup polls pending runtimeCleanup until complete and stores validated summary only', async () => {
  await withScratch(async context => {
    const sentinelRef = 'sentinel-runtime-ref-12345';
    const sentinelStdout = 'sentinel-cancel-stdout-67890';
    const sentinelStderr = 'sentinel-status-stderr-abcde';
    const { exitCode, result, resultText, commands, calls } = await runFake(context, {
      onCancel: () => ({
        ...ok({ runId: context.caseInput.target.runId, state: 'cancelled', note: sentinelStdout }),
        stderr: sentinelStderr,
      }),
      onRunStatus: readCount => ok({
        projectId: context.caseInput.target.projectId,
        runId: context.caseInput.target.runId,
        rootSessionId: context.caseInput.target.sessionId,
        executionFence: 2,
        logicalTurnOrdinal: 0,
        executionState: 'cancelled',
        stateVersion: 2 + readCount,
        reference: sentinelRef,
        runtimeCleanup: readCount === 1
          ? { state: 'pending', total: 1, pending: 1, complete: 0 }
          : { state: 'complete', total: 1, pending: 0, complete: 1, reference: sentinelRef },
      }),
    });
    assert.equal(exitCode, 0);
    assert.equal(result.status, 'passed');
    assert.equal(result.generation.status, 'passed');
    assert.equal(result.cleanup.status, 'passed');
    assert.deepEqual(result.cleanup.runtimeCleanup, { state: 'complete', total: 1, pending: 0, complete: 1 });
    assert.equal(calls.filter(call => call.args[2] === 'cancelProjectRun').length, 1);
    assert.equal(calls.filter(call => call.args[2] === 'readRunStatus').length, 2);

    for (const sentinel of [sentinelRef, sentinelStdout, sentinelStderr]) {
      assert.equal(commands.includes(sentinel), false);
      assert.equal(resultText.includes(sentinel), false);
    }
    const logRecords = commands.trim().split('\n').map(line => JSON.parse(line));
    for (const stepId of ['api-invoke-cancel-disposable-run', 'api-invoke-verify-run-cleanup']) {
      const matching = logRecords.filter(record => record.stepId === stepId);
      assert.ok(matching.length >= 1, stepId);
      for (const entry of matching) {
        for (const forbidden of ['arguments', 'stdout', 'stderr', 'error']) {
          assert.equal(Object.hasOwn(entry, forbidden), false, `${stepId}:${forbidden}`);
        }
      }
    }
  });
});

test('standard cleanup rejects cancellation alone, missing/zero summary, and count mismatches', async t => {
  for (const [name, runtimeCleanup, expectedFailure] of [
    ['absent summary at deadline', undefined, /absent/],
    ['null summary at deadline', null, /absent/],
    ['zero total', { state: 'complete', total: 0, pending: 0, complete: 0 }, /malformed/],
    ['complete with nonzero pending', { state: 'complete', total: 2, pending: 1, complete: 1 }, /malformed/],
    ['complete count mismatch', { state: 'complete', total: 2, pending: 0, complete: 1 }, /malformed/],
    ['pending with zero pending', { state: 'pending', total: 1, pending: 0, complete: 1 }, /malformed/],
  ]) {
    await t.test(name, () => withScratch(async context => {
      const { exitCode, result } = await runFake(context, {
        onRunStatus: () => ok({
          projectId: context.caseInput.target.projectId,
          runId: context.caseInput.target.runId,
          rootSessionId: context.caseInput.target.sessionId,
          executionFence: 1,
          logicalTurnOrdinal: 0,
          executionState: 'cancelled',
          stateVersion: 2,
          runtimeCleanup,
        }),
      }, { cleanupDeadlineMs: 2_500, cleanupPollIntervalMs: 1_000 });
      assert.equal(exitCode, 1);
      assert.equal(result.generation.status, 'passed');
      assert.equal(result.cleanup.status, 'failed');
      assert.equal(result.cleanup.runtimeCleanup, undefined);
      assert.match(result.cleanup.failure, expectedFailure);
    }));
  }
});

test('standard caller context gates block both generation and cleanup on mismatch', async t => {
  for (const [name, overrides] of Object.entries({
    'wrong admin actor': {
      adminContext: input => ({
        actorId: 'other-admin',
        tenantId: input.target.tenantId,
        boundProjectId: null,
        boundRunId: null,
      }),
    },
    'wrong admin tenant': {
      adminContext: input => ({
        actorId: input.expectedActorId,
        tenantId: 'other-tenant',
        boundProjectId: null,
        boundRunId: null,
      }),
    },
    'bound admin context': {
      adminContext: input => ({
        actorId: input.expectedActorId,
        tenantId: input.target.tenantId,
        boundProjectId: input.target.projectId,
        boundRunId: null,
      }),
    },
    'mismatched run context': {
      runContext: input => ({
        actorId: input.expectedActorId,
        tenantId: input.target.tenantId,
        boundProjectId: input.target.projectId,
        boundRunId: 'other-run',
      }),
    },
  })) {
    await t.test(name, () => withScratch(async context => {
      const { exitCode, result, calls } = await runFake(context, {
        adminContext: overrides.adminContext?.(context.caseInput),
        runContext: overrides.runContext?.(context.caseInput),
      });
      assert.equal(exitCode, 2);
      assert.equal(result.generation.status, 'blocked');
      assert.equal(result.cleanup.status, 'not-attempted');
      assert.equal(calls.some(call => call.args[2] === 'generateProjectWorkflow'), false);
      assert.equal(calls.some(call => call.args[2] === 'cancelProjectRun'), false);
      assert.equal(calls.some(call => call.args[2] === 'readRunStatus'), false);
    }));
  }
});

test('standard cleanup stops on foreign target, denial, or incompatible terminal state', async t => {
  for (const [name, responseFn] of [
    ['foreign project', input => ok({
      projectId: 'other-project',
      runId: input.target.runId,
      rootSessionId: input.target.sessionId,
      executionState: 'cancelled',
      runtimeCleanup: { state: 'complete', total: 1, pending: 0, complete: 1 },
    })],
    ['foreign run', input => ok({
      projectId: input.target.projectId,
      runId: 'other-run',
      rootSessionId: input.target.sessionId,
      executionState: 'cancelled',
      runtimeCleanup: { state: 'complete', total: 1, pending: 0, complete: 1 },
    })],
    ['foreign root session', input => ok({
      projectId: input.target.projectId,
      runId: input.target.runId,
      rootSessionId: 'other-session',
      executionState: 'cancelled',
      runtimeCleanup: { state: 'complete', total: 1, pending: 0, complete: 1 },
    })],
    ['status denial 403', () => ok({ error: 'forbidden' }, 403)],
    ['incompatible terminal state completed', input => ok({
      projectId: input.target.projectId,
      runId: input.target.runId,
      rootSessionId: input.target.sessionId,
      executionState: 'completed',
    })],
  ]) {
    await t.test(name, () => withScratch(async context => {
      const { exitCode, result, calls } = await runFake(context, {
        onRunStatus: () => responseFn(context.caseInput),
      });
      assert.equal(exitCode, 1);
      assert.equal(result.generation.status, 'passed');
      assert.equal(result.cleanup.status, 'failed');
      assert.equal(result.cleanup.runtimeCleanup, undefined);
      assert.equal(calls.filter(call => call.args[2] === 'cancelProjectRun').length, 1);
      assert.equal(calls.filter(call => call.args[2] === 'readRunStatus').length, 1);
    }));
  }
});

test('standard cleanup stops on permanent CLI failure without a second read even if next response would be valid', async t => {
  for (const [name, badRead] of [
    ['empty stdout with stderr', () => ({ exitCode: 1, signal: null, stdout: '', stderr: 'invalid OpenAPI document', timedOut: false, error: null })],
    ['invalid JSON stdout with stderr', () => ({ exitCode: 1, signal: null, stdout: 'not-json', stderr: 'discovery failed', timedOut: false, error: null })],
    ['process error', () => ({ exitCode: null, signal: null, stdout: '', stderr: '', timedOut: false, error: 'ENOENT' })],
  ]) {
    await t.test(name, () => withScratch(async context => {
      const { exitCode, result, calls } = await runFake(context, {
        onRunStatus: readCount => readCount === 1
          ? badRead()
          : ok({
              projectId: context.caseInput.target.projectId,
              runId: context.caseInput.target.runId,
              rootSessionId: context.caseInput.target.sessionId,
              executionState: 'cancelled',
              runtimeCleanup: { state: 'complete', total: 1, pending: 0, complete: 1 },
            }),
      });
      assert.equal(exitCode, 1);
      assert.equal(result.generation.status, 'passed');
      assert.equal(result.cleanup.status, 'failed');
      assert.equal(result.cleanup.runtimeCleanup, undefined);
      assert.equal(calls.filter(call => call.args[2] === 'cancelProjectRun').length, 1);
      assert.equal(calls.filter(call => call.args[2] === 'readRunStatus').length, 1);
    }));
  }
});

test('standard cleanup retries CLI gateway-request-timeout diagnostic then passes on valid proof', async () => {
  await withScratch(async context => {
    const { exitCode, result, calls } = await runFake(context, {
      onRunStatus: readCount => readCount === 1
        ? {
            exitCode: 1,
            signal: null,
            stdout: '',
            stderr: 'The Gateway request timed out.\n',
            timedOut: false,
            error: null,
          }
        : ok({
            projectId: context.caseInput.target.projectId,
            runId: context.caseInput.target.runId,
            rootSessionId: context.caseInput.target.sessionId,
            executionState: 'cancelled',
            runtimeCleanup: { state: 'complete', total: 1, pending: 0, complete: 1 },
          }),
    });
    assert.equal(exitCode, 0);
    assert.equal(result.generation.status, 'passed');
    assert.equal(result.cleanup.status, 'passed');
    assert.deepEqual(result.cleanup.runtimeCleanup, { state: 'complete', total: 1, pending: 0, complete: 1 });
    assert.equal(calls.filter(call => call.args[2] === 'cancelProjectRun').length, 1);
    assert.equal(calls.filter(call => call.args[2] === 'readRunStatus').length, 2);
  });
});

test('standard cleanup caps sleep to remaining budget (<1000ms) after pending read and starts no further read', async () => {
  await withScratch(async context => {
    const readTimeouts = [];
    const { exitCode, result, sleepCalls, calls } = await runFake(context, {
      onRunStatus: (_readCount, timeoutMs) => {
        readTimeouts.push(timeoutMs);
        return ok({
          projectId: context.caseInput.target.projectId,
          runId: context.caseInput.target.runId,
          rootSessionId: context.caseInput.target.sessionId,
          executionState: 'cancelled',
          runtimeCleanup: { state: 'pending', total: 1, pending: 1, complete: 0 },
        });
      },
      onAfterCommand: (args, _timeoutMs, advanceClock) => {
        if (args[0] === 'api' && args[1] === 'invoke' && args[2] === 'readRunStatus') {
          advanceClock(800);
        }
      },
    }, { cleanupDeadlineMs: 1_500, cleanupPollIntervalMs: 1_000 });
    assert.equal(exitCode, 1);
    assert.deepEqual(readTimeouts, [1_500]);
    assert.deepEqual(sleepCalls, [700]);
    assert.equal(calls.filter(call => call.args[2] === 'readRunStatus').length, 1);
    assert.equal(result.generation.status, 'passed');
    assert.equal(result.cleanup.status, 'failed');
    assert.equal(result.cleanup.runtimeCleanup, undefined);
  });
});

test('standard cleanup caps remaining budget for cancel, read, and sleep and rejects post-read deadline', async () => {
  await withScratch(async context => {
    const cancelTimeouts = [];
    const readTimeouts = [];
    const { exitCode, result, sleepCalls } = await runFake(context, {
      onCancel: (_count, timeoutMs) => {
        cancelTimeouts.push(timeoutMs);
        return ok({ runId: context.caseInput.target.runId, state: 'cancelled' });
      },
      onRunStatus: (readCount, timeoutMs) => {
        readTimeouts.push(timeoutMs);
        return ok({
          projectId: context.caseInput.target.projectId,
          runId: context.caseInput.target.runId,
          rootSessionId: context.caseInput.target.sessionId,
          executionState: 'cancelled',
          runtimeCleanup: readCount === 1
            ? { state: 'pending', total: 1, pending: 1, complete: 0 }
            : { state: 'complete', total: 1, pending: 0, complete: 1 },
        });
      },
      onAfterCommand: (args, _timeoutMs, advanceClock) => {
        const op = args[0] === 'api' && args[1] === 'invoke' ? args[2] : null;
        if (op === 'cancelProjectRun') advanceClock(500);
        if (op === 'readRunStatus' && readTimeouts.length === 1) advanceClock(200);
        if (op === 'readRunStatus' && readTimeouts.length === 2) advanceClock(800);
      },
    }, { cleanupDeadlineMs: 2_500, cleanupPollIntervalMs: 1_000 });
    assert.equal(exitCode, 1);
    assert.deepEqual(cancelTimeouts, [2_500]);
    assert.deepEqual(readTimeouts, [2_000, 800]);
    assert.deepEqual(sleepCalls, [1_000]);
    assert.equal(result.generation.status, 'passed');
    assert.equal(result.cleanup.status, 'failed');
    assert.equal(result.cleanup.runtimeCleanup, undefined);
  });
});

test('standard cleanup permits exact readback with one cancel on 504 owner_timeout or lost cancel', async t => {
  for (const [name, cancelResponse] of [
    ['504 owner_timeout', () => ok({ code: 'owner_timeout', detail: 'Gateway timed out waiting for owner.' }, 504)],
    ['transport-lost cancel', () => ({ exitCode: null, signal: null, stdout: '', stderr: '', timedOut: true, error: 'ETIMEDOUT' })],
  ]) {
    await t.test(name, () => withScratch(async context => {
      const { exitCode, result, calls } = await runFake(context, {
        onCancel: cancelResponse,
      });
      assert.equal(exitCode, 0);
      assert.equal(result.cleanup.status, 'passed');
      assert.deepEqual(result.cleanup.runtimeCleanup, { state: 'complete', total: 1, pending: 0, complete: 1 });
      assert.equal(calls.filter(call => call.args[2] === 'cancelProjectRun').length, 1);
      assert.equal(calls.filter(call => call.args[2] === 'readRunStatus').length, 1);
    }));
  }
});

test('standard cleanup fails without polling or second cancel on other 504, denial, conflict, or queue_full', async t => {
  for (const [name, cancelResponse] of [
    ['other 504', () => ok({ code: 'upstream_gateway_timeout' }, 504)],
    ['denial 403', () => ok({ error: 'forbidden' }, 403)],
    ['conflict 409', () => ok({ error: 'conflict' }, 409)],
    ['queue_full 503', () => queueFull('runtime_start_queue_full')],
  ]) {
    await t.test(name, () => withScratch(async context => {
      const { exitCode, result, calls } = await runFake(context, {
        onCancel: cancelResponse,
      });
      assert.equal(exitCode, 1);
      assert.equal(result.generation.status, 'passed');
      assert.equal(result.cleanup.status, 'failed');
      assert.equal(calls.filter(call => call.args[2] === 'cancelProjectRun').length, 1);
      assert.equal(calls.filter(call => call.args[2] === 'readRunStatus').length, 0);
    }));
  }
});

const creditZeroExamplePath = path.resolve(directory, '..', 'examples', 'workflow-generation-credit-zero.json');
const creditZeroExample = JSON.parse(await readFile(creditZeroExamplePath, 'utf8'));

async function withCreditZeroScratch(callback) {
  const scratch = path.join(directory, `credit-zero-case-${randomUUID()}`);
  await mkdir(scratch);
  try {
    const cliPath = path.join(scratch, apphostName);
    await writeFile(cliPath, 'controlled native apphost');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    const caseInput = validateInput({ ...creditZeroExample, apphostSha256 });
    const casePath = path.join(scratch, 'case.json');
    await writeFile(casePath, JSON.stringify({ ...creditZeroExample, apphostSha256 }));
    await callback({
      scratch,
      cliPath,
      casePath,
      caseInput,
      outputPath: path.join(scratch, 'evidence'),
      env: { PATH: process.env.PATH, AGENTWEAVER_CLI_PATH: cliPath, AGENTWEAVER_RUN_ID: 'stale-run' },
    });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
}

function creditZeroOperations() {
  return [
    'getAuthorizationContext',
    'getRunSelection',
    'readSessionStatus',
    'readDecisions',
    'generateProjectWorkflow',
    'readProjectWorkflowGenerationJob',
    'cancelProjectRun',
    'readRunStatus',
  ].map(operationId => ({ operationId }));
}

function creditZeroSelection(input, overrides = {}) {
  return {
    projectId: input.target.projectId,
    runId: input.target.runId,
    projectConfigurationRevision: input.expectedSelection.projectConfigurationRevision,
    modelSelection: {
      reference: input.expectedSelection.modelSelectionReference,
      sourceMode: 'hostedCopilot',
      ...overrides.modelSelection,
    },
    runLimits: {
      ...input.expectedSelection.runLimits,
      ...overrides.runLimits,
    },
  };
}

function creditZeroSessionStatus(input, overrides = {}) {
  const runtimeInstanceId = overrides.runtimeInstanceId ?? randomUUID();
  return {
    identity: {
      projectId: input.target.projectId,
      runId: input.target.runId,
      sessionId: input.target.sessionId,
      ...overrides.identity,
    },
    parentSessionId: overrides.parentSessionId ?? null,
    rootSessionId: overrides.rootSessionId ?? input.target.sessionId,
    kind: overrides.kind ?? 'coordinator',
    lifecycle: overrides.lifecycle ?? 'active',
    executionFence: overrides.executionFence ?? 3,
    stateVersion: 1,
    runExecution: {
      state: overrides.runState ?? 'active',
      stateVersion: 1,
    },
    runtimeStartup: {
      state: overrides.startupState ?? 'ready',
      step: 'ready',
      runtimeInstanceId,
      registrationRevision: overrides.registrationRevision ?? 2,
      executionFence: overrides.startupFence ?? overrides.executionFence ?? 3,
    },
  };
}

function creditZeroProof(sessionStatus, overrides = {}) {
  return {
    nativeRequestId: randomUUID(),
    runtimeInstanceId: overrides.runtimeInstanceId ?? sessionStatus.runtimeStartup.runtimeInstanceId,
    registrationRevision: overrides.registrationRevision ?? sessionStatus.runtimeStartup.registrationRevision,
    executionFence: overrides.executionFence ?? sessionStatus.executionFence,
    stage: 'hostPreQueue',
    code: 'hardLimitZero',
    receiptSha256: hash,
    observedEventCount: 0,
    rangeComplete: true,
    beforeCursorSha256: hash,
    afterCursorSha256: hash,
    recordedAt: '2026-11-25T09:00:00Z',
    verifiedAt: '2026-11-25T09:00:01Z',
  };
}

async function runCreditZeroFake(context, scenario = {}) {
  const input = context.caseInput;
  const sessionStatus = scenario.sessionStatus ?? creditZeroSessionStatus(input);
  const jobId = randomUUID();
  const invocations = [];
  const cancelCalls = [];
  let currentTime = 1_000_000;
  let generateCount = 0;
  let cancelCount = 0;
  let statusReadCount = 0;

  const exitCode = await runCase(
    { inputPath: context.casePath, outputPath: context.outputPath, dryRun: false },
    context.env,
    {
      now: () => currentTime,
      sleep: async ms => {
        currentTime += ms;
      },
      cleanupDeadlineMs: scenario.cleanupDeadlineMs ?? 90_000,
      cleanupReplayIntervalMs: scenario.cleanupReplayIntervalMs ?? 5_000,
      cleanupPollIntervalMs: scenario.cleanupPollIntervalMs ?? 1_000,
      jobDeadlineMs: scenario.jobDeadlineMs ?? 10_000,
      pollIntervalMs: scenario.pollIntervalMs ?? 100,
      runCommand: (_cliPath, args) => {
        const operation = args[0] === 'api' && args[1] === 'invoke' ? args[2] : args[0];
        invocations.push(operation);
        if (args[0] === '--version') return plain('1.0.0-test\n');
        if (args[0] === 'api' && args[1] === 'operations') return plain(creditZeroOperations());
        if (args[0] === 'auth' && args[1] === 'status') {
          const bound = args.includes('--project-id');
          return plain(bound
            ? { isSignedIn: true, tenantId: input.target.tenantId, projectId: input.target.projectId, runId: input.target.runId }
            : { isSignedIn: true, tenantId: input.target.tenantId });
        }
        switch (operation) {
          case 'getAuthorizationContext':
            if (args.includes('projects.admin')) {
              return ok(scenario.adminContext ?? {
                actorId: input.expectedActorId,
                tenantId: input.target.tenantId,
                boundProjectId: null,
                boundRunId: null,
              });
            }
            return ok({
              actorId: input.expectedActorId,
              tenantId: input.target.tenantId,
              boundProjectId: input.target.projectId,
              boundRunId: input.target.runId,
            });
          case 'getRunSelection': {
            if (scenario.rawSelectionText) {
              return { exitCode: 0, signal: null, stdout: scenario.rawSelectionText, stderr: '', timedOut: false, error: null };
            }
            return ok(scenario.selection ?? creditZeroSelection(input));
          }
          case 'readSessionStatus':
            return ok(sessionStatus);
          case 'readDecisions':
            return ok(scenario.decisions ?? { executionFence: sessionStatus.executionFence, stateVersion: 1 });
          case 'generateProjectWorkflow': {
            generateCount += 1;
            if (scenario.onGenerate) return scenario.onGenerate(generateCount);
            const statusUrl = `/api/v1/projects/${input.target.projectId}/workflows/generation-jobs/${jobId}`;
            return ok({
              jobId,
              status: 'queued',
              projectId: input.target.projectId,
              runId: input.target.runId,
              sessionId: input.target.sessionId,
              createdAt: '2026-11-25T09:00:00Z',
              updatedAt: '2026-11-25T09:00:00Z',
              statusUrl,
              resultUrl: `${statusUrl}/result`,
              artifact: null,
              failure: null,
            }, 202);
          }
          case 'readProjectWorkflowGenerationJob': {
            const statusUrl = `/api/v1/projects/${input.target.projectId}/workflows/generation-jobs/${jobId}`;
            if (scenario.onJobStatus) return ok(scenario.onJobStatus(jobId, statusUrl, sessionStatus));
            return ok({
              jobId,
              status: 'running',
              projectId: input.target.projectId,
              runId: input.target.runId,
              sessionId: input.target.sessionId,
              createdAt: '2026-11-25T09:00:00Z',
              updatedAt: '2026-11-25T09:00:01Z',
              statusUrl,
              resultUrl: `${statusUrl}/result`,
              artifact: null,
              failure: null,
              zeroCreditNoSend: creditZeroProof(sessionStatus),
            });
          }
          case 'cancelProjectRun': {
            cancelCount += 1;
            const keyHeader = args[args.indexOf('--header') + 1];
            const bodyArg = args[args.indexOf('--body') + 1];
            cancelCalls.push({ attempt: cancelCount, keyHeader, bodyArg });
            if (scenario.onCancel) return scenario.onCancel(cancelCount, context.outputPath);
            return ok({ runId: input.target.runId, state: 'cancelled', cancelledAt: '2026-11-25T09:00:02Z' });
          }
          case 'readRunStatus': {
            statusReadCount += 1;
            if (scenario.onRunStatus) return scenario.onRunStatus(statusReadCount, cancelCount);
            return ok({
              projectId: input.target.projectId,
              runId: input.target.runId,
              rootSessionId: input.target.sessionId,
              executionFence: sessionStatus.executionFence,
              logicalTurnOrdinal: 0,
              executionState: 'cancelled',
              stateVersion: 2,
              runtimeCleanup: { state: 'complete', total: 1, pending: 0, complete: 1 },
            });
          }
          default:
            throw new Error(`Unexpected operation ${operation}`);
        }
      },
    },
  );
  const result = JSON.parse(await readFile(path.join(context.outputPath, 'result.json'), 'utf8'));
  return { exitCode, result, invocations, cancelCalls, generateCount, cancelCount, statusReadCount };
}

test('credit-zero input rejects nonzero, rounded-to-zero, or non-v1 decision version', async () => {
  validateInput({ ...creditZeroExample, apphostSha256: hash });
  assert.throws(
    () => validateInput({
      ...creditZeroExample,
      apphostSha256: hash,
      expectedDecisionStateVersion: 2,
    }),
    /case schema/,
  );
  assert.throws(
    () => validateInput({
      ...creditZeroExample,
      apphostSha256: hash,
      expectedSelection: {
        ...creditZeroExample.expectedSelection,
        runLimits: { ...creditZeroExample.expectedSelection.runLimits, copilotHardCreditLimit: 1 },
      },
    }),
    /copilotHardCreditLimit/,
  );
  await withCreditZeroScratch(async context => {
    const roundedText = JSON.stringify({ ...creditZeroExample, apphostSha256: context.caseInput.apphostSha256 })
      .replace('"copilotHardCreditLimit":0', '"copilotHardCreditLimit":1e-400');
    await writeFile(context.casePath, roundedText);
    await assert.rejects(
      runCase({ inputPath: context.casePath, outputPath: context.outputPath, dryRun: true }, context.env),
      /explicit zero JSON number/,
    );
  });
});

test('credit-zero child and stale preflight fail closed with neither submission nor cancellation', async () => {
  await withCreditZeroScratch(async context => {
    const child = await runCreditZeroFake(context, {
      sessionStatus: creditZeroSessionStatus(context.caseInput, {
        parentSessionId: 'parent-root',
        kind: 'childWork',
      }),
    });
    assert.equal(child.exitCode, 2);
    assert.equal(child.generateCount, 0);
    assert.equal(child.cancelCount, 0);
    assert.equal(child.result.cleanup.status, 'not-attempted');
  });

  await withCreditZeroScratch(async context => {
    const staleStartup = await runCreditZeroFake(context, {
      sessionStatus: creditZeroSessionStatus(context.caseInput, { startupState: 'pending' }),
    });
    assert.equal(staleStartup.exitCode, 2);
    assert.equal(staleStartup.generateCount, 0);
    assert.equal(staleStartup.cancelCount, 0);
  });

  await withCreditZeroScratch(async context => {
    const staleDecision = await runCreditZeroFake(context, {
      decisions: { executionFence: 3, stateVersion: 2 },
    });
    assert.equal(staleDecision.exitCode, 2);
    assert.equal(staleDecision.generateCount, 0);
    assert.equal(staleDecision.cancelCount, 0);
  });

  await withCreditZeroScratch(async context => {
    const roundedSelection = JSON.stringify({
      status: 200,
      body: creditZeroSelection(context.caseInput),
    }).replace('"copilotHardCreditLimit":0', '"copilotHardCreditLimit":1e-400');
    const rounded = await runCreditZeroFake(context, { rawSelectionText: roundedSelection });
    assert.equal(rounded.exitCode, 2);
    assert.equal(rounded.generateCount, 0);
    assert.equal(rounded.cancelCount, 0);
  });
});

test('credit-zero submits once, saves proof before cancellation, and passes on verified cleanup', async () => {
  await withCreditZeroScratch(async context => {
    let proofSavedBeforeCancel = false;
    let cancelRequestSavedBeforeCancel = false;
    const outcome = await runCreditZeroFake(context, {
      onCancel: async (_count, outputDir) => {
        await access(path.join(outputDir, 'zero-credit-proof.json'));
        proofSavedBeforeCancel = true;
        await access(path.join(outputDir, 'cancellation-request.json'));
        cancelRequestSavedBeforeCancel = true;
        return ok({ runId: context.caseInput.target.runId, state: 'cancelled', cancelledAt: '2026-11-25T09:00:02Z' });
      },
    });
    assert.equal(outcome.exitCode, 0);
    assert.equal(outcome.generateCount, 1);
    assert.equal(proofSavedBeforeCancel, true);
    assert.equal(cancelRequestSavedBeforeCancel, true);
    assert.equal(outcome.invocations.includes('readProjectWorkflowGenerationResult'), false);
    assert.equal(outcome.result.numericMatrix.rows.find(row => row.id === 'credit-zero').status, 'passed');
  });
});

test('credit-zero never retries generation on queue-full and leaves credit-zero blocked', async () => {
  await withCreditZeroScratch(async context => {
    const outcome = await runCreditZeroFake(context, {
      onGenerate: () => queueFull(),
    });
    assert.equal(outcome.exitCode, 2);
    assert.equal(outcome.generateCount, 1);
    assert.equal(outcome.cancelCount, 1);
    assert.equal(outcome.result.generation.status, 'blocked');
    assert.equal(outcome.result.cleanup.status, 'passed');
    assert.equal(outcome.result.numericMatrix.rows.find(row => row.id === 'credit-zero').status, 'blocked');
  });
});

test('credit-zero rejects same-fence proof with different runtimeInstanceId or registrationRevision', async () => {
  await withCreditZeroScratch(async context => {
    const wrongInstance = await runCreditZeroFake(context, {
      onJobStatus: (jobId, statusUrl, sessionStatus) => ({
        jobId,
        status: 'running',
        projectId: context.caseInput.target.projectId,
        runId: context.caseInput.target.runId,
        sessionId: context.caseInput.target.sessionId,
        createdAt: '2026-11-25T09:00:00Z',
        updatedAt: '2026-11-25T09:00:01Z',
        statusUrl,
        resultUrl: `${statusUrl}/result`,
        artifact: null,
        failure: null,
        zeroCreditNoSend: creditZeroProof(sessionStatus, { runtimeInstanceId: randomUUID() }),
      }),
    });
    assert.equal(wrongInstance.exitCode, 1);
    assert.equal(wrongInstance.result.generation.status, 'failed');
    await assert.rejects(access(path.join(context.outputPath, 'zero-credit-proof.json')), { code: 'ENOENT' });
  });

  await withCreditZeroScratch(async context => {
    const wrongRevision = await runCreditZeroFake(context, {
      onJobStatus: (jobId, statusUrl, sessionStatus) => ({
        jobId,
        status: 'failed',
        projectId: context.caseInput.target.projectId,
        runId: context.caseInput.target.runId,
        sessionId: context.caseInput.target.sessionId,
        createdAt: '2026-11-25T09:00:00Z',
        updatedAt: '2026-11-25T09:00:01Z',
        statusUrl,
        resultUrl: `${statusUrl}/result`,
        artifact: null,
        failure: { code: 'hard_credit_limit_reached', message: 'Zero credit.' },
        zeroCreditNoSend: creditZeroProof(sessionStatus, { registrationRevision: 99 }),
      }),
    });
    assert.equal(wrongRevision.exitCode, 1);
    assert.equal(wrongRevision.result.generation.status, 'failed');
  });
});

test('credit-zero fails on unexpected job completion without fetching a draft', async () => {
  await withCreditZeroScratch(async context => {
    const outcome = await runCreditZeroFake(context, {
      onJobStatus: (jobId, statusUrl) => completedEnvelope(context.caseInput, jobId, 'completed'),
    });
    assert.equal(outcome.exitCode, 1);
    assert.equal(outcome.result.generation.status, 'failed');
    assert.equal(outcome.invocations.includes('readProjectWorkflowGenerationResult'), false);
    assert.equal(outcome.result.cleanup.status, 'passed');
  });
});

test('credit-zero replays cancellation with the same key and body on lost response, 503 queue-full, and pending cleanup', async () => {
  for (const mode of ['lost', 'queue-full', 'pending']) {
    await withCreditZeroScratch(async context => {
      const outcome = await runCreditZeroFake(context, {
        onCancel: attempt => {
          if (attempt === 1 && mode === 'lost') {
            return { exitCode: 1, signal: null, stdout: '', stderr: '', timedOut: true, error: 'ETIMEDOUT' };
          }
          if (attempt === 1 && mode === 'queue-full') {
            return queueFull('runtime_start_queue_full');
          }
          return ok({ runId: context.caseInput.target.runId, state: 'cancelled', cancelledAt: '2026-11-25T09:00:02Z' });
        },
        onRunStatus: (_readCount, cancels) => ok({
          projectId: context.caseInput.target.projectId,
          runId: context.caseInput.target.runId,
          rootSessionId: context.caseInput.target.sessionId,
          executionFence: 3,
          logicalTurnOrdinal: 0,
          executionState: 'cancelled',
          stateVersion: 2,
          runtimeCleanup: cancels >= 2
            ? { state: 'complete', total: 1, pending: 0, complete: 1 }
            : { state: 'pending', total: 1, pending: 1, complete: 0 },
        }),
      });
      assert.equal(outcome.exitCode, 0);
      assert.equal(outcome.cancelCount, 2);
      assert.equal(outcome.cancelCalls[0].keyHeader, outcome.cancelCalls[1].keyHeader);
      assert.equal(outcome.cancelCalls[0].bodyArg, outcome.cancelCalls[1].bodyArg);
      assert.equal(outcome.result.cleanup.replays, 1);
    });
  }
});

test('credit-zero stops on authority denial and fails while retaining cancellation details on deadline exhaustion', async () => {
  await withCreditZeroScratch(async context => {
    const denied = await runCreditZeroFake(context, {
      onCancel: () => ok({ error: 'forbidden' }, 403),
    });
    assert.equal(denied.exitCode, 1);
    assert.equal(denied.cancelCount, 1);
    assert.equal(denied.result.cleanup.status, 'failed');
  });

  await withCreditZeroScratch(async context => {
    const exhausted = await runCreditZeroFake(context, {
      onRunStatus: () => ok({
        projectId: context.caseInput.target.projectId,
        runId: context.caseInput.target.runId,
        rootSessionId: context.caseInput.target.sessionId,
        executionFence: 3,
        logicalTurnOrdinal: 0,
        executionState: 'cancelled',
        stateVersion: 2,
        runtimeCleanup: { state: 'pending', total: 1, pending: 1, complete: 0 },
      }),
    });
    assert.equal(exhausted.exitCode, 1);
    assert.equal(exhausted.cancelCount, 3);
    assert.equal(exhausted.result.cleanup.replays, 2);
    assert.equal(exhausted.result.cleanup.cancellationRetained, true);
    await access(path.join(context.outputPath, 'cancellation-request.json'));
  });
});

test('normal workflow-generation allows nullable copilotHardCreditLimit and credit-zero rejects null without mutations', async () => {
  await withScratch(async context => {
    const selectionWithNull = {
      ...acceptedSelection(context.caseInput),
      runLimits: { ...context.caseInput.expectedSelection.runLimits, copilotHardCreditLimit: null },
    };
    const { exitCode, result } = await runFake(context, { selection: selectionWithNull });
    assert.equal(exitCode, 0);
    assert.equal(result.generation.status, 'passed');
    assert.equal(result.cleanup.status, 'passed');
  });

  await withCreditZeroScratch(async context => {
    const outcome = await runCreditZeroFake(context, {
      selection: creditZeroSelection(context.caseInput, { runLimits: { copilotHardCreditLimit: null } }),
    });
    assert.equal(outcome.exitCode, 2);
    assert.equal(outcome.generateCount, 0);
    assert.equal(outcome.cancelCount, 0);
    assert.equal(outcome.result.cleanup.status, 'not-attempted');
  });
});

test('credit-zero gates generation and cancellation on persisted cancellation-request record', async () => {
  await withCreditZeroScratch(async context => {
    let generateCount = 0;
    let cancelCount = 0;
    const exitCode = await runCase(
      { inputPath: context.casePath, outputPath: context.outputPath, dryRun: false },
      context.env,
      {
        writeCancellationRequest: async () => {
          throw new Error('disk full');
        },
        runCommand: (_cliPath, args) => {
          const operation = args[0] === 'api' && args[1] === 'invoke' ? args[2] : args[0];
          if (args[0] === '--version') return plain('1.0.0-test\n');
          if (args[0] === 'api' && args[1] === 'operations') return plain(creditZeroOperations());
          if (args[0] === 'auth' && args[1] === 'status') {
            const bound = args.includes('--project-id');
            return plain(bound
              ? { isSignedIn: true, tenantId: context.caseInput.target.tenantId, projectId: context.caseInput.target.projectId, runId: context.caseInput.target.runId }
              : { isSignedIn: true, tenantId: context.caseInput.target.tenantId });
          }
          if (operation === 'getAuthorizationContext') {
            if (args.includes('projects.admin')) {
              return ok({
                actorId: context.caseInput.expectedActorId,
                tenantId: context.caseInput.target.tenantId,
                boundProjectId: null,
                boundRunId: null,
              });
            }
            return ok({
              actorId: context.caseInput.expectedActorId,
              tenantId: context.caseInput.target.tenantId,
              boundProjectId: context.caseInput.target.projectId,
              boundRunId: context.caseInput.target.runId,
            });
          }
          if (operation === 'getRunSelection') return ok(creditZeroSelection(context.caseInput));
          if (operation === 'readSessionStatus') return ok(creditZeroSessionStatus(context.caseInput));
          if (operation === 'readDecisions') return ok({ executionFence: 3, stateVersion: 1 });
          if (operation === 'generateProjectWorkflow') generateCount += 1;
          if (operation === 'cancelProjectRun') cancelCount += 1;
          throw new Error(`Unexpected operation ${operation}`);
        },
      },
    );
    const result = JSON.parse(await readFile(path.join(context.outputPath, 'result.json'), 'utf8'));
    assert.equal(exitCode, 1);
    assert.equal(generateCount, 0);
    assert.equal(cancelCount, 0);
    assert.equal(result.cleanup.status, 'not-attempted');
    assert.equal(result.cleanup.cancellationRetained, false);
  });

  await withCreditZeroScratch(async context => {
    const outcome = await runCreditZeroFake(context);
    assert.equal(outcome.exitCode, 0);
    const savedCancel = JSON.parse(await readFile(path.join(context.outputPath, 'cancellation-request.json'), 'utf8'));
    assert.equal(new URL(savedCancel.gatewayUrl).origin, context.caseInput.target.gatewayUrl);
    assert.equal(savedCancel.tenantId, context.caseInput.target.tenantId);
    assert.equal(savedCancel.projectId, context.caseInput.target.projectId);
    assert.equal(savedCancel.runId, context.caseInput.target.runId);
    assert.equal(savedCancel.expectedAdminActorId, context.caseInput.expectedActorId);
    assert.equal(typeof savedCancel.idempotencyKey, 'string');
    assert.deepEqual(savedCancel.body, { reason: 'workflow-generation-credit-zero acceptance cleanup' });
  });
});

test('credit-zero stops on CLI stderr auth denial and arbitrary 5xx without polling or replaying', async () => {
  await withCreditZeroScratch(async context => {
    const stderrDenied = await runCreditZeroFake(context, {
      onCancel: () => ({
        exitCode: 1,
        signal: null,
        stdout: '',
        stderr: 'The Gateway authorization-context request was denied or invalid.\n',
        timedOut: false,
        error: null,
      }),
    });
    assert.equal(stderrDenied.exitCode, 1);
    assert.equal(stderrDenied.cancelCount, 1);
    assert.equal(stderrDenied.statusReadCount, 0);
    assert.match(stderrDenied.result.cleanup.failure, /denied by authority/);
  });

  await withCreditZeroScratch(async context => {
    const server500 = await runCreditZeroFake(context, {
      onCancel: () => ok({ error: 'internal_server_error' }, 500),
    });
    assert.equal(server500.exitCode, 1);
    assert.equal(server500.cancelCount, 1);
    assert.equal(server500.statusReadCount, 0);
    assert.match(server500.result.cleanup.failure, /HTTP 500/);
  });
});

test('credit-zero caps job read timeout and rejects proof returned after polling deadline', async () => {
  await withCreditZeroScratch(async context => {
    const jobId = randomUUID();
    const sessionStatus = creditZeroSessionStatus(context.caseInput);
    let currentTime = 1_000_000;
    const jobTimeouts = [];
    const exitCode = await runCase(
      { inputPath: context.casePath, outputPath: context.outputPath, dryRun: false },
      context.env,
      {
        now: () => currentTime,
        sleep: async ms => {
          currentTime += ms;
        },
        jobDeadlineMs: 1_500,
        pollIntervalMs: 1_000,
        cleanupDeadlineMs: 10_000,
        runCommand: (_cliPath, args, _env, timeoutMs) => {
          const operation = args[0] === 'api' && args[1] === 'invoke' ? args[2] : args[0];
          if (args[0] === '--version') return plain('1.0.0-test\n');
          if (args[0] === 'api' && args[1] === 'operations') return plain(creditZeroOperations());
          if (args[0] === 'auth' && args[1] === 'status') {
            const bound = args.includes('--project-id');
            return plain(bound
              ? { isSignedIn: true, tenantId: context.caseInput.target.tenantId, projectId: context.caseInput.target.projectId, runId: context.caseInput.target.runId }
              : { isSignedIn: true, tenantId: context.caseInput.target.tenantId });
          }
          if (operation === 'getAuthorizationContext') {
            if (args.includes('projects.admin')) {
              return ok({
                actorId: context.caseInput.expectedActorId,
                tenantId: context.caseInput.target.tenantId,
                boundProjectId: null,
                boundRunId: null,
              });
            }
            return ok({
              actorId: context.caseInput.expectedActorId,
              tenantId: context.caseInput.target.tenantId,
              boundProjectId: context.caseInput.target.projectId,
              boundRunId: context.caseInput.target.runId,
            });
          }
          if (operation === 'getRunSelection') return ok(creditZeroSelection(context.caseInput));
          if (operation === 'readSessionStatus') return ok(sessionStatus);
          if (operation === 'readDecisions') return ok({ executionFence: sessionStatus.executionFence, stateVersion: 1 });
          if (operation === 'generateProjectWorkflow') {
            const statusUrl = `/api/v1/projects/${context.caseInput.target.projectId}/workflows/generation-jobs/${jobId}`;
            return ok({
              jobId,
              status: 'queued',
              projectId: context.caseInput.target.projectId,
              runId: context.caseInput.target.runId,
              sessionId: context.caseInput.target.sessionId,
              createdAt: '2026-11-25T09:00:00Z',
              updatedAt: '2026-11-25T09:00:00Z',
              statusUrl,
              resultUrl: `${statusUrl}/result`,
              artifact: null,
              failure: null,
            }, 202);
          }
          if (operation === 'readProjectWorkflowGenerationJob') {
            jobTimeouts.push(timeoutMs);
            const statusUrl = `/api/v1/projects/${context.caseInput.target.projectId}/workflows/generation-jobs/${jobId}`;
            if (jobTimeouts.length === 1) {
              return ok({
                jobId,
                status: 'running',
                projectId: context.caseInput.target.projectId,
                runId: context.caseInput.target.runId,
                sessionId: context.caseInput.target.sessionId,
                createdAt: '2026-11-25T09:00:00Z',
                updatedAt: '2026-11-25T09:00:01Z',
                statusUrl,
                resultUrl: `${statusUrl}/result`,
                artifact: null,
                failure: null,
              });
            }
            // Cross the 1500ms deadline during the second read before returning proof.
            currentTime += 600;
            return ok({
              jobId,
              status: 'running',
              projectId: context.caseInput.target.projectId,
              runId: context.caseInput.target.runId,
              sessionId: context.caseInput.target.sessionId,
              createdAt: '2026-11-25T09:00:00Z',
              updatedAt: '2026-11-25T09:00:02Z',
              statusUrl,
              resultUrl: `${statusUrl}/result`,
              artifact: null,
              failure: null,
              zeroCreditNoSend: creditZeroProof(sessionStatus),
            });
          }
          if (operation === 'cancelProjectRun') {
            return ok({ runId: context.caseInput.target.runId, state: 'cancelled', cancelledAt: '2026-11-25T09:00:02Z' });
          }
          if (operation === 'readRunStatus') {
            return ok({
              projectId: context.caseInput.target.projectId,
              runId: context.caseInput.target.runId,
              rootSessionId: context.caseInput.target.sessionId,
              executionFence: sessionStatus.executionFence,
              logicalTurnOrdinal: 0,
              executionState: 'cancelled',
              stateVersion: 2,
              runtimeCleanup: { state: 'complete', total: 1, pending: 0, complete: 1 },
            });
          }
          throw new Error(`Unexpected operation ${operation}`);
        },
      },
    );
    const result = JSON.parse(await readFile(path.join(context.outputPath, 'result.json'), 'utf8'));
    assert.equal(exitCode, 2);
    assert.deepEqual(jobTimeouts, [1500, 500]);
    assert.equal(result.generation.status, 'blocked');
    assert.equal(result.cleanup.status, 'passed');
    await assert.rejects(access(path.join(context.outputPath, 'zero-credit-proof.json')), { code: 'ENOENT' });
  });
});

test('credit-zero blocks mismatched admin actor without submitting or cancelling', async () => {
  await withCreditZeroScratch(async context => {
    const outcome = await runCreditZeroFake(context, {
      adminContext: {
        actorId: 'different-admin-actor',
        tenantId: context.caseInput.target.tenantId,
        boundProjectId: null,
        boundRunId: null,
      },
    });
    assert.equal(outcome.exitCode, 2);
    assert.equal(outcome.generateCount, 0);
    assert.equal(outcome.cancelCount, 0);
    assert.equal(outcome.result.cleanup.status, 'not-attempted');
  });
});

test('credit-zero stops on login-required stderr and incompatible terminal run status without replaying', async () => {
  for (const stderrMessage of [
    'Not signed in for this target. Run login first.\n',
    'Session expired. Run login again.\n',
  ]) {
    await withCreditZeroScratch(async context => {
      const outcome = await runCreditZeroFake(context, {
        onCancel: () => ({
          exitCode: 1,
          signal: null,
          stdout: '',
          stderr: stderrMessage,
          timedOut: false,
          error: null,
        }),
      });
      assert.equal(outcome.exitCode, 1);
      assert.equal(outcome.cancelCount, 1);
      assert.equal(outcome.statusReadCount, 0);
      assert.match(outcome.result.cleanup.failure, /denied by authority/);
    });
  }

  await withCreditZeroScratch(async context => {
    const outcome = await runCreditZeroFake(context, {
      onRunStatus: () => ok({
        projectId: context.caseInput.target.projectId,
        runId: context.caseInput.target.runId,
        rootSessionId: context.caseInput.target.sessionId,
        executionFence: 3,
        logicalTurnOrdinal: 0,
        executionState: 'completed',
        stateVersion: 2,
      }),
    });
    assert.equal(outcome.exitCode, 1);
    assert.equal(outcome.cancelCount, 1);
    assert.equal(outcome.statusReadCount, 1);
    assert.match(outcome.result.cleanup.failure, /incompatible terminal state completed/);
  });

  await withCreditZeroScratch(async context => {
    const outcome = await runCreditZeroFake(context, {
      cleanupDeadlineMs: 8_000,
      cleanupReplayIntervalMs: 5_000,
      cleanupPollIntervalMs: 1_000,
      onRunStatus: readCount => readCount === 1
        ? ok({
            projectId: context.caseInput.target.projectId,
            runId: context.caseInput.target.runId,
            rootSessionId: context.caseInput.target.sessionId,
            executionFence: 3,
            logicalTurnOrdinal: 0,
            executionState: 'cancelled',
            stateVersion: 2,
            runtimeCleanup: { state: 'pending', total: 1, pending: 1, complete: 0 },
          })
        : ok({ error: 'service_unavailable' }, 503),
    });
    assert.equal(outcome.exitCode, 1);
    assert.equal(outcome.cancelCount, 1);
    assert.equal(outcome.result.cleanup.replays, 0);
    assert.match(outcome.result.cleanup.failure, /HTTP 503/);
  });
});

test('credit-zero reports blocked exit 2 when capped job status read hits ETIMEDOUT', async () => {
  await withCreditZeroScratch(async context => {
    const sessionStatus = creditZeroSessionStatus(context.caseInput);
    const jobId = randomUUID();
    let currentTime = 1_000_000;
    const exitCode = await runCase(
      { inputPath: context.casePath, outputPath: context.outputPath, dryRun: false },
      context.env,
      {
        now: () => currentTime,
        sleep: async ms => { currentTime += ms; },
        jobDeadlineMs: 1_500,
        cleanupDeadlineMs: 10_000,
        runCommand: (_cliPath, args) => {
          const operation = args[0] === 'api' && args[1] === 'invoke' ? args[2] : args[0];
          if (args[0] === '--version') return plain('1.0.0-test\n');
          if (args[0] === 'api' && args[1] === 'operations') return plain(creditZeroOperations());
          if (args[0] === 'auth' && args[1] === 'status') {
            const bound = args.includes('--project-id');
            return plain(bound
              ? { isSignedIn: true, tenantId: context.caseInput.target.tenantId, projectId: context.caseInput.target.projectId, runId: context.caseInput.target.runId }
              : { isSignedIn: true, tenantId: context.caseInput.target.tenantId });
          }
          if (operation === 'getAuthorizationContext') {
            return ok(args.includes('projects.admin')
              ? { actorId: context.caseInput.expectedActorId, tenantId: context.caseInput.target.tenantId, boundProjectId: null, boundRunId: null }
              : { actorId: context.caseInput.expectedActorId, tenantId: context.caseInput.target.tenantId, boundProjectId: context.caseInput.target.projectId, boundRunId: context.caseInput.target.runId });
          }
          if (operation === 'getRunSelection') return ok(creditZeroSelection(context.caseInput));
          if (operation === 'readSessionStatus') return ok(sessionStatus);
          if (operation === 'readDecisions') return ok({ executionFence: sessionStatus.executionFence, stateVersion: 1 });
          if (operation === 'generateProjectWorkflow') {
            const statusUrl = `/api/v1/projects/${context.caseInput.target.projectId}/workflows/generation-jobs/${jobId}`;
            return ok({
              jobId, status: 'queued', projectId: context.caseInput.target.projectId, runId: context.caseInput.target.runId,
              sessionId: context.caseInput.target.sessionId, createdAt: '2026-11-25T09:00:00Z', updatedAt: '2026-11-25T09:00:00Z',
              statusUrl, resultUrl: `${statusUrl}/result`, artifact: null, failure: null,
            }, 202);
          }
          if (operation === 'readProjectWorkflowGenerationJob') {
            return { exitCode: null, signal: null, stdout: '', stderr: '', timedOut: true, error: 'ETIMEDOUT' };
          }
          if (operation === 'cancelProjectRun') {
            return ok({ runId: context.caseInput.target.runId, state: 'cancelled', cancelledAt: '2026-11-25T09:00:02Z' });
          }
          if (operation === 'readRunStatus') {
            return ok({
              projectId: context.caseInput.target.projectId, runId: context.caseInput.target.runId,
              rootSessionId: context.caseInput.target.sessionId, executionFence: sessionStatus.executionFence,
              logicalTurnOrdinal: 0, executionState: 'cancelled', stateVersion: 2,
              runtimeCleanup: { state: 'complete', total: 1, pending: 0, complete: 1 },
            });
          }
          throw new Error(`Unexpected operation ${operation}`);
        },
      },
    );
    const result = JSON.parse(await readFile(path.join(context.outputPath, 'result.json'), 'utf8'));
    assert.equal(exitCode, 2);
    assert.equal(result.generation.status, 'blocked');
    assert.equal(result.cleanup.status, 'passed');
  });
});

if (process.platform === 'win32') {
  test('credit-zero restricts Windows evidence directory ACL to SYSTEM and process token SID', async () => {
    await withCreditZeroScratch(async context => {
      const outcome = await runCreditZeroFake(context);
      assert.equal(outcome.exitCode, 0);
      const { spawnSync } = await import('node:child_process');
      const checkScript = [
        '$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value',
        '$acl = [System.IO.DirectoryInfo]::new($env:AGENTWEAVER_ACL_PATH).GetAccessControl()',
        'if ($acl.AreAccessRulesProtected -ne $true) { exit 2 }',
        '$rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])',
        '$sids = @($rules | ForEach-Object { $_.IdentityReference.Value })',
        'if ($sids -notcontains $sid -or $sids -notcontains "S-1-5-18") { exit 3 }',
        '$unexpected = @($sids | Where-Object { $_ -notin @($sid, "S-1-5-18") })',
        'if ($unexpected.Count -gt 0) { exit 4 }',
      ].join('; ');
      const check = spawnSync(
        'powershell.exe',
        ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(checkScript, 'utf16le').toString('base64')],
        {
          shell: false,
          windowsHide: true,
          encoding: 'utf8',
          env: { ...process.env, AGENTWEAVER_ACL_PATH: context.outputPath },
        },
      );
      assert.equal(check.status, 0);
    });
  });
}
