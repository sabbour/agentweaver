import assert from 'node:assert/strict';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync } from 'node:fs';
import { access, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  buildCommands,
  buildGenerationPrompt,
  buildCleanupCommands,
  parseArgs,
  persistOperationKey,
  runCase,
  validateCompletion,
  validateCleanupAuthorizationContext,
  validateGenerationReplay,
  validateInput,
  validateReservationEvidence,
} from '../numeric-admitted-completion.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(directory, '..', 'numeric-admitted-completion.mjs');
const examplePath = path.resolve(directory, '..', 'examples', 'numeric-admitted-completion.json');
const example = JSON.parse(await readFile(examplePath, 'utf8'));
const acceptedSelectionHash = 'a'.repeat(64);
const requestId = '00000000-0000-4000-8000-000000000101';
const runtimeInstanceId = '00000000-0000-4000-8000-000000000102';
const factId = '00000000-0000-4000-8000-000000000103';
const eventId = '00000000-0000-4000-8000-000000000104';

function input() {
  return validateInput({
    ...example,
    expectedActorId: 'approved-actor',
    target: {
      ...example.target,
      gatewayUrl: 'https://gateway.example.com',
      brokerIssuer: 'https://broker.example.com',
      clientId: 'cli-client',
      apiAudience: 'api-audience',
      tenantId: 'tenant-1',
      projectId: 'project-1',
      runId: 'run-1',
      sessionId: 'root-1',
    },
  });
}

function completionReceipt() {
  const hash = 'b'.repeat(64);
  return {
    contractVersion: 1,
    requestId,
    promptSha256: hash,
    outputSha256: hash,
    runtimeInstanceId,
    registrationRevision: 4,
    registrationSha256: hash,
    executionFence: 3,
    acceptedSelectionHash,
    sourceSha256: hash,
    sdkSessionSha256: hash,
    observeGrantId: '00000000-0000-4000-8000-000000000105',
    observeGrantRevision: 7,
    modelSelectionReference: 'provider/model',
    modelId: 'model-1',
    modelSourceMode: 'connection',
    reasoningEffort: null,
    maxPromptTokens: null,
    image: { digest: 'sha256:abc', platform: 'linux/amd64', compressedPullBytes: 10 },
    nativeMessageId: '00000000-0000-4000-8000-000000000106',
    nativeTurnStartEventId: '00000000-0000-4000-8000-000000000107',
    nativeTurnEndEventId: '00000000-0000-4000-8000-000000000108',
    nativeCompletionReceiptEventId: '00000000-0000-4000-8000-000000000109',
    nativeCompletedAt: '2026-11-17T12:00:00Z',
    nativeCursorBeforeSha256: hash,
    nativeCursorAfterSha256: hash,
    nativeEventIntervalSha256: hash,
    nativeEventCount: 3,
    usageEventIds: ['00000000-0000-4000-8000-000000000110'],
    successfulToolCount: 0,
    failedToolCount: 0,
    userMaterial: { eventId, position: 1, kind: 'turnContent', contentSha256: hash, bindingSha256: hash },
    assistantMaterial: { eventId: '00000000-0000-4000-8000-000000000111', position: 2, kind: 'turnContent', contentSha256: hash, bindingSha256: hash },
    cacheMaterial: { eventId: '00000000-0000-4000-8000-000000000112', position: 3, kind: 'sdkCache', contentSha256: hash, bindingSha256: hash },
    cacheBaselineCursorSha256: hash,
    cacheBaselineAfterCursorSha256: hash,
    cacheBaselineEventCount: 1,
    cacheBaselineIsComplete: true,
  };
}

function proposal() {
  return {
    proposalId: '00000000-0000-4000-8000-000000000113',
    projectId: 'project-1',
    baseConfigurationRevision: 1,
    draftRevision: 2,
    state: 'pending',
    generation: {
      requestId,
      mode: 'formulate',
      state: 'completed',
      nativeCompletion: completionReceipt(),
    },
  };
}

function reservationData() {
  const selected = input();
  const binding = {
    projectId: selected.target.projectId,
    runId: selected.target.runId,
    rootSessionId: selected.target.sessionId,
    sessionId: selected.target.sessionId,
    isRoot: true,
    currentExecutionFence: 3,
    sessionExecutionFence: 3,
    acceptedSelectionHash,
    runtimeInstanceId,
    registrationRevision: 4,
  };
  const reservation = {
    factId,
    kind: 'modelTurnReserved',
    limitKind: 'modelTurns',
    projectId: selected.target.projectId,
    runId: selected.target.runId,
    attemptId: requestId,
    dispatchId: requestId,
    sequence: 1,
    sessionId: selected.target.sessionId,
    configuredValue: 1,
    executionFence: 3,
    runtimeInstanceId,
    registrationRevision: 4,
    acceptedSelectionHash,
    observedValue: 1,
    reservedCount: 1,
    unit: 'count',
    scope: 'copilotRun',
    ownerDecision: { state: 'available' },
  };
  const page = {
    contractVersion: 2,
    binding,
    acceptedLimits: { maxModelTurns: 1 },
    totals: { modelSlotsReserved: { state: 'available', value: 1 } },
    history: {
      snapshotId: '00000000-0000-4000-8000-000000000114',
      highWatermark: 1,
      complete: true,
      items: [reservation],
      nextCursor: null,
    },
  };
  const exact = {
    binding,
    totals: page.totals,
    history: {
      exactAttempt: true,
      complete: true,
      items: [structuredClone(reservation)],
      nextCursor: null,
      exactUnavailableReason: null,
    },
  };
  return { selected, binding, reservation, page, exact };
}

test('admitted completion input requires target, spend, and cleanup approvals', () => {
  assert.equal(input().expectedMaxModelTurns, 1);
  for (const key of ['approvedNonProduction', 'approvedModelAndSpend', 'approvedProposalCleanup']) {
    assert.throws(() => validateInput({ ...example, [key]: false }), /schema/);
  }
  assert.throws(() => validateInput({ ...example, prompt: 'caller-controlled text' }), /schema/);
});

test('fixed command uses one generated operation key and no caller command or prompt', () => {
  const selected = input();
  const commands = buildCommands(selected, 'operation-key-1', 'canonical prompt');
  assert.deepEqual(commands.map(command => command.id), [
    'version', 'auth-status', 'api-operations', 'describe-formulate',
    'describe-observation', 'describe-configuration', 'describe-role-catalog',
    'describe-proposal', 'describe-reject-proposal', 'authorization-context',
    'cleanup-authorization-context', 'project-configuration', 'role-catalog',
    'formulate-proposal', 'observation-page-1', 'formulate-proposal-replay',
  ]);
  const bodyArg = commands.find(command => command.id === 'formulate-proposal')
    .args.slice(-1)[0];
  const body = JSON.parse(bodyArg);
  assert.equal(body.idempotencyKey, 'operation-key-1');
  assert.equal(body.expectedConfigurationRevision, 1);
  assert.equal(body.runId, 'run-1');
  assert.equal(body.sessionId, 'root-1');
  assert.equal(body.prompt.includes('caller-controlled'), false);
  assert.ok(commands.every(command => !command.args.includes('agentweaver test')));
  assert.deepEqual(
    commands.find(command => command.id === 'formulate-proposal-replay').args,
    commands.find(command => command.id === 'formulate-proposal').args,
  );
  const catalogCommand = commands.find(command => command.id === 'role-catalog');
  assert.ok(catalogCommand.args.includes('--scope'));
  assert.ok(!catalogCommand.args.includes('--project-id'));
  const cleanup = buildCleanupCommands(selected, { proposalId: proposal().proposalId, draftRevision: 2 });
  assert.deepEqual(cleanup.map(command => command.id), ['reject-proposal-cleanup', 'read-rejected-proposal']);
  assert.ok(cleanup.every(command => !command.args.includes('--project-id')
    && !command.args.includes('--run-id')
    && command.args.includes('projects.admin')));
});

test('canonical prompt uses the approved configuration revision and role catalog', () => {
  const selected = input();
  const prompt = buildGenerationPrompt(selected, {
    projectId: selected.target.projectId,
    revision: selected.expectedConfigurationRevision,
    configuration: { casting: [{ order: 2, role: 'reviewer' }, { order: 1, role: 'planner' }] },
  }, [
    { id: 'planner', title: 'Planner', summary: 'Plan work.', responsibilities: [], boundaries: [] },
    { id: 'reviewer', title: 'Reviewer', summary: 'Review changes.', responsibilities: [], boundaries: [] },
    { id: 'scribe', title: 'Scribe', summary: 'Reserved role.', responsibilities: [], boundaries: [] },
  ]);
  assert.match(prompt, /Current role IDs to avoid duplicating: \["planner","reviewer"\]/);
  assert.match(prompt, /- planner \| Planner \| Plan work\./);
  assert.doesNotMatch(prompt, /- scribe \|/);
  assert.match(buildGenerationPrompt(selected, {
    projectId: selected.target.projectId,
    revision: selected.expectedConfigurationRevision,
    configuration: { casting: [] },
  }, []), /Current role IDs to avoid duplicating: \[\]/);
  assert.throws(() => buildGenerationPrompt(selected, {
    projectId: selected.target.projectId,
    revision: selected.expectedConfigurationRevision + 1,
    configuration: { casting: [] },
  }, []), /approved revision/);
});

test('native completion requires events, usage IDs, cursors, and typed material acknowledgments', () => {
  const safe = validateCompletion(proposal(), input());
  assert.equal(safe.requestId, requestId);
  assert.equal(Object.hasOwn(safe, 'outputSha256'), false);

  const partial = proposal();
  delete partial.generation.nativeCompletion;
  assert.throws(() => validateCompletion(partial, input()), /complete native root-turn completion receipt/);

  const missingUsage = proposal();
  missingUsage.generation.nativeCompletion.usageEventIds = [];
  assert.throws(() => validateCompletion(missingUsage, input()), /complete native root-turn completion receipt/);

  const wrongMaterial = proposal();
  wrongMaterial.generation.nativeCompletion.cacheMaterial.kind = 'turnContent';
  assert.throws(() => validateCompletion(wrongMaterial, input()), /sdkCache material acknowledgment/);
  const deterministicGuid = proposal();
  deterministicGuid.generation.nativeCompletion.nativeMessageId = '00000000-0000-0000-0000-000000000116';
  assert.equal(validateCompletion(deterministicGuid, input()).requestId, requestId);
  deterministicGuid.generation.nativeCompletion.nativeMessageId = '00000000-0000-0000-0000-000000000000';
  assert.throws(() => validateCompletion(deterministicGuid, input()), /complete native/);
});

test('completion joins one current-authority reservation and exact replays preserve its count', () => {
  const { selected, reservation, page, exact } = reservationData();
  const completion = validateCompletion(proposal(), selected);
  const generationReplay = validateGenerationReplay(completion, proposal(), selected);
  const evidence = validateReservationEvidence(
    selected, completion, [page], exact, structuredClone(exact), structuredClone(exact), generationReplay,
  );
  assert.equal(evidence.reservationFactId, factId);
  assert.deepEqual(evidence.exactReplay, {
    firstFactId: factId,
    replayFactId: factId,
    restFactId: factId,
    firstCount: 1,
    replayCount: 1,
    restCount: 1,
  });
  assert.equal(evidence.numericMatrix.rows.find(row => row.id === 'model-turn-exact-replay').status, 'passed');
  assert.equal(evidence.numericMatrix.rows.find(row => row.id === 'model-turn-next-denial').status, 'blocked');

  const changedReplay = structuredClone(exact);
  changedReplay.totals.modelSlotsReserved.value = 2;
  assert.throws(() => validateReservationEvidence(
    selected, completion, [page], exact, changedReplay, exact, generationReplay,
  ), /post-replay CLI exact lookup/);

  const stalePage = structuredClone(page);
  stalePage.history.items[0].registrationRevision += 1;
  assert.throws(() => validateReservationEvidence(
    selected, completion, [stalePage], exact, exact, exact, generationReplay,
  ), /no unique reservation/);

  const missingOwnerDecision = structuredClone(reservation);
  missingOwnerDecision.ownerDecision.state = 'unavailable';
  const noOwnerPage = structuredClone(page);
  noOwnerPage.history.items[0] = missingOwnerDecision;
  const noOwnerExact = structuredClone(exact);
  noOwnerExact.history.items[0] = missingOwnerDecision;
  assert.throws(() => validateReservationEvidence(
    selected, completion, [noOwnerPage], noOwnerExact, noOwnerExact, noOwnerExact, generationReplay,
  ), /owner decision/);
  const changedGeneration = proposal();
  changedGeneration.generation.nativeCompletion.nativeEventIntervalSha256 = 'c'.repeat(64);
  assert.throws(() => validateGenerationReplay(completion, changedGeneration, selected), /different proposal or completion/);

  const incompleteExact = structuredClone(exact);
  incompleteExact.history.complete = false;
  assert.throws(() => validateReservationEvidence(
    selected, completion, [page], incompleteExact, exact, exact, generationReplay,
  ), /CLI exact lookup/);
});

test('cleanup authority must be unbound and include project write permission', () => {
  const selected = input();
  const context = {
    actorId: selected.expectedActorId,
    tenantId: selected.target.tenantId,
    boundProjectId: null,
    boundRunId: null,
    effectiveAuthority: [{
      resourceType: 'project',
      resourceId: selected.target.projectId,
      permissions: [{ permission: 'writeProjects' }],
    }],
  };
  assert.equal(validateCleanupAuthorizationContext(context, selected), context);
  for (const change of [
    { boundProjectId: selected.target.projectId },
    { boundRunId: selected.target.runId },
    { effectiveAuthority: [] },
  ]) {
    assert.throws(() => validateCleanupAuthorizationContext({ ...context, ...change }, selected), /unbound project writer/);
  }
});

test('operation key record is immutable', async () => {
  const scratch = path.join(directory, `operation-key-${randomUUID()}`);
  const hash = 'd'.repeat(64);
  await mkdir(scratch);
  try {
    await persistOperationKey(scratch, requestId, hash);
    const saved = JSON.parse(await readFile(path.join(scratch, 'operation-key.json'), 'utf8'));
    assert.equal(saved.operationKey, requestId);
    assert.equal(saved.requestSha256, hash);
    await assert.rejects(persistOperationKey(scratch, requestId, hash), { code: 'EEXIST' });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
});

test('controlled command transcript persists, replays, and cleans up the admitted completion', async () => {
  const scratch = path.join(directory, `admitted-transcript-${randomUUID()}`);
  const cliPath = path.join(scratch, process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver');
  const inputPath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch);
  try {
    await writeFile(cliPath, 'mock apphost');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    await writeFile(inputPath, JSON.stringify({
      ...example,
      apphostSha256,
      expectedActorId: 'approved-actor',
      target: {
        ...example.target,
        gatewayUrl: 'https://gateway.example.com',
        brokerIssuer: 'https://broker.example.com',
        clientId: 'cli-client',
        apiAudience: 'api-audience',
        tenantId: 'tenant-1',
        projectId: 'project-1',
        runId: 'run-1',
        sessionId: 'root-1',
      },
    }));

    const { page, exact } = reservationData();
    const commands = [];
    const generationBodies = [];
    const context = {
      actorId: 'approved-actor',
      tenantId: 'tenant-1',
      boundProjectId: 'project-1',
      boundRunId: 'run-1',
    };
    const cleanupContext = {
      ...context,
      boundProjectId: null,
      boundRunId: null,
      effectiveAuthority: [{
        resourceType: 'project',
        resourceId: 'project-1',
        permissions: [{ permission: 'writeProjects' }],
      }],
    };
    const operations = [
      'formulateCastingProposal', 'readRuntimeObservationV2',
      'rejectProjectCastingProposal', 'getProjectConfiguration',
      'listProjectCastingRoles', 'getProjectCastingProposal',
    ].map(operationId => ({ operationId }));
    const configuration = {
      projectId: 'project-1',
      revision: 1,
      configuration: { casting: [{ order: 0, role: 'planner' }] },
    };
    const roles = [
      { id: 'planner', title: 'Planner', summary: 'Plan work.', responsibilities: [], boundaries: [] },
    ];
    const proposalBody = proposal();
    const response = body => JSON.stringify({ status: 200, body });
    const record = stdout => ({
      startedAt: new Date().toISOString(),
      completedAt: new Date().toISOString(),
      durationMs: 0,
      exitCode: 0,
      signal: null,
      stdout,
      stderr: '',
      timedOut: false,
      error: null,
    });

    const result = await runCase(
      { inputPath, outputPath, dryRun: false },
      { AGENTWEAVER_CLI_PATH: cliPath },
      (_path, args) => {
        commands.push(args);
        const [root, action, operation] = args;
        if (args[0] === '--version') return record('1.1.0');
        if (root === 'auth' && action === 'status') {
          return record(JSON.stringify({ isSignedIn: true, tenantId: 'tenant-1' }));
        }
        if (root === 'api' && action === 'operations') return record(JSON.stringify(operations));
        if (root === 'api' && action === 'describe') {
          const methods = {
            formulateCastingProposal: 'POST',
            readRuntimeObservationV2: 'GET',
            getProjectConfiguration: 'GET',
            listProjectCastingRoles: 'GET',
            getProjectCastingProposal: 'GET',
            rejectProjectCastingProposal: 'POST',
          };
          return record(JSON.stringify({
            operationId: operation,
            method: methods[operation],
            path: operation === 'readRuntimeObservationV2'
              ? '/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/runtime-observation/v2'
              : `/api/${operation}`,
          }));
        }
        if (root === 'rest' && args.includes('/api/v1/authorization/context')) {
          return record(JSON.stringify({
            status: 200,
            body: args.includes('projects.admin') ? cleanupContext : context,
          }));
        }
        if (root === 'api' && action === 'invoke' && operation === 'getProjectConfiguration') {
          return record(response(configuration));
        }
        if (root === 'api' && action === 'invoke' && operation === 'listProjectCastingRoles') {
          return record(response(roles));
        }
        if (root === 'api' && action === 'invoke' && operation === 'formulateCastingProposal') {
          assert.ok(existsSync(path.join(outputPath, 'operation-key.json')));
          generationBodies.push(JSON.parse(args[args.indexOf('--body') + 1]));
          return record(response(proposalBody));
        }
        if (root === 'api' && action === 'invoke' && operation === 'readRuntimeObservationV2') {
          const query = args[args.indexOf('--query') + 1];
          return record(response(query.startsWith('attemptId=') ? exact : page));
        }
        if (root === 'rest' && args.some(value => value.includes('/runtime-observation/v2?attemptId='))) {
          return record(JSON.stringify({ status: 200, body: exact }));
        }
        if (root === 'api' && action === 'invoke' && operation === 'rejectProjectCastingProposal') {
          return record(response({
            proposalId: proposalBody.proposalId,
            draftRevision: proposalBody.draftRevision,
            state: 'rejected',
          }));
        }
        if (root === 'api' && action === 'invoke' && operation === 'getProjectCastingProposal') {
          return record(response({
            proposalId: proposalBody.proposalId,
            draftRevision: proposalBody.draftRevision,
            state: 'rejected',
          }));
        }
        throw new Error(`Unexpected command transcript: ${args.join(' ')}`);
      },
    );

    assert.equal(result.status, 'passed', result.reason);
    assert.equal(result.cleanup.status, 'rejected');
    assert.equal(result.reservationEvidence.exactReplay.firstCount, 1);
    assert.equal(generationBodies.length, 2);
    assert.deepEqual(generationBodies[1], generationBodies[0]);
    assert.equal(commands.filter(args => args[0] === 'api'
      && args[1] === 'invoke' && args[2] === 'formulateCastingProposal').length, 2);
    assert.ok(commands.some(args => args.includes('projects.admin') && !args.includes('--run-id')));
    const observedSteps = (await readFile(path.join(outputPath, 'commands.jsonl'), 'utf8'))
      .trim().split('\n').map(line => JSON.parse(line));
    const relevantSteps = observedSteps.map(item => item.stepId).filter(id =>
      ['formulate-proposal', 'observation-page-1', 'observation-exact',
        'formulate-proposal-replay', 'observation-exact-replay', 'rest-exact',
        'reject-proposal-cleanup', 'read-rejected-proposal'].includes(id));
    assert.deepEqual(relevantSteps, [
      'formulate-proposal', 'observation-page-1', 'observation-exact',
      'formulate-proposal-replay', 'observation-exact-replay', 'rest-exact',
      'reject-proposal-cleanup', 'read-rejected-proposal',
    ]);
    assert.ok(observedSteps.filter(item => item.stepId === 'formulate-proposal'
      || item.stepId === 'formulate-proposal-replay')
      .every(item => item.arguments.includes('[REDACTED]')));
    assert.equal(JSON.stringify(observedSteps).includes('Choose a minimal team'), false);
    const savedKey = JSON.parse(await readFile(path.join(outputPath, 'operation-key.json'), 'utf8'));
    assert.equal(savedKey.operationKey, generationBodies[0].idempotencyKey);
    const evidence = JSON.parse(await readFile(path.join(outputPath, 'result.json'), 'utf8'));
    assert.equal(evidence.cleanup.status, 'rejected');
    assert.equal(evidence.numericMatrix.rows.find(row => row.id === 'model-turn-exact-replay').status, 'passed');
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
});

test('dry run validates the apphost hash without starting a process or creating evidence', async () => {
  const scratch = path.join(directory, `admitted-dry-run-${randomUUID()}`);
  const cliPath = path.join(scratch, process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver');
  const inputPath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch);
  try {
    await writeFile(cliPath, 'unused apphost fixture');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    await writeFile(inputPath, JSON.stringify({
      ...example,
      apphostSha256,
      expectedActorId: 'approved-actor',
      target: {
        ...example.target,
        gatewayUrl: 'https://gateway.example.com',
        brokerIssuer: 'https://broker.example.com',
        clientId: 'cli-client',
        apiAudience: 'api-audience',
        tenantId: 'tenant-1',
        projectId: 'project-1',
        runId: 'run-1',
        sessionId: 'root-1',
      },
    }));
    const run = spawnSync(process.execPath, [
      scriptPath, '--input', inputPath, '--output', outputPath, '--dry-run',
    ], {
      cwd: path.resolve(directory, '..', '..', '..', '..'),
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(run.status, 0, run.stderr);
    const result = JSON.parse(run.stdout);
    assert.equal(result.status, 'validated-not-run');
    assert.equal(result.process, 'not-started');
    assert.equal(result.evidence, 'not-created');
    await assert.rejects(access(outputPath), { code: 'ENOENT' });
  } finally {
    await rm(scratch, { recursive: true, force: true });
  }
});

test('argument parser rejects unknown and repeated options', () => {
  assert.throws(() => parseArgs(['--input', 'case.json', '--output', 'evidence', '--token', 'secret']), /Use --input/);
  assert.throws(() => parseArgs(['--input', 'case.json', '--input', 'other.json', '--output', 'evidence']), /Use --input/);
});
