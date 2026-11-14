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
  exactLookupFact,
  parseArgs,
  resolveCliPath,
  validateAuthorizationContext,
  validateInput,
  validateObservation,
} from '../numeric-runtime-observation.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.resolve(directory, '..', 'numeric-runtime-observation.mjs');
const inputPath = path.resolve(directory, '..', 'examples', 'numeric-runtime-observation.json');
const input = JSON.parse(await readFile(inputPath, 'utf8'));
const modelTurnInput = JSON.parse(await readFile(
  path.resolve(directory, '..', 'examples', 'numeric-model-turn-no-send.json'), 'utf8',
));
const hardCreditInput = JSON.parse(await readFile(
  path.resolve(directory, '..', 'examples', 'numeric-hard-credit-no-send.json'), 'utf8',
));
const selectionHash = 'a'.repeat(64);
const otherSelectionHash = 'b'.repeat(64);
const authorityMutations = {
  'stale fence': { executionFence: 2, reason: /stale or different execution fence/ },
  'other selection': { acceptedSelectionHash: otherSelectionHash, reason: /another accepted selection/ },
  'other session': { sessionId: 'other-child-session', reason: /another session/ },
};

function mutateAuthority(fact, mutation) {
  const { reason, ...fields } = mutation;
  Object.assign(fact, fields);
  return reason;
}

function rootFact(sessionId, factId, fields) {
  return {
    factId: `00000000-0000-4000-8000-0000000001${factId}`,
    sequence: Number(factId),
    sessionId,
    executionFence: 3,
    acceptedSelectionHash: selectionHash,
    ownerDecision: { state: 'available' },
    nativeNoSend: { state: 'unavailable' },
    ...fields,
  };
}

function rootBody(caseInput, acceptedLimits, totals, items) {
  return {
    contractVersion: 2,
    binding: {
      projectId: caseInput.target.projectId,
      runId: caseInput.target.runId,
      sessionId: caseInput.target.sessionId,
      isRoot: true,
      acceptedSelectionHash: selectionHash,
      currentExecutionFence: 3,
      sessionExecutionFence: 3,
    },
    acceptedLimits,
    totals,
    history: {
      snapshotId: '00000000-0000-4000-8000-000000000115',
      highWatermark: 20,
      complete: true,
      items,
      nextCursor: null,
    },
  };
}

function modelTurnObservation() {
  const sessionId = modelTurnInput.target.sessionId;
  const attemptId = '00000000-0000-4000-8000-000000000299';
  const reservations = ['01', '02'].map(id => rootFact(sessionId, id, {
    kind: 'modelTurnReserved', limitKind: 'modelTurns', attemptId: `00000000-0000-4000-8000-0000000002${id}`,
  }));
  const denied = {
    kind: 'denied', limitKind: 'modelTurns', stage: 'rootAuthorization', reasonCode: 'limit_reached', attemptId,
  };
  const denial = rootFact(sessionId, '03', denied);
  const noSend = rootFact(sessionId, '04', {
    ...denied,
    kind: 'nativeNoSend',
    nativeNoSend: { state: 'available', rangeComplete: true, eventCount: 0 },
  });
  const ignored = [
    rootFact('other-child-session', '05', { kind: 'modelTurnReserved', limitKind: 'modelTurns' }),
    rootFact(sessionId, '06', { ...denied, executionFence: 2 }),
  ];
  const body = rootBody(
    modelTurnInput,
    { maxModelTurns: 2 },
    { modelSlotsReserved: { state: 'available', value: 2 } },
    [...reservations, ...ignored, denial, noSend],
  );
  const exact = { history: { exactAttempt: true, items: [structuredClone(denial), structuredClone(noSend)] } };
  return { body, exact, restExact: structuredClone(exact) };
}

function hardCreditObservation() {
  const sessionId = hardCreditInput.target.sessionId;
  const dispatchId = '00000000-0000-4000-8000-000000000301';
  const guard = (id, observedValue, flags) => rootFact(sessionId, id, {
    kind: 'creditGuardChanged', observedValue, softWarningEmitted: false, hardCapExhausted: false, ...flags,
  });
  const denied = {
    kind: 'denied',
    limitKind: 'copilotHardCredits',
    stage: 'nativeTurnBegin',
    reasonCode: 'hard_credit_limit_reached',
    unit: 'nanoAiu',
    attemptId: dispatchId,
    dispatchId,
    configuredValue: 5_000_000_000,
    observedValue: 5_200_000_000,
  };
  const denial = rootFact(sessionId, '13', denied);
  const noSend = rootFact(sessionId, '14', {
    ...denied,
    kind: 'nativeNoSend',
    nativeNoSend: { state: 'available', rangeComplete: true, eventCount: 0 },
  });
  const body = rootBody(
    hardCreditInput,
    { copilotHardCreditLimit: 5_000_000_000 },
    {},
    [
      guard('10', 4_000_000_000, { softWarningEmitted: true }),
      guard('11', 5_200_000_000, { hardCapExhausted: true }),
      rootFact(sessionId, '12', { ...denied, configuredValue: 4_000_000_000, executionFence: 2 }),
      denial,
      noSend,
    ],
  );
  const exact = { history: { exactAttempt: true, items: [structuredClone(denial), structuredClone(noSend)] } };
  return { body, exact, restExact: structuredClone(exact) };
}

function observation() {
  const reservation = {
    factId: '00000000-0000-4000-8000-000000000010',
    kind: 'toolInvocationReserved',
    limitKind: 'toolCalls',
    sessionId: input.target.sessionId,
    attemptId: '00000000-0000-4000-8000-000000000011',
  };
  const rejection = {
    factId: '00000000-0000-4000-8000-000000000012',
    kind: 'toolPermissionRejected',
    limitKind: 'toolCalls',
    stage: 'toolPermission',
    reasonCode: 'runtime_tool_call_limit_exhausted',
    sessionId: input.target.sessionId,
    attemptId: '00000000-0000-4000-8000-000000000013',
    dispatchId: '00000000-0000-4000-8000-000000000014',
    ownerDecision: { state: 'available' },
    toolPermission: { state: 'available' },
    nativeNoSend: { state: 'unavailable' },
    toolExecution: { state: 'unavailable' },
  };
  const body = {
    contractVersion: 2,
    binding: {
      projectId: input.target.projectId,
      runId: input.target.runId,
      sessionId: input.target.sessionId,
      isRoot: false,
      currentExecutionFence: 2,
      sessionExecutionFence: 2,
    },
    acceptedLimits: { maxToolCalls: 1 },
    totals: { toolSlotsReserved: { state: 'available', value: 1 } },
    history: {
      snapshotId: '00000000-0000-4000-8000-000000000015',
      highWatermark: 2,
      complete: true,
      items: [reservation, rejection],
      nextCursor: null,
    },
  };
  const exact = {
    history: { exactAttempt: true, items: [rejection] },
  };
  return { body, exact, restExact: structuredClone(exact) };
}

test('case input accepts only approved target metadata and safe resource IDs', () => {
  const parsed = validateInput(input);
  assert.equal(parsed.caseId, 'numeric-runtime-observation');
  assert.equal(parsed.expectedMaxToolCalls, 1);
  assert.throws(() => validateInput({ ...input, bearerToken: 'do-not-log-this' }), /schema/);
  assert.throws(() => validateInput({
    ...input,
    target: { ...input.target, sessionId: 'bad/session/id' },
  }), /schema/);
  assert.throws(() => validateInput({ ...input, approvedNonProduction: false }), /schema/);
});

test('commands use the normal CLI and REST surfaces without passing run text', () => {
  const commands = buildCommands(validateInput(input));
  assert.deepEqual(commands.map(command => command.id), [
    'version',
    'auth-status',
    'api-operations',
    'api-describe-runtime-observation',
    'rest-authorization-context',
    'run-status',
    'project-usage',
    'observation-page-1',
  ]);
  assert.ok(commands.some(command => command.args.includes('rest')));
  assert.ok(commands.some(command => command.args.includes('readRuntimeObservationV2')));
  assert.ok(commands.every(command => !command.args.some(argument => argument.includes('prompt'))));
  assert.ok(commands[7].args.includes('--query'));
  assert.ok(commands[7].args.includes('pageSize=100'));
  for (const command of commands.slice(2, 4)) {
    assert.ok(!command.args.includes('--project-id'));
    assert.ok(!command.args.includes('--run-id'));
  }
  const boundCommands = [commands[1], commands[4], commands[5], commands[6], commands[7]];
  for (const command of boundCommands) {
    assert.ok(command.args.includes('--project-id'));
    assert.ok(command.args.includes(input.target.projectId));
    assert.ok(command.args.includes('--run-id'));
    assert.ok(command.args.includes(input.target.runId));
    const scopes = command.args.flatMap((argument, index) => (
      argument === '--scope' ? [command.args[index + 1]] : []
    ));
    assert.deepEqual(scopes, ['api.read', 'projects.orchestrator']);
  }
});

test('authorization context must identify the same bound actor, tenant, project, and run', () => {
  const parsed = validateInput(input);
  const context = {
    actorId: parsed.expectedActorId,
    tenantId: parsed.target.tenantId,
    boundProjectId: parsed.target.projectId,
    boundRunId: parsed.target.runId,
  };
  assert.equal(validateAuthorizationContext(context, parsed), context);
  for (const key of ['actorId', 'tenantId', 'boundProjectId', 'boundRunId']) {
    assert.throws(() => validateAuthorizationContext(
      { ...context, [key]: 'different-value' },
      parsed,
    ), /case actor, tenant, and bound run/);
  }
});

test('API invocation response bodies require HTTP 200 and unwrap the CLI envelope', () => {
  const { body } = observation();
  const response = { status: 200, headers: {}, evidence: {}, body };
  assert.equal(apiResponseBody(response, 'read'), body);
  assert.throws(() => apiResponseBody({ ...response, status: 403 }, 'read'), /HTTP 200/);
  assert.throws(() => apiResponseBody({ ...response, body: [] }, 'read'), /JSON body/);
});

test('case passes only when paged history and both exact lookups retain rejection evidence', () => {
  const { body, exact, restExact } = observation();
  const cliResponse = value => ({ status: 200, headers: {}, evidence: {}, body: value });
  const summary = validateObservation(
    validateInput(input),
    [apiResponseBody(cliResponse(body), 'observation page')],
    apiResponseBody(cliResponse(exact), 'exact lookup'),
    restExact,
  );
  assert.equal(summary.attemptId, '00000000-0000-4000-8000-000000000013');
  assert.match(summary.conclusion, /does not prove/);

  const unavailable = structuredClone(body);
  unavailable.history.complete = false;
  assert.throws(() => validateObservation(validateInput(input), [unavailable], exact, restExact), /incomplete/);

  const missingPermission = structuredClone(exact);
  missingPermission.history.items[0].toolPermission.state = 'unavailable';
  assert.throws(() => validateObservation(validateInput(input), [body], missingPermission, restExact), /Exact attempt lookup/);

  const mismatchedAttempt = structuredClone(exact);
  mismatchedAttempt.history.items[0].attemptId = '00000000-0000-4000-8000-000000000099';
  assert.throws(() => validateObservation(
    validateInput(input), [body], mismatchedAttempt, restExact,
  ), /Exact attempt lookup/);

  const mismatchedRestEvidence = structuredClone(restExact);
  mismatchedRestEvidence.history.items[0].dispatchId = '00000000-0000-4000-8000-000000000099';
  mismatchedRestEvidence.history.items[0].toolPermission.payloadHash = 'different-hash';
  assert.throws(() => validateObservation(
    validateInput(input), [body], exact, mismatchedRestEvidence,
  ), /REST exact lookup/);
});

test('case rejects a changed snapshot and repeated facts across history pages', () => {
  const { body, exact, restExact } = observation();
  const secondPage = structuredClone(body);
  secondPage.history.items = [secondPage.history.items[1]];
  secondPage.history.nextCursor = null;
  body.history.items = [body.history.items[0]];
  body.history.nextCursor = 'opaque-cursor';
  assert.equal(validateObservation(validateInput(input), [body, secondPage], exact, restExact).pagesRead, 2);

  const changedSnapshot = structuredClone(secondPage);
  changedSnapshot.history.snapshotId = '00000000-0000-4000-8000-000000000016';
  assert.throws(() => validateObservation(
    validateInput(input), [body, changedSnapshot], exact, restExact,
  ), /snapshot/);

  const duplicate = structuredClone(body);
  duplicate.history.items = [body.history.items[0]];
  assert.throws(() => validateObservation(
    validateInput(input), [body, duplicate], exact, restExact,
  ), /duplicate fact/);
});

test('native CLI path must be absolute and name the apphost', () => {
  const expectedName = process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver';
  assert.throws(() => resolveCliPath('agentweaver.exe'), /absolute path/);
  assert.throws(() => resolveCliPath(path.resolve(directory, 'node.exe')), /native/);
  assert.equal(path.basename(resolveCliPath(path.resolve(directory, expectedName))), expectedName);
});

test('dry-run validates the apphost hash and creates no evidence or network request', async () => {
  const scratch = path.join(directory, `dry-run-${randomUUID()}`);
  const cliPath = path.join(scratch, process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver');
  const casePath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch);
  try {
    await writeFile(cliPath, 'non-executable dry-run fixture');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    await writeFile(casePath, JSON.stringify({ ...input, apphostSha256 }));
    const run = spawnSync(process.execPath, [
      scriptPath, '--input', casePath, '--output', outputPath, '--dry-run',
    ], {
      cwd: path.resolve(directory, '..', '..', '..', '..'),
      encoding: 'utf8',
      env: { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
    });
    assert.equal(run.status, 0, run.stderr);
    const result = JSON.parse(run.stdout);
    assert.equal(result.status, 'validated-not-run');
    assert.equal(result.network, 'not-used');
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

test('numeric no-send cases accept only their own input fields', () => {
  const modelTurn = validateInput(modelTurnInput);
  assert.equal(modelTurn.caseId, 'numeric-model-turn-no-send');
  assert.equal(modelTurn.expectedMaxModelTurns, 2);
  assert.equal(Object.hasOwn(modelTurn, 'expectedMaxToolCalls'), false);
  const hardCredit = validateInput(hardCreditInput);
  assert.equal(hardCredit.caseId, 'numeric-hard-credit-no-send');
  assert.equal(hardCredit.expectedSoftWarning, true);

  const { expectedMaxModelTurns, ...withoutTurns } = modelTurnInput;
  assert.throws(() => validateInput({ ...withoutTurns, expectedMaxToolCalls: expectedMaxModelTurns }), /schema/);
  assert.throws(() => validateInput({ ...modelTurnInput, expectedMaxModelTurns: 0 }), /schema/);
  assert.throws(() => validateInput({ ...hardCreditInput, expectedSoftWarning: 'true' }), /schema/);
  assert.throws(() => validateInput({ ...hardCreditInput, copilotHardCreditLimit: 1 }), /schema/);
  assert.throws(() => validateInput({ ...input, caseId: 'numeric-model-turn-no-send' }), /schema/);
});

test('model-turn case passes on current-authority root denial and no-send evidence', () => {
  const caseInput = validateInput(modelTurnInput);
  const { body, exact, restExact } = modelTurnObservation();
  const summary = validateObservation(caseInput, [body], exact, restExact);
  assert.equal(summary.attemptId, '00000000-0000-4000-8000-000000000299');
  assert.equal(summary.factId, body.history.items[5].factId);
  assert.equal(summary.currentModelTurnReservations, 2);
  assert.equal(exactLookupFact(caseInput, [body]), body.history.items[5]);

  const child = structuredClone(body);
  child.binding.isRoot = false;
  assert.throws(() => validateObservation(caseInput, [child], exact, restExact), /root session/);
});

test('model-turn case passes when an earlier denial has no no-send fact and a later pair is valid', () => {
  const caseInput = validateInput(modelTurnInput);
  const { body, exact, restExact } = modelTurnObservation();
  const later = body.history.items[4];
  body.history.items.splice(2, 0, {
    ...structuredClone(later),
    factId: '00000000-0000-4000-8000-000000000107',
    attemptId: '00000000-0000-4000-8000-000000000298',
  });
  const summary = validateObservation(caseInput, [body], exact, restExact);
  assert.equal(summary.attemptId, later.attemptId);
  assert.equal(summary.denialFactId, later.factId);
});

test('model-turn case ignores denial and no-send facts outside the current authority', () => {
  const caseInput = validateInput(modelTurnInput);
  for (const [index, label] of [[4, 'root model-turn denial'], [5, 'model-turn native no-send']]) {
    for (const [name, mutation] of Object.entries(authorityMutations)) {
      const { body, exact, restExact } = modelTurnObservation();
      const reason = mutateAuthority(body.history.items[index], mutation);
      assert.throws(
        () => validateObservation(caseInput, [body], exact, restExact),
        error => error.message.includes(label) && reason.test(error.message),
        `${label} with ${name}`,
      );
    }
  }
});

test('model-turn case does not count reservations outside the current authority', () => {
  const caseInput = validateInput(modelTurnInput);
  for (const [name, mutation] of Object.entries(authorityMutations)) {
    const { body, exact, restExact } = modelTurnObservation();
    mutateAuthority(body.history.items[1], mutation);
    assert.throws(
      () => validateObservation(caseInput, [body], exact, restExact),
      /1 model-turn reservations that match the current authority; the case needs 2/,
      name,
    );
  }
});

test('model-turn case requires the run-wide total to equal the limit', () => {
  const caseInput = validateInput(modelTurnInput);
  for (const [value, reason] of [[3, /over-admission/], [1, /do not reach/]]) {
    const { body, exact, restExact } = modelTurnObservation();
    body.totals.modelSlotsReserved.value = value;
    assert.throws(() => validateObservation(caseInput, [body], exact, restExact), reason);
  }
  const { body, exact, restExact } = modelTurnObservation();
  body.totals.modelSlotsReserved.state = 'unknown';
  assert.throws(() => validateObservation(caseInput, [body], exact, restExact), /not available/);
});

test('model-turn case requires a complete zero-event native no-send witness', () => {
  const caseInput = validateInput(modelTurnInput);
  for (const change of [{ eventCount: 1 }, { rangeComplete: false }]) {
    const { body, exact, restExact } = modelTurnObservation();
    Object.assign(body.history.items[5].nativeNoSend, change);
    assert.throws(() => validateObservation(caseInput, [body], exact, restExact), /complete range and zero events/);
  }
});

test('no-send cases reject exact and REST lookups outside the current authority', () => {
  for (const [caseInput, observation] of [
    [validateInput(modelTurnInput), modelTurnObservation],
    [validateInput(hardCreditInput), hardCreditObservation],
  ]) {
    for (const [name, mutation] of Object.entries(authorityMutations)) {
      const exactCase = observation();
      const reason = mutateAuthority(exactCase.exact.history.items[1], mutation);
      assert.throws(
        () => validateObservation(caseInput, [exactCase.body], exactCase.exact, exactCase.restExact),
        error => /^Exact attempt lookup .* current authority/.test(error.message) && reason.test(error.message),
        `${caseInput.caseId} exact ${name}`,
      );
      const restCase = observation();
      mutateAuthority(restCase.restExact.history.items[1], mutation);
      assert.throws(
        () => validateObservation(caseInput, [restCase.body], restCase.exact, restCase.restExact),
        error => /^The REST exact lookup .* current authority/.test(error.message) && reason.test(error.message),
        `${caseInput.caseId} REST ${name}`,
      );
    }
  }
});

test('hard-credit case passes on current-authority denial, no-send, and guard facts', () => {
  const caseInput = validateInput(hardCreditInput);
  const { body, exact, restExact } = hardCreditObservation();
  const summary = validateObservation(caseInput, [body], exact, restExact);
  assert.equal(summary.dispatchId, '00000000-0000-4000-8000-000000000301');
  assert.equal(summary.factId, body.history.items[4].factId);
  assert.equal(summary.softWarningObserved, true);
  assert.equal(exactLookupFact(caseInput, [body]), body.history.items[4]);

  const unset = structuredClone(body);
  unset.acceptedLimits.copilotHardCreditLimit = null;
  assert.throws(() => validateObservation(caseInput, [unset], exact, restExact), /is not set/);
});

test('hard-credit case passes when an earlier denial has no no-send fact and a later pair is valid', () => {
  const caseInput = validateInput(hardCreditInput);
  const { body, exact, restExact } = hardCreditObservation();
  const later = body.history.items[3];
  body.history.items.splice(2, 0, {
    ...structuredClone(later),
    factId: '00000000-0000-4000-8000-000000000115',
    attemptId: '00000000-0000-4000-8000-000000000300',
    dispatchId: '00000000-0000-4000-8000-000000000300',
  });
  const summary = validateObservation(caseInput, [body], exact, restExact);
  assert.equal(summary.dispatchId, later.dispatchId);
  assert.equal(summary.denialFactId, later.factId);
});

test('hard-credit case ignores denial, no-send, and guard facts outside the current authority', () => {
  const caseInput = validateInput(hardCreditInput);
  for (const [index, label] of [
    [3, /hard-credit denial/],
    [4, /hard-credit native no-send/],
    [1, /hard cap is exhausted/],
    [0, /expected soft warning/],
  ]) {
    for (const [name, mutation] of Object.entries(authorityMutations)) {
      const { body, exact, restExact } = hardCreditObservation();
      mutateAuthority(body.history.items[index], mutation);
      assert.throws(
        () => validateObservation(caseInput, [body], exact, restExact),
        label,
        `fact ${index} with ${name}`,
      );
    }
  }
});

test('hard-credit case rejects old configuration, floor decrease, and incomplete no-send', () => {
  const caseInput = validateInput(hardCreditInput);
  const oldConfiguration = hardCreditObservation();
  oldConfiguration.body.history.items[3].configuredValue = 4_000_000_000;
  assert.throws(() => validateObservation(
    caseInput, [oldConfiguration.body], oldConfiguration.exact, oldConfiguration.restExact,
  ), /old configuration/);

  const decrease = hardCreditObservation();
  decrease.body.history.items[1].observedValue = 3_000_000_000;
  assert.throws(() => validateObservation(
    caseInput, [decrease.body], decrease.exact, decrease.restExact,
  ), /floor decreased/);

  const events = hardCreditObservation();
  events.body.history.items[4].nativeNoSend.eventCount = 1;
  assert.throws(() => validateObservation(
    caseInput, [events.body], events.exact, events.restExact,
  ), /complete range and zero events/);

  const noSoftWarning = hardCreditObservation();
  noSoftWarning.body.history.items[0].softWarningEmitted = false;
  assert.throws(() => validateObservation(
    caseInput, [noSoftWarning.body], noSoftWarning.exact, noSoftWarning.restExact,
  ), /expected soft warning/);
  const notExpected = validateInput({ ...hardCreditInput, expectedSoftWarning: false });
  assert.equal(validateObservation(
    notExpected, [noSoftWarning.body], noSoftWarning.exact, noSoftWarning.restExact,
  ).softWarningObserved, false);
});
