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
  let statusReads = 0;
  let submits = 0;
  const runner = (_cliPath, args, env) => {
    calls.push({ args, env });
    const operation = args[0] === 'api' && args[1] === 'invoke' ? args[2] : null;
    if (args[0] === '--version') return { ...ok({}), stdout: '1.1.0\n' };
    if (args[0] === 'api' && args[1] === 'operations') {
      return plain([
        'getAuthorizationContext', 'getRunSelection', 'generateProjectWorkflow',
        'readProjectWorkflowGenerationJob', 'readProjectWorkflowGenerationResult',
        'cancelProjectRun', 'readRunStatus',
      ].map(operationId => ({ operationId })));
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
        return ok({
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
      case 'cancelProjectRun':
        return options.cleanupFailed ? fail() : ok({ runId: input.target.runId, state: 'cancelled', cancelledAt: '2026-11-17T00:00:02Z' });
      case 'readRunStatus':
        return ok({
          projectId: input.target.projectId,
          runId: input.target.runId,
          rootSessionId: input.target.sessionId,
          executionFence: 1,
          logicalTurnOrdinal: 0,
          executionState: options.cleanupFailed ? 'running' : 'cancelled',
          stateVersion: 2,
        });
      default:
        throw new Error(`Unexpected operation: ${args.join(' ')}`);
    }
  };
  return { runner, calls, jobId };
}

async function runFake(context, options = {}, runOptions = {}) {
  const apphost = fakeApphost(context.caseInput, options);
  const exitCode = await runCase(
    { inputPath: context.casePath, outputPath: context.outputPath, dryRun: false },
    context.env,
    {
      runCommand: apphost.runner,
      jobDeadlineMs: 100,
      pollIntervalMs: 0,
      sleep: async () => {},
      ...runOptions,
    },
  );
  const result = JSON.parse(await readFile(path.join(context.outputPath, 'result.json'), 'utf8'));
  const commands = await readFile(path.join(context.outputPath, 'commands.jsonl'), 'utf8');
  return { exitCode, result, commands, calls: apphost.calls, jobId: apphost.jobId };
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
    'api-operations',
    'auth-status-run',
    'auth-status-project-writer',
    'api-invoke-authorization-context',
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
    const { exitCode, result, commands, calls } = await runFake(context, { pendingFirst: true });
    assert.equal(exitCode, 0);
    assert.equal(result.status, 'passed');
    assert.equal(result.generation.status, 'passed');
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
    assert.deepEqual(calls.slice(-5, -2).map(call => call.args), [
      buildJobStatusCommand(context.caseInput, jobId).args,
      buildJobStatusCommand(context.caseInput, jobId).args,
      buildJobResultCommand(context.caseInput, jobId).args,
    ]);
    assert.deepEqual(calls.slice(-2).map(call => call.args[2]), ['cancelProjectRun', 'readRunStatus']);
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
      assert.deepEqual(calls.slice(6).map(call => call.args[2]), [
        ...submitResponses.map(() => 'generateProjectWorkflow'), 'cancelProjectRun', 'readRunStatus',
      ]);
      const submits = calls.filter(call => call.args[2] === 'generateProjectWorkflow');
      assertSafeSubmitEvidence(commands, submits, expectedCodes);
    });
  });
}

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
    assert.match(result.cleanup.failure, /cancelled state/);
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
