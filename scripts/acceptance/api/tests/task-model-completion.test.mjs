import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  buildCleanupCommands,
  buildCommands,
  buildDispatchCommand,
  buildRunUsageCommand,
  childEnvironment,
  parseArgs,
  resolveCliPath,
  runCase,
  validateAcceptedSelection,
  validateCompletedDispatch,
  validateDecisions,
  validateInput,
  validateRunUsage,
} from '../task-model-completion.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const examplePath = path.resolve(directory, '..', 'examples', 'task-model-completion.json');
const example = JSON.parse(await readFile(examplePath, 'utf8'));
const eventId = 'c57eb1b9-6e32-4a0e-aa77-0770989753e5';
const turnRoute = '/api/v1/projects/{projectId}/runs/{runId}/sessions/{sessionId}/turns/{eventId}';
const hiddenOutput = 'private reply and diagnostic sentinel';
const apphostName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
const hash = 'a'.repeat(64);

function acceptedSelection(input, options = {}) {
  return {
    projectId: input.target.projectId,
    runId: input.target.runId,
    projectConfigurationRevision: input.expectedSelection.projectConfigurationRevision,
    modelSelection: {
      reference: input.expectedSelection.modelSelectionReference,
      sourceMode: 'byok',
      modelBindingPin: {
        modelSelectionReference: input.expectedSelection.modelSelectionReference,
        sourceMode: 'byok',
        modelId: input.expectedSelection.modelId,
      },
    },
    runLimits: {
      ...input.expectedSelection.runLimits,
      spendBudgets: [{
        budgetId: 'byok-run',
        revision: 1,
        scope: 'run',
        meterSource: 'byok.tokens',
        unit: 'USD',
        hardLimit: options.hardLimit ?? input.expectedSelection.maxRunUsd,
        reservePerDispatch: 0.5,
      }],
    },
  };
}

function completedDispatch(input, options = {}) {
  const task = input.expectedTask;
  const completion = {
    state: 'completed',
    taskId: task.taskId,
    taskRevision: task.taskRevision,
    claimTaskRevision: task.taskRevision,
    claimId: task.claimId,
    taskContentSha256: hash,
    runId: input.target.runId,
    rootSessionId: input.target.sessionId,
    acceptedSelectionHash: hash,
    executionFence: 1,
    workPlanId: task.workPlanId,
    workflowId: task.workflowId,
    definitionRevision: task.definitionRevision,
    associationId: task.associationId,
    childSessionId: 'child-session-1',
    childOutputSha256: hash,
    childReceiptSha256: hash,
    checkpointId: 'checkpoint-1',
    checkpointRevision: 2,
    decisionStateVersion: input.expectedDecisionStateVersion,
    outputSetSha256: hash,
    outputWitnessProofSha256: hash,
  };
  const receipt = {
    contractVersion: 1,
    requestSha256: hash,
    promptSha256: hash,
    workPlanId: task.workPlanId,
    associationId: task.associationId,
    childSessionId: completion.childSessionId,
    outputSha256: completion.childOutputSha256,
    assistantMaterial: { eventId, kind: 'turnContent', contentSha256: completion.childOutputSha256 },
    registrationSha256: hash,
    sourceSha256: hash,
    executionFence: completion.executionFence,
    modelSelectionReference: options.wrongModel
      ? 'other-model-reference'
      : input.expectedSelection.modelSelectionReference,
    modelSourceMode: 'byok',
    modelId: input.expectedSelection.modelId,
    acceptedSelectionHash: completion.acceptedSelectionHash,
    workItemBindingSha256: hash,
    usageEventIds: [randomUUID()],
  };
  return {
    workPlanId: task.workPlanId,
    decisionStateVersion: input.expectedDecisionStateVersion,
    checkpointRevision: 2,
    dispatchedAssociationIds: [task.associationId],
    unavailableExecutorStepIds: [],
    failedDependencyIds: [],
    isComplete: true,
    completedResults: { [task.associationId]: 'task output' },
    wallTimeLimitReached: false,
    childRuntimeStartup: {},
    completionEvidence: [receipt],
    taskCompletion: completion,
  };
}

function runUsage(input, options = {}) {
  return {
    projectId: input.target.projectId,
    runId: input.target.runId,
    events: 1,
    isFullyPriced: options.unpriced !== true,
    agents: [],
    amounts: [{
      meterSource: 'byok.tokens',
      unit: 'USD',
      amount: options.amount ?? (options.overBudget ? input.expectedSelection.maxRunUsd + 1 : 1.75),
      pricedEvents: 1,
      unpricedEvents: options.unpriced === true ? 1 : 0,
    }],
  };
}

function ok(body, status = 200) {
  return {
    exitCode: 0,
    signal: null,
    stdout: JSON.stringify({ status, body }),
    stderr: '',
    timedOut: false,
    error: null,
  };
}

function plain(body) {
  return { exitCode: 0, signal: null, stdout: JSON.stringify(body), stderr: '', timedOut: false, error: null };
}

function fail() {
  return { exitCode: 1, signal: null, stdout: '', stderr: 'controlled failure', timedOut: false, error: null };
}

async function withScratch(callback) {
  const scratch = path.join(directory, `task-case-${randomUUID()}`);
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
      env: {
        PATH: process.env.PATH,
        AGENTWEAVER_CLI_PATH: cliPath,
        AGENTWEAVER_RUN_ID: 'stale-run',
      },
    });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
}

function fakeApphost(input, options = {}) {
  const calls = [];
  let dispatchCount = 0;
  let cleanupReadCount = 0;
  const runner = (_cliPath, args, env) => {
    calls.push({ args, env });
    const operation = args[0] === 'api' && args[1] === 'invoke' ? args[2] : null;
    if (args[0] === '--version') return { ...ok({}), stdout: '1.1.0\n' };
    if (args[0] === '--help') {
      return { ...ok({}), stdout: options.oldApphost
        ? 'agentweaver api invoke readSessionTurnContent --turn-content-sha256-extra\n'
        : '  agentweaver api invoke readSessionTurnContent --turn-content-sha256\n' };
    }
    if (args[0] === 'api' && args[1] === 'operations') {
      return plain([
        'getAuthorizationContext', 'getRunSelection', 'readDecisions',
        'dispatchCoordinatorExecution', 'readRunUsage', 'cancelProjectRun', 'readRunStatus',
      ].map(operationId => ({ operationId })).concat(options.noTurnRoute ? [] : [{
        operationId: 'readSessionTurnContent',
        method: options.wrongTurnMethod ?? 'GET',
        path: options.wrongTurnPath ?? turnRoute,
      }]));
    }
    if (args[0] === 'api' && args[1] === 'describe' && args[2] === 'readSessionTurnContent') {
      const description = {
        operationId: 'readSessionTurnContent',
        method: 'GET',
        path: turnRoute,
        parameters: ['projectId', 'runId', 'sessionId', 'eventId']
          .map(name => ({
            name, in: 'path', required: true,
            schema: name === 'eventId' ? { type: 'string', format: 'uuid' } : { type: 'string' },
          })),
        responses: { 200: { content: { 'application/json': {
          schema: { $ref: '#/components/schemas/SessionTurnContent' },
        } } } },
      };
      description.parameters.push({ name: 'X-Agentweaver-Tenant', in: 'header', required: false });
      options.changeDescription?.(description);
      return options.describeFailed ? fail() : plain(description);
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
      case 'readDecisions':
        return ok({
          stateVersion: input.expectedDecisionStateVersion,
          canDispatch: true,
        });
      case 'dispatchCoordinatorExecution':
        dispatchCount += 1;
        if (options.blockedStartup) {
          return ok({
            workPlanId: input.expectedTask.workPlanId,
            decisionStateVersion: input.expectedDecisionStateVersion,
            checkpointRevision: 1,
            isComplete: false,
            completionEvidence: [],
            failedDependencyIds: [],
            unavailableExecutorStepIds: ['task-step-1'],
            wallTimeLimitReached: false,
            childRuntimeStartup: {
              [input.expectedTask.associationId]: {
                state: 'blocked',
                step: 'task-step-1',
                failureCode: 'runtime_child_start_blocked',
              },
            },
          });
        }
        if (options.terminalFailure) {
          return ok({
            workPlanId: input.expectedTask.workPlanId,
            decisionStateVersion: input.expectedDecisionStateVersion,
            checkpointRevision: 1,
            isComplete: false,
            completionEvidence: [],
            failedDependencyIds: ['item-1'],
          });
        }
        return ok(dispatchCount === 1 && options.pendingFirst
          ? {
            workPlanId: input.expectedTask.workPlanId,
            decisionStateVersion: input.expectedDecisionStateVersion,
            checkpointRevision: 1,
            isComplete: false,
            completionEvidence: [],
            failedDependencyIds: [],
            unavailableExecutorStepIds: ['task-step-1'],
            wallTimeLimitReached: false,
            childRuntimeStartup: {
              [input.expectedTask.associationId]: {
                state: 'pending',
                step: 'task-step-1',
              },
            },
          }
          : options.changeReceipt?.(completedDispatch(input, options)) ?? completedDispatch(input, options));
      case 'readSessionTurnContent': {
        const completion = completedDispatch(input, options).taskCompletion;
        if (options.failedTurnRead) {
          return { ...fail(), stdout: hiddenOutput, stderr: hiddenOutput, error: hiddenOutput, timedOut: true };
        }
        return plain({
          projectId: input.target.projectId,
          runId: input.target.runId,
          sessionId: completion.childSessionId,
          eventId,
          contentSha256: completion.childOutputSha256,
          ...options.turnFields,
        });
      }
      case 'readRunUsage':
        return ok(runUsage(input, options));
      case 'cancelProjectRun':
        return options.cleanupFailed
          ? fail()
          : ok({ runId: input.target.runId, state: 'cancelled', alreadyTerminal: false });
      case 'readRunStatus':
        cleanupReadCount += 1;
        return ok({
          projectId: input.target.projectId,
          runId: input.target.runId,
          executionState: options.cleanupFailed ? 'running' : 'cancelled',
          runtimeCleanup: options.cleanupNeverCompletes
            || options.cleanupPendingFirst && cleanupReadCount === 1
            ? { state: 'pending', total: 1, pending: 1, complete: 0 }
            : { state: 'complete', total: 1, pending: 0, complete: 1 },
        });
      default:
        throw new Error(`Unexpected operation: ${args.join(' ')}`);
    }
  };
  return { runner, calls };
}

async function runFake(context, options = {}) {
  const apphost = fakeApphost(context.caseInput, options);
  let fakeTime = Date.now();
  const exitCode = await runCase(
    { inputPath: context.casePath, outputPath: context.outputPath, dryRun: false },
    context.env,
    {
      runCommand: apphost.runner,
      pollIntervalMs: 0,
      cleanupPollIntervalMs: 2_000,
      now: () => fakeTime,
      sleep: async milliseconds => { fakeTime += milliseconds; },
    },
  );
  const result = JSON.parse(await readFile(path.join(context.outputPath, 'result.json'), 'utf8'));
  const commands = await readFile(path.join(context.outputPath, 'commands.jsonl'), 'utf8');
  return { exitCode, result, commands, calls: apphost.calls };
}

test('case input requires an approved disposable run, BYOK model, budget, task and native target', () => {
  const parsed = validateInput(example);
  assert.equal(parsed.caseId, 'task-model-completion');
  assert.throws(() => validateInput({ ...example, approvedDisposableRun: false }), /schema/);
  assert.throws(() => validateInput({ ...example, bearerToken: 'do-not-log-this' }), /schema/);
  assert.throws(() => validateInput({
    ...example,
    expectedSelection: { ...example.expectedSelection, maxRunUsd: 0 },
  }), /schema/);
  assert.throws(() => validateInput({
    ...example,
    expectedSelection: { ...example.expectedSelection, maxRunUsd: 5.01 },
  }), /schema/);
  assert.throws(() => validateInput({
    ...example,
    expectedSelection: {
      ...example.expectedSelection,
      runLimits: {
        ...example.expectedSelection.runLimits,
        maxChildren: 0,
        maxConcurrentChildren: 0,
      },
    },
  }), /Copilot limits/);
  assert.throws(() => parseArgs(['--input', 'case.json', '--output', 'evidence', '--token', 'secret']), /Use --input/);
});

test('preflight requires the accepted BYOK model, hard run budget, and dispatchable decision version', () => {
  const input = validateInput(example);
  assert.equal(validateAcceptedSelection(acceptedSelection(input), input), input.expectedSelection.maxRunUsd);
  assert.equal(validateAcceptedSelection(acceptedSelection(input, { hardLimit: 4 }), input), 4);
  assert.throws(() => validateAcceptedSelection({
    ...acceptedSelection(input),
    modelSelection: { ...acceptedSelection(input).modelSelection, sourceMode: 'hostedCopilot' },
  }, input), /BYOK model/);
  assert.throws(() => validateAcceptedSelection({
    ...acceptedSelection(input),
    runLimits: { ...acceptedSelection(input).runLimits, spendBudgets: [] },
  }, input), /hard BYOK USD budget/);
  assert.equal(validateDecisions({
    stateVersion: input.expectedDecisionStateVersion,
    canDispatch: true,
  }, input), input.expectedDecisionStateVersion);
  assert.throws(() => validateDecisions({ stateVersion: 2, canDispatch: true }, input), /expected decision version/);
});

test('task completion needs a matching receipt and the selected BYOK model', () => {
  const input = validateInput(example);
  assert.equal(
    validateCompletedDispatch(
      completedDispatch(input),
      input,
      input.expectedDecisionStateVersion,
    ).taskId,
    input.expectedTask.taskId,
  );
  assert.throws(() => validateCompletedDispatch(
    completedDispatch(input, { wrongModel: true }),
    input,
    input.expectedDecisionStateVersion,
  ), /expected BYOK model/);
  assert.throws(() => validateCompletedDispatch({
    ...completedDispatch(input),
    taskCompletion: null,
  }, input, input.expectedDecisionStateVersion), /task-completion evidence/);
  const mismatchedCheckpoint = completedDispatch(input);
  mismatchedCheckpoint.taskCompletion.checkpointRevision += 1;
  assert.throws(() => validateCompletedDispatch(
    mismatchedCheckpoint,
    input,
    input.expectedDecisionStateVersion,
  ), /task-completion evidence/);
});

test('usage must be fully priced in BYOK USD and stay below the hard limit', () => {
  const input = validateInput(example);
  const completion = validateCompletedDispatch(
    completedDispatch(input),
    input,
    input.expectedDecisionStateVersion,
  );
  assert.equal(validateRunUsage(runUsage(input), input, completion).amount, 1.75);
  assert.throws(() => validateRunUsage(runUsage(input, { unpriced: true }), input, completion), /unpriced/);
  assert.throws(() => validateRunUsage(runUsage(input, { overBudget: true }), input, completion), /above/);
  assert.throws(() => validateRunUsage(runUsage(input, { amount: 4.5 }), input, completion, 4), /above/);
});

test('case retries the same dispatch, checks completion and usage, then cancels the disposable run', async () => {
  await withScratch(async context => {
    const { exitCode, result, commands, calls } = await runFake(context, {
      pendingFirst: true,
      cleanupPendingFirst: true,
    });
    assert.equal(exitCode, 0);
    assert.equal(result.status, 'passed');
    assert.equal(result.dispatch.status, 'passed');
    assert.equal(result.dispatch.attempts, 2);
    assert.equal(result.dispatch.task.taskId, context.caseInput.expectedTask.taskId);
    assert.ok(!Object.hasOwn(result.dispatch.task, 'childReceiptSha256'));
    assert.deepEqual(result.dispatch.storedTurn, {
      projectId: context.caseInput.target.projectId,
      runId: context.caseInput.target.runId,
      sessionId: result.dispatch.task.childSessionId,
      eventId,
      contentSha256: result.dispatch.task.childOutputSha256,
    });
    assert.equal(result.dispatch.usage.amount, 1.75);
    assert.equal(result.cleanup.status, 'passed');
    assert.equal(result.cleanup.runtimeCleanup.state, 'complete');
    assert.equal(calls.filter(call => call.args[2] === 'readRunStatus').length, 2);
    assert.match(commands, /api-invoke-dispatch-task/);
    assert.match(commands, /api-invoke-turn-digest/);
    assert.match(commands, /api-invoke-run-usage/);
    assert.doesNotMatch(commands, /task output/);
    assert.doesNotMatch(commands, /"stdout"|"stderr"|"error"|"arguments"/);
    const [turn] = calls.filter(call => call.args[1] === 'invoke' && call.args[2] === 'readSessionTurnContent');
    assert.ok(turn.args.includes('--turn-content-sha256'));
    assert.ok(turn.args.includes('projects.orchestrator'));
    assert.ok(turn.args.includes(`sessionId=${result.dispatch.task.childSessionId}`));
    assert.ok(turn.args.includes(`eventId=${eventId}`));

    const dispatchCalls = calls.filter(call => call.args[2] === 'dispatchCoordinatorExecution');
    assert.equal(dispatchCalls.length, 2);
    assert.deepEqual(dispatchCalls[0].args, dispatchCalls[1].args);
    const dispatchBody = dispatchCalls[0].args[dispatchCalls[0].args.indexOf('--body') + 1];
    assert.deepEqual(JSON.parse(dispatchBody), {
      expectedStateVersion: context.caseInput.expectedDecisionStateVersion,
      workPlanId: context.caseInput.expectedTask.workPlanId,
    });

    test('the stored assistant reply is verified against the child receipt without a receipt hash', async () => {
      await withScratch(async context => {
        const { exitCode, result } = await runFake(context, {
          changeReceipt: response => {
            delete response.taskCompletion.childReceiptSha256;
            return response;
          },
        });
        assert.equal(exitCode, 0);
        assert.equal(result.dispatch.storedTurn.eventId, eventId);
        assert.equal(result.dispatch.storedTurn.contentSha256, result.dispatch.task.childOutputSha256);
        assert.ok(!Object.hasOwn(result.dispatch.task, 'childReceiptSha256'));
        assert.equal(result.cleanup.status, 'passed');
      });
    });

    test('preflight blocks dispatch without the exact apphost flag and Gateway GET route', async t => {
      for (const [name, options] of Object.entries({
        'old apphost': { oldApphost: true },
        'missing route': { noTurnRoute: true },
        'wrong method': { wrongTurnMethod: 'POST' },
        'wrong path': { wrongTurnPath: '/api/v1/another-route' },
        'describe failed': { describeFailed: true },
        'missing required path parameter': { changeDescription: op => op.parameters.shift() },
        'extra required query': {
          changeDescription: op => op.parameters.push({ name: 'partition', in: 'query', required: true }),
        },
        'extra required header': {
          changeDescription: op => op.parameters.push({ name: 'X-Owner', in: 'header', required: true }),
        },
        'required body': { changeDescription: op => { op.requestBody = { required: true }; } },
        'missing 200 JSON even with other success JSON': {
          changeDescription: op => { op.responses = { 202: op.responses[200] }; },
        },
        'wrong 200 response media type': {
          changeDescription: op => { op.responses[200].content = { 'text/plain': {} }; },
        },
        'wrong 200 response schema': {
          changeDescription: op => { op.responses[200].content['application/json'].schema = { type: 'string' }; },
        },
      })) {
        await t.test(name, () => withScratch(async context => {
          const { exitCode, calls } = await runFake(context, options);
          assert.equal(exitCode, 2);
          assert.ok(!calls.some(call => call.args[2] === 'dispatchCoordinatorExecution'));
        }));
      }
    });

    test('receipt and turn failures still clean up without redispatch or output leaks', async t => {
      const editReceipt = edit => ({ changeReceipt: response => {
        edit(response.completionEvidence, response.completionEvidence[0]);
        return response;
      } });
      for (const [name, options] of Object.entries({
        'missing receipt': editReceipt(receipts => receipts.pop()),
        'duplicate receipt': editReceipt((receipts, receipt) => receipts.push({ ...receipt, outputSha256: 'f'.repeat(64) })),
        'missing event': editReceipt((_, receipt) => { delete receipt.assistantMaterial.eventId; }),
        'wrong material kind': editReceipt((_, receipt) => { receipt.assistantMaterial.kind = 'sdkCache'; }),
        'wrong material hash': editReceipt((_, receipt) => { receipt.assistantMaterial.contentSha256 = 'f'.repeat(64); }),
        'wrong receipt hash': editReceipt((_, receipt) => { receipt.outputSha256 = 'f'.repeat(64); }),
        'wrong project': { turnFields: { projectId: hiddenOutput } },
        'wrong run': { turnFields: { runId: hiddenOutput } },
        'wrong child session': { turnFields: { sessionId: hiddenOutput } },
        'wrong event': { turnFields: { eventId: 'eafc81b9-6e32-4a0e-aa77-0770989753e5' } },
        'wrong digest': { turnFields: { contentSha256: 'f'.repeat(64) } },
        'additional output': { turnFields: { text: hiddenOutput } },
        'failed read with partial streams': { failedTurnRead: true },
      })) {
        await t.test(name, () => withScratch(async context => {
          const { exitCode, result, commands, calls } = await runFake(context, options);
          assert.equal(exitCode, 1);
          assert.equal(result.cleanup.status, 'passed');
          assert.equal(result.dispatch.storedTurn, null);
          assert.equal(calls.filter(call => call.args[2] === 'dispatchCoordinatorExecution').length, 1);
          assert.equal(calls.filter(call => call.args[1] === 'invoke' && call.args[2] === 'readSessionTurnContent').length,
            options.changeReceipt ? 0 : 1);
          assert.ok(!calls.some(call => call.args[2] === 'readRunUsage'));
          assert.ok(!commands.includes(hiddenOutput));
          assert.ok(!JSON.stringify(result).includes(hiddenOutput));
        }));
      }
    });

    const cancel = calls.find(call => call.args[2] === 'cancelProjectRun');
    assert.ok(cancel.args.includes('projects.admin'));
    assert.ok(!cancel.args.includes('--project-id'));
    assert.ok(!cancel.args.includes('--run-id'));
    assert.deepEqual(childEnvironment({
      PATH: 'system-path',
      AGENTWEAVER_RUN_ID: 'stale-run',
      AGENTWEAVER_CLI_PATH: 'stale-apphost',
    }), { PATH: 'system-path' });
  });
});

test('terminal incomplete dispatch stops retry and cleanup still runs', async () => {
  await withScratch(async context => {
    const { exitCode, result, calls } = await runFake(context, { terminalFailure: true });
    assert.equal(exitCode, 1);
    assert.equal(result.dispatch.status, 'failed');
    assert.match(result.dispatch.failure, /terminal failure/);
    assert.equal(result.cleanup.status, 'passed');
    assert.equal(calls.filter(call => call.args[2] === 'dispatchCoordinatorExecution').length, 1);
  });
});

test('blocked child startup stops dispatch retry and cleanup still runs', async () => {
  await withScratch(async context => {
    const { exitCode, result, calls } = await runFake(context, { blockedStartup: true });
    assert.equal(exitCode, 1);
    assert.equal(result.dispatch.status, 'failed');
    assert.match(result.dispatch.failure, /terminal failure/);
    assert.equal(result.cleanup.status, 'passed');
    assert.equal(calls.filter(call => call.args[2] === 'dispatchCoordinatorExecution').length, 1);
  });
});

test('cleanup fails when runtime work remains pending until its deadline', async () => {
  await withScratch(async context => {
    const { exitCode, result, calls } = await runFake(context, { cleanupNeverCompletes: true });
    assert.equal(exitCode, 1);
    assert.equal(result.cleanup.status, 'failed');
    assert.equal(result.cleanup.runtimeCleanup.state, 'pending');
    assert.match(result.cleanup.failure, /did not complete before its deadline/);
    assert.ok(calls.filter(call => call.args[2] === 'readRunStatus').length > 1);
  });
});

test('model or budget mismatch blocks dispatch and cleanup failure remains visible', async () => {
  await withScratch(async context => {
    const selection = acceptedSelection(context.caseInput);
    selection.runLimits.spendBudgets[0].hardLimit = 6;
    const { exitCode, result, calls } = await runFake(context, { selection, cleanupFailed: true });
    assert.equal(exitCode, 1);
    assert.equal(result.dispatch.status, 'blocked');
    assert.equal(result.cleanup.status, 'failed');
    assert.equal(calls.some(call => call.args[2] === 'dispatchCoordinatorExecution'), false);
  });
});

test('native apphost validation and dry-run do not invoke the apphost or create evidence', async () => {
  const expectedName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  assert.throws(() => resolveCliPath(expectedName), /absolute path/);
  assert.throws(() => resolveCliPath(path.resolve(directory, 'node.exe')), /native/);
  assert.equal(path.basename(resolveCliPath(path.resolve(directory, expectedName))), expectedName);

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

test('dispatch and cleanup commands use the public API through the native CLI', () => {
  const input = validateInput(example);
  const operations = buildCommands(input).map(command => command.id);
  assert.deepEqual(operations, [
    'version',
    'help',
    'api-operations',
    'api-describe-turn-digest',
    'auth-status-run',
    'auth-status-project-writer',
    'api-invoke-authorization-context',
    'api-invoke-run-selection',
    'api-invoke-decisions',
  ]);
  assert.equal(buildDispatchCommand(input, 1).args[2], 'dispatchCoordinatorExecution');
  assert.equal(buildRunUsageCommand(input).args[2], 'readRunUsage');
  const [cancel, verify] = buildCleanupCommands(input, 'cleanup-key');
  assert.equal(cancel.args[2], 'cancelProjectRun');
  assert.equal(verify.args[2], 'readRunStatus');
});
