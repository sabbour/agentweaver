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
  compareDecimals,
  exactLookupFact,
  parseChildObservationJson,
  parseDotNetDecimal,
  parseArgs,
  resolveCliPath,
  runCase,
  validateAuthorizationContext,
  validateChildHardCreditObservation,
  validateInput,
  validateObservation,
} from '../numeric-runtime-observation.mjs';
import {
  buildNumericMatrix,
  compareBoundary,
  validateNumericMatrix,
  warningFloorTransition,
} from '../numeric-case-matrix.mjs';

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
const childHardCreditInput = JSON.parse(await readFile(
  path.resolve(directory, '..', 'examples', 'numeric-child-hard-credit-denial.json'), 'utf8',
));
const selectionHash = 'a'.repeat(64);
const otherSelectionHash = 'b'.repeat(64);
const runtimeInstanceId = '00000000-0000-4000-8000-000000000221';
const registrationRevision = 7;
const authorityMutations = {
  'stale fence': { executionFence: 2, reason: /stale or different execution fence/ },
  'other selection': { acceptedSelectionHash: otherSelectionHash, reason: /another accepted selection/ },
  'other session': { sessionId: 'other-child-session', reason: /another session/ },
  'other registration': { registrationRevision: registrationRevision + 1, reason: /another runtime registration/ },
};

function mutateAuthority(fact, mutation) {
  const { reason, ...fields } = mutation;
  Object.assign(fact, fields);
  return reason;
}

// This fact starts with the current root binding; tests override fields for each case.
function rootFact(sessionId, factId, fields) {
  return {
    factId: `00000000-0000-4000-8000-0000000001${factId}`,
    sequence: Number(factId),
    sessionId,
    runtimeInstanceId,
    registrationRevision,
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
      rootSessionId: caseInput.target.sessionId,
      sessionId: caseInput.target.sessionId,
      isRoot: true,
      acceptedSelectionHash: selectionHash,
      currentExecutionFence: 3,
      sessionExecutionFence: 3,
      runtimeInstanceId,
      registrationRevision,
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
    kind: 'modelTurnReserved',
    limitKind: 'modelTurns',
    configuredValue: 2,
    observedValue: Number(id),
    reservedCount: Number(id),
    unit: 'count',
    scope: 'copilotRun',
    attemptId: `00000000-0000-4000-8000-0000000002${id}`,
  }));
  const denied = {
    kind: 'denied',
    limitKind: 'modelTurns',
    configuredValue: 2,
    observedValue: 2,
    unit: 'count',
    scope: 'copilotRun',
    stage: 'rootAuthorization',
    reasonCode: 'limit_reached',
    attemptId,
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
  const exact = {
    binding: structuredClone(body.binding),
    history: {
      exactAttempt: true,
      complete: true,
      items: [structuredClone(denial), structuredClone(noSend)],
      nextCursor: null,
      exactUnavailableReason: null,
    },
  };
  return { body, exact, restExact: structuredClone(exact) };
}

function hardCreditObservation() {
  const sessionId = hardCreditInput.target.sessionId;
  const dispatchId = '00000000-0000-4000-8000-000000000301';
  const guard = (id, observedValue, flags) => rootFact(sessionId, id, {
    kind: 'creditGuardChanged',
    observedValue,
    unit: 'nanoAiu',
    scope: 'copilotRun',
    softWarningEmitted: false,
    hardCapExhausted: false,
    accountedThroughLedgerEventCount: Number(id),
    ownerRevision: Number(id),
    ...flags,
  });
  const denied = {
    kind: 'denied',
    limitKind: 'copilotHardCredits',
    stage: 'nativeTurnBegin',
    reasonCode: 'hard_credit_limit_reached',
    unit: 'nanoAiu',
    scope: 'copilotRun',
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
    {
      copilotSoftCreditLimit: 4_000_000_000,
      copilotHardCreditLimit: 5_000_000_000,
      creditUnit: 'nanoAiu',
      creditScope: 'copilotRun',
    },
    {},
    [
      guard('10', 3_900_000_000, { softWarningEmitted: false }),
      guard('11', 4_000_000_000, { softWarningEmitted: true }),
      rootFact(sessionId, '12', { ...denied, configuredValue: 4_000_000_000, executionFence: 2 }),
      denial,
      noSend,
      guard('16', 5_200_000_000, { hardCapExhausted: true }),
    ],
  );
  const exact = {
    binding: structuredClone(body.binding),
    history: {
      exactAttempt: true,
      complete: true,
      items: [structuredClone(denial), structuredClone(noSend)],
      nextCursor: null,
      exactUnavailableReason: null,
    },
  };
  return { body, exact, restExact: structuredClone(exact) };
}

function childHardCreditObservation(options = {}) {
  const caseInput = validateInput(childHardCreditInput);
  const factId = '00000000-0000-4000-8000-000000000401';
  const fact = {
    factId,
    sequence: 1,
    kind: 'denied',
    projectId: caseInput.target.projectId,
    runId: caseInput.target.runId,
    attemptId: caseInput.attemptId,
    dispatchId: caseInput.attemptId,
    sessionId: caseInput.target.sessionId,
    executionFence: 4,
    runtimeInstanceId,
    registrationRevision,
    acceptedSelectionHash: selectionHash,
    ownerRevision: null,
    limitKind: 'copilotHardCredits',
    configuredValue: '__CONFIGURED__',
    observedValue: '__OBSERVED__',
    unit: 'nanoAiu',
    scope: 'copilotRun',
    stage: 'childPreparation',
    reasonCode: 'hard_credit_limit_reached',
    ownerDecision: {
      state: 'available',
      receiptId: factId,
      payloadHash: 'd'.repeat(64),
    },
    nativeNoSend: {
      state: 'unavailable',
      reason: 'no_native_boundary_witness',
    },
  };
  const binding = {
    projectId: caseInput.target.projectId,
    runId: caseInput.target.runId,
    rootSessionId: 'root-session',
    sessionId: caseInput.target.sessionId,
    isRoot: false,
    acceptedSelectionHash: selectionHash,
    currentExecutionFence: 4,
    sessionExecutionFence: 4,
    runtimeInstanceId,
    registrationRevision,
  };
  const body = {
    contractVersion: 2,
    binding,
    acceptedLimits: {
      copilotHardCreditLimit: '__LIMIT__',
      creditUnit: 'nanoAiu',
      creditScope: 'copilotRun',
    },
    totals: {},
    history: {
      snapshotId: '00000000-0000-4000-8000-000000000402',
      highWatermark: 1,
      complete: true,
      exactAttempt: false,
      items: [fact],
      nextCursor: null,
    },
  };
  const exact = {
    contractVersion: 2,
    binding: structuredClone(binding),
    acceptedLimits: structuredClone(body.acceptedLimits),
    history: {
      snapshotId: '00000000-0000-4000-8000-000000000403',
      highWatermark: 1,
      exactAttempt: true,
      complete: true,
      items: [structuredClone(fact)],
      nextCursor: null,
      exactUnavailableReason: null,
    },
  };
  const replaceTokens = value => JSON.stringify(value)
    .replace('"__LIMIT__"', options.limitToken ?? '1000000000')
    .replace('"__CONFIGURED__"', options.configuredToken ?? options.limitToken ?? '1000000000')
    .replace(
      '"__OBSERVED__"',
      options.observedToken
        ?? (options.relation === 'overshoot' ? '1000000000.000000001' : '1000000000'),
    );
  const pageRaw = replaceTokens({ status: 200, headers: {}, body });
  const exactRaw = replaceTokens(exact);
  const restRaw = `{"status":200,"body":${exactRaw}}`;
  return { body, exact, pageRaw, exactRaw, restRaw };
}

async function parseChildFixture(value, step = 'observation') {
  const parsed = await parseChildObservationJson(
    typeof value === 'string' ? value : JSON.stringify(value),
    step,
  );
  if (parsed.value.status === 200 && parsed.value.body) parsed.value = parsed.value.body;
  return parsed;
}

function observation() {
  const reservation = {
    factId: '00000000-0000-4000-8000-000000000010',
    kind: 'toolInvocationReserved',
    limitKind: 'toolCalls',
    sessionId: input.target.sessionId,
    executionFence: 2,
    acceptedSelectionHash: selectionHash,
    configuredValue: 1,
    observedValue: 1,
    reservedCount: 1,
    runtimeInstanceId,
    registrationRevision,
    unit: 'count',
    scope: 'copilotRun',
    attemptId: '00000000-0000-4000-8000-000000000011',
  };
  const rejection = {
    factId: '00000000-0000-4000-8000-000000000012',
    kind: 'toolPermissionRejected',
    limitKind: 'toolCalls',
    stage: 'toolPermission',
    reasonCode: 'runtime_tool_call_limit_exhausted',
    configuredValue: 1,
    observedValue: 1,
    runtimeInstanceId,
    registrationRevision,
    unit: 'count',
    scope: 'copilotRun',
    sessionId: input.target.sessionId,
    executionFence: 2,
    acceptedSelectionHash: selectionHash,
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
      rootSessionId: 'root-session',
      sessionId: input.target.sessionId,
      isRoot: false,
      currentExecutionFence: 2,
      sessionExecutionFence: 2,
      acceptedSelectionHash: selectionHash,
      runtimeInstanceId,
      registrationRevision,
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
    binding: structuredClone(body.binding),
    history: {
      exactAttempt: true,
      complete: true,
      items: [rejection],
      nextCursor: null,
      exactUnavailableReason: null,
    },
  };
  return { body, exact, restExact: structuredClone(exact) };
}

function twoSlotObservation() {
  const fixture = observation();
  const { body, exact, restExact } = fixture;
  const belowCap = {
    ...body.history.items[0],
    projectId: input.target.projectId,
    runId: input.target.runId,
    configuredValue: 2,
    observedValue: 1,
    reservedCount: 1,
    sequence: 7,
  };
  const atCap = {
    ...structuredClone(belowCap),
    factId: '00000000-0000-4000-8000-000000000026',
    observedValue: 2,
    reservedCount: 2,
    sequence: 12,
    attemptId: '00000000-0000-4000-8000-000000000027',
  };
  const rejection = body.history.items[1];
  rejection.configuredValue = 2;
  rejection.observedValue = 2;
  rejection.sequence = 13;
  body.acceptedLimits.maxToolCalls = 2;
  body.totals.toolSlotsReserved.value = 2;
  body.history.highWatermark = 15;
  body.history.items = [belowCap];
  body.history.nextCursor = 'second-page';
  const secondPage = structuredClone(body);
  secondPage.history.items = [atCap, rejection];
  secondPage.history.nextCursor = null;
  exact.history.items = [structuredClone(rejection)];
  restExact.history.items = [structuredClone(rejection)];
  return { pages: [body, secondPage], exact, restExact, belowCap, atCap };
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

test('child hard-credit input requires one attempt and one explicit relation', () => {
  const parsed = validateInput(childHardCreditInput);
  assert.equal(parsed.caseId, 'numeric-child-hard-credit-denial');
  assert.equal(parsed.expectedRelation, 'equal');
  assert.throws(() => validateInput({ ...childHardCreditInput, expectedRelation: 'both' }), /schema/);
  assert.throws(() => validateInput({ ...childHardCreditInput, attemptId: 'not-a-uuid' }), /schema/);
  assert.throws(() => validateInput({ ...childHardCreditInput, expectedSoftWarning: true }), /schema/);
  const uppercase = childHardCreditInput.attemptId.toUpperCase();
  assert.equal(validateInput({ ...childHardCreditInput, attemptId: uppercase }).attemptId,
    childHardCreditInput.attemptId);
});

test('child hard-credit parser rejects literal and escaped duplicate keys', async () => {
  for (const source of [
    '{"configuredValue":1,"configuredValue":2}',
    '{"configuredValue":1,"configured\\u0056alue":2}',
    '{"nested":{"copilotHardCreditLimit":1,"copilotHardCreditLimit":2}}',
  ]) {
    await assert.rejects(parseChildObservationJson(source, 'step'), /strict JSON with unique keys/);
  }
  await assert.rejects(
    parseChildObservationJson('copilotHardCreditLimit: 1', 'step'),
    /strict JSON with unique keys/,
  );
  await assert.rejects(
    parseChildObservationJson('{"configuredValue":"1"}', 'step'),
    /numeric credit tokens/,
  );
});

test('bounded decimal parsing preserves precision and enforces .NET decimal bounds', () => {
  for (const [token, comparison, rightToken] of [
    ['999999999.999999999', -1, '1000000000'],
    ['1e-28', -1, '1000000000'],
    ['1e3', 0, '1000.0'],
  ]) {
    assert.equal(
      compareDecimals(parseDotNetDecimal(token), parseDotNetDecimal(rightToken)),
      comparison,
    );
  }
  assert.equal(
    parseDotNetDecimal('79228162514264337593543950335.0').coefficient,
    79_228_162_514_264_337_593_543_950_335n,
  );
  for (const token of [
    '-1',
    '1e-65',
    '1e65',
    '1e-29',
    '79228162514264337593543950336',
    '1e29',
    `${'1'.repeat(129)}`,
  ]) {
    assert.throws(() => parseDotNetDecimal(token), /credit value|decimal/);
  }
});

test('child hard-credit validation binds one equal or overshoot attempt across all reads', async () => {
  for (const relation of ['equal', 'overshoot']) {
    const fixture = childHardCreditObservation({ relation });
    const caseInput = validateInput({ ...childHardCreditInput, expectedRelation: relation });
    const summary = validateChildHardCreditObservation(
      caseInput,
      [await parseChildFixture(fixture.pageRaw)],
      await parseChildFixture(fixture.exactRaw),
      await parseChildFixture(fixture.restRaw),
    );
    assert.equal(summary.relation, relation);
    assert.equal(summary.creditNumberSources.copilotHardCreditLimit, '1000000000');
    assert.equal(summary.creditNumberSources.configuredValue, '1000000000');
    assert.equal(
      summary.creditNumberSources.observedValue,
      relation === 'equal' ? '1000000000' : '1000000000.000000001',
    );
  }

  const fixture = childHardCreditObservation();
  const page = await parseChildFixture(fixture.pageRaw);
  const exact = await parseChildFixture(fixture.exactRaw);
  const rest = await parseChildFixture(fixture.restRaw);
  const caseInput = validateInput(childHardCreditInput);
  for (const [mutate, reason] of [
    [value => { value.value.binding.currentExecutionFence = 5; }, /selected child session and current fence|current authority/],
    [value => { value.value.history.items[0].attemptId = '00000000-0000-4000-8000-000000000499'; }, /exactly one/],
    [value => { value.value.history.items[0].ownerDecision.receiptId = '00000000-0000-4000-8000-000000000499'; }, /owner-backed/],
  ]) {
    const parsed = await parseChildFixture(fixture.pageRaw);
    mutate(parsed);
    assert.throws(
      () => validateChildHardCreditObservation(caseInput, [parsed], exact, rest),
      reason,
    );
  }
  const mismatch = await parseChildFixture(fixture.exactRaw);
  mismatch.value.history.items[0].ownerDecision.payloadHash = 'a'.repeat(64);
  assert.throws(
    () => validateChildHardCreditObservation(caseInput, [page], mismatch, rest),
    /same denial/,
  );

  for (const [mutate, reason] of [
    [value => { value.binding.acceptedSelectionHash = undefined; }, /selected child session/],
    [value => { value.binding.currentExecutionFence = undefined; }, /selected child session/],
    [value => { value.binding.currentExecutionFence = Number.MAX_SAFE_INTEGER + 1; }, /selected child session/],
    [value => { value.history.snapshotId = 'not-a-uuid'; }, /paged response/],
    [value => { value.history.highWatermark = undefined; }, /paged response/],
    [value => { value.history.exactAttempt = true; }, /paged response/],
  ]) {
    const malformed = await parseChildFixture(fixture.pageRaw);
    mutate(malformed.value);
    assert.throws(
      () => validateChildHardCreditObservation(caseInput, [malformed], exact, rest),
      reason,
    );
  }

  const extraExactItem = await parseChildFixture(fixture.exactRaw);
  extraExactItem.value.history.items.push({
    ...structuredClone(extraExactItem.value.history.items[0]),
    factId: '00000000-0000-4000-8000-000000000499',
  });
  assert.throws(
    () => validateChildHardCreditObservation(caseInput, [page], extraExactItem, rest),
    /exactly one pinned denial/,
  );

  const below = childHardCreditObservation({ observedToken: '999999999.999999999' });
  const belowInput = validateInput({ ...childHardCreditInput, expectedRelation: 'equal' });
  const belowPage = await parseChildFixture(below.pageRaw);
  const matchingOther = structuredClone(belowPage.value.history.items[0]);
  matchingOther.factId = '00000000-0000-4000-8000-000000000498';
  matchingOther.attemptId = '00000000-0000-4000-8000-000000000497';
  matchingOther.dispatchId = matchingOther.attemptId;
  matchingOther.observedValue = 1_000_000_000;
  belowPage.value.history.items.push(matchingOther);
  belowPage.value.history.highWatermark = 2;
  matchingOther.sequence = 2;
  assert.throws(
    () => validateChildHardCreditObservation(
      belowInput,
      [belowPage],
      exact,
      rest,
    ),
    /below its configured value/,
  );

  for (const [mutate, reason] of [
    [value => { value.history.snapshotId = 'not-a-uuid'; }, /incomplete or truncated/],
    [value => { value.history.highWatermark = -1; }, /incomplete or truncated/],
    [value => { value.history.highWatermark = 0; }, /exactly one pinned denial/],
  ]) {
    const malformed = await parseChildFixture(fixture.exactRaw);
    mutate(malformed.value);
    assert.throws(
      () => validateChildHardCreditObservation(caseInput, [page], malformed, rest),
      reason,
    );
  }
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

test('tool-below-cap accepts two distinct ordered reservations across pages and preserves other rows', () => {
  const fixture = twoSlotObservation();
  const caseInput = validateInput({ ...input, expectedMaxToolCalls: 2 });
  const summary = validateObservation(caseInput, fixture.pages, fixture.exact, fixture.restExact);
  const rows = summary.numericMatrix.rows;
  assert.equal(rows.find(row => row.id === 'tool-below-cap').status, 'passed');
  assert.equal(rows.find(row => row.id === 'tool-at-cap').status, 'passed');
  assert.equal(rows.find(row => row.id === 'tool-next-denial').status, 'passed');
  assert.equal(summary.attemptId, fixture.exact.history.items[0].attemptId);
});

test('tool-below-cap blocks malformed reservation pairs without changing the legacy case pass', () => {
  const mutations = [
    ['zero count', fixture => {
      fixture.belowCap.observedValue = 0;
      fixture.belowCap.reservedCount = 0;
    }],
    ['duplicate attempt', fixture => {
      fixture.atCap.attemptId = fixture.belowCap.attemptId;
    }],
    ['mixed-case duplicate attempt', fixture => {
      fixture.belowCap.attemptId = 'abcdef00-0000-4000-8000-000000000011';
      fixture.atCap.attemptId = 'ABCDEF00-0000-4000-8000-000000000011';
    }],
    ['missing project ID', fixture => {
      delete fixture.belowCap.projectId;
    }],
    ['foreign project ID', fixture => {
      fixture.belowCap.projectId = 'other-project';
    }],
    ['missing run ID', fixture => {
      delete fixture.belowCap.runId;
    }],
    ['foreign run ID', fixture => {
      fixture.belowCap.runId = 'other-run';
    }],
    ['stale reservation', fixture => {
      fixture.belowCap.executionFence = 1;
    }],
    ['foreign reservation', fixture => {
      fixture.belowCap.sessionId = 'other-child-session';
    }],
    ['missing sequence', fixture => {
      delete fixture.belowCap.sequence;
    }],
    ['invalid sequence', fixture => {
      fixture.belowCap.sequence = 1.5;
    }],
    ['missing watermark', fixture => {
      delete fixture.pages[0].history.highWatermark;
      delete fixture.pages[1].history.highWatermark;
    }],
    ['invalid watermark', fixture => {
      fixture.pages[0].history.highWatermark = 0;
      fixture.pages[1].history.highWatermark = 0;
    }],
    ['sequence above watermark', fixture => {
      fixture.atCap.sequence = 16;
    }],
    ['reversed traversal', fixture => {
      fixture.pages[0].history.items[0] = structuredClone(fixture.atCap);
      fixture.pages[1].history.items[0] = structuredClone(fixture.belowCap);
      fixture.pages[0].history.nextCursor = 'second-page';
    }],
    ['reversed sequences', fixture => {
      fixture.atCap.sequence = 6;
    }],
  ];
  for (const [name, mutate] of mutations) {
    const fixture = twoSlotObservation();
    mutate(fixture);
    const caseInput = validateInput({ ...input, expectedMaxToolCalls: 2 });
    const summary = validateObservation(caseInput, fixture.pages, fixture.exact, fixture.restExact);
    assert.equal(summary.numericMatrix.rows.find(row => row.id === 'tool-below-cap').status,
      'blocked', name);
    assert.equal(summary.numericMatrix.rows.find(row => row.id === 'tool-at-cap').status,
      'passed', name);
    assert.equal(summary.numericMatrix.rows.find(row => row.id === 'tool-next-denial').status,
      'passed', name);
    assert.equal(summary.attemptId, fixture.exact.history.items[0].attemptId, name);
  }
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

test('tool evidence rejects old fence or selection facts despite current run-wide totals', () => {
  for (const [field, value, reason] of [
    ['executionFence', 1, /stale or different execution fence/],
    ['acceptedSelectionHash', otherSelectionHash, /another accepted selection/],
    ['sessionId', 'other-child-session', /another session/],
  ]) {
    const { body, exact, restExact } = observation();
    for (const fact of body.history.items) fact[field] = value;
    assert.equal(body.totals.toolSlotsReserved.value, input.expectedMaxToolCalls);
    assert.throws(
      () => validateObservation(validateInput(input), [body], exact, restExact),
      reason,
      field,
    );
    assert.throws(
      () => exactLookupFact(validateInput(input), [body]),
      reason,
      `${field} exact lookup selection`,
    );
  }
});

test('tool evidence rejects a current rejection when its admitted reservation is old', () => {
  const { body, exact, restExact } = observation();
  body.history.items[0].executionFence = 1;
  assert.throws(
    () => validateObservation(validateInput(input), [body], exact, restExact),
    /admitted tool reservation.*current authority|stale or different execution fence/,
  );
});

test('tool exact selection skips old facts and selects a later current pair', () => {
  const { body } = observation();
  const [reservation, rejection] = body.history.items;
  const oldReservation = { ...structuredClone(reservation), executionFence: 1 };
  const oldRejection = {
    ...structuredClone(rejection),
    factId: '00000000-0000-4000-8000-000000000016',
    attemptId: '00000000-0000-4000-8000-000000000017',
    dispatchId: '00000000-0000-4000-8000-000000000018',
    executionFence: 1,
  };
  const currentReservation = {
    ...structuredClone(reservation),
    factId: '00000000-0000-4000-8000-000000000023',
    attemptId: '00000000-0000-4000-8000-000000000019',
  };
  const currentRejection = {
    ...structuredClone(rejection),
    factId: '00000000-0000-4000-8000-000000000020',
    attemptId: '00000000-0000-4000-8000-000000000021',
    dispatchId: '00000000-0000-4000-8000-000000000022',
  };
  body.history.items = [oldReservation, oldRejection, currentReservation, currentRejection];
  const exact = {
    binding: structuredClone(body.binding),
    history: {
      exactAttempt: true,
      complete: true,
      items: [currentRejection],
      nextCursor: null,
      exactUnavailableReason: null,
    },
  };
  const summary = validateObservation(validateInput(input), [body], exact, structuredClone(exact));
  assert.equal(exactLookupFact(validateInput(input), [body]).attemptId, currentRejection.attemptId);
  assert.equal(summary.attemptId, currentRejection.attemptId);
});

test('tool exact lookups reject stale and foreign bindings even when the fact matches', () => {
  for (const [field, value, reason] of [
    ['projectId', 'other-project', /binding.*current authority|response binding/i],
    ['runId', 'other-run', /binding.*current authority|response binding/i],
    ['currentExecutionFence', 1, /binding.*current authority|response binding/i],
    ['acceptedSelectionHash', otherSelectionHash, /binding.*current authority|response binding/i],
    ['sessionId', 'other-child-session', /binding.*current authority|response binding/i],
  ]) {
    const { body, exact, restExact } = observation();
    exact.binding[field] = value;
    assert.throws(
      () => validateObservation(validateInput(input), [body], exact, restExact),
      reason,
      `API exact response ${field}`,
    );

    const rest = observation();
    rest.restExact.binding[field] = value;
    assert.throws(
      () => validateObservation(validateInput(input), [rest.body], rest.exact, rest.restExact),
      reason,
      `REST exact response ${field}`,
    );
  }
});

test('tool history rejects mixed bindings even when snapshot and high-water match', () => {
  const { body, exact, restExact } = observation();
  const secondPage = structuredClone(body);
  body.history.items = [body.history.items[0]];
  body.history.nextCursor = 'opaque-cursor';
  secondPage.history.items = [secondPage.history.items[1]];
  secondPage.history.nextCursor = null;
  secondPage.binding.currentExecutionFence += 1;
  assert.equal(secondPage.history.snapshotId, body.history.snapshotId);
  assert.equal(secondPage.history.highWatermark, body.history.highWatermark);
  assert.throws(
    () => validateObservation(validateInput(input), [body, secondPage], exact, restExact),
    /binding.*current authority|response binding/i,
  );
});

test('tool evidence allows model-turn reservations, later observations, and unavailable tool execution evidence', () => {
  const { body, exact, restExact } = observation();
  body.observedAt = '2026-11-14T00:00:00Z';
  exact.observedAt = '2026-11-14T00:00:01Z';
  restExact.observedAt = '2026-11-14T00:00:02Z';
  body.history.items.push({
    factId: '00000000-0000-4000-8000-000000000024',
    kind: 'modelTurnReserved',
    limitKind: 'modelTurns',
    sessionId: input.target.sessionId,
    executionFence: 2,
    acceptedSelectionHash: selectionHash,
    attemptId: '00000000-0000-4000-8000-000000000025',
  });
  assert.equal(body.history.items[1].nativeNoSend.state, 'unavailable');
  assert.equal(body.history.items[1].toolExecution.state, 'unavailable');
  assert.equal(validateObservation(validateInput(input), [body], exact, restExact).attemptId,
    body.history.items[1].attemptId);
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

test('child hard-credit CLI collection preserves exact tokens and saves no raw observation output', async () => {
  const scratch = path.join(directory, `child-evidence-${randomUUID()}`);
  const cliPath = path.join(scratch, process.platform === 'win32' ? 'agentweaver.exe' : 'agentweaver');
  const casePath = path.join(scratch, 'case.json');
  const outputPath = path.join(scratch, 'evidence');
  await mkdir(scratch);
  try {
    await writeFile(cliPath, 'controlled CLI fixture');
    const apphostSha256 = createHash('sha256').update(await readFile(cliPath)).digest('hex');
    await writeFile(casePath, JSON.stringify({
      ...childHardCreditInput,
      expectedRelation: 'overshoot',
      apphostSha256,
    }));
    const fixture = childHardCreditObservation({ relation: 'overshoot' });
    const firstPage = JSON.parse(fixture.pageRaw);
    firstPage.body.history.items = [];
    firstPage.body.history.nextCursor = 'opaque-cursor';
    const firstPageRaw = JSON.stringify(firstPage);
    const response = (stdout = '{}') => ({
      startedAt: '2026-11-25T00:00:00.000Z',
      completedAt: '2026-11-25T00:00:00.001Z',
      durationMs: 1,
      exitCode: 0,
      signal: null,
      stdout,
      stderr: '',
      timedOut: false,
      error: null,
    });
    const api = body => JSON.stringify({ status: 200, headers: {}, evidence: {}, body });
    const context = {
      actorId: childHardCreditInput.expectedActorId,
      tenantId: childHardCreditInput.target.tenantId,
      boundProjectId: childHardCreditInput.target.projectId,
      boundRunId: childHardCreditInput.target.runId,
    };
    const runner = (_cli, args) => {
      if (args[0] === '--version') return response('fixture');
      if (args[0] === 'auth') return response(JSON.stringify({ isSignedIn: true, tenantId: context.tenantId }));
      if (args[0] === 'api' && args[1] === 'operations') return response('{}');
      if (args[0] === 'api' && args[1] === 'describe') return response('{}');
      if (args[0] === 'api' && args[1] === 'invoke') {
        if (args.includes('readRuntimeObservationV2')) {
          const cursor = args.some(value => value === 'cursor=opaque-cursor');
          const exact = args.some(value => value === `attemptId=${childHardCreditInput.attemptId}`);
          return response(exact
            ? `{"status":200,"headers":{},"body":${fixture.exactRaw}}`
            : cursor ? fixture.pageRaw : firstPageRaw);
        }
        return response(api({}));
      }
      if (args.includes('/api/v1/authorization/context')) {
        return response(JSON.stringify({ status: 200, body: context }));
      }
      if (args.includes('GET') && args.some(value => value.includes('runtime-observation/v2'))) {
        return response(fixture.restRaw);
      }
      throw new Error(`Unexpected controlled command: ${args.join(' ')}`);
    };
    const result = await runCase(
      { inputPath: casePath, outputPath, dryRun: false },
      { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
      runner,
    );
    assert.equal(result.status, 'passed', result.reason);
    assert.equal(result.pagesRead, 2);
    assert.equal(result.creditNumberSources.observedValue, '1000000000.000000001');

    const persisted = JSON.parse(await readFile(path.join(outputPath, 'result.json'), 'utf8'));
    assert.deepEqual(persisted.creditNumberSources, result.creditNumberSources);
    const records = (await readFile(path.join(outputPath, 'commands.jsonl'), 'utf8'))
      .trim().split('\n').map(line => JSON.parse(line));
    const observations = records.filter(record => record.id.startsWith('observation-page-')
      || record.id.startsWith('observation-exact-') || record.id === 'rest-exact-attempt');
    assert.equal(observations.length, 4);
    assert.ok(observations.every(record => !Object.hasOwn(record, 'stdout')
      && !Object.hasOwn(record, 'stderr')));
    assert.ok(observations.every(record => Array.isArray(record.creditNumberSources)));
    const denialRecords = observations.filter(record =>
      record.creditNumberSources.some(item => item.path.endsWith('.observedValue')));
    assert.equal(denialRecords.length, 3);
    assert.ok(denialRecords.every(record => record.creditNumberSources.some(item =>
      item.path.endsWith('.observedValue') && item.source === '1000000000.000000001')));
    const cursorRecord = records.find(record => record.id === 'observation-page-2');
    assert.ok(cursorRecord.args.includes('cursor=[REDACTED]'));
    assert.equal(records.find(record => record.id === 'observation-page-1')
      .response.body.history.nextCursor, '[REDACTED]');

    for (const [name, mutate] of [
      ['paged-literal-duplicate', record => {
        record.stdout = record.stdout.replace(
          '"configuredValue":1000000000',
          '"configuredValue":1000000000,"configuredValue":1000000000',
        );
      }],
      ['exact-escaped-duplicate', record => {
        record.stdout = record.stdout.replace(
          '"configuredValue":1000000000',
          '"configuredValue":1000000000,"configured\\u0056alue":1000000000',
        );
      }],
    ]) {
      const malformedOutput = path.join(scratch, name);
      const malformedRunner = (_cli, args) => {
        const record = runner(_cli, args);
        const paged = args.some(value => value === 'cursor=opaque-cursor');
        const exact = args.some(value => value === `attemptId=${childHardCreditInput.attemptId}`);
        if (name === 'paged-literal-duplicate' && paged || name === 'exact-escaped-duplicate' && exact) {
          mutate(record);
        }
        return record;
      };
      const blocked = await runCase(
        { inputPath: casePath, outputPath: malformedOutput, dryRun: false },
        { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
        malformedRunner,
      );
      assert.equal(blocked.status, 'blocked');
      assert.match(blocked.reason, /strict JSON with unique keys/);
      const blockedRecords = (await readFile(
        path.join(malformedOutput, 'commands.jsonl'), 'utf8',
      )).trim().split('\n').map(line => JSON.parse(line));
      const blockedObservations = blockedRecords.filter(item =>
        item.id.startsWith('observation-page-')
        || item.id === 'observation-exact-attempt'
        || item.id === 'rest-exact-attempt');
      assert.ok(blockedObservations.length > 0);
      assert.ok(blockedObservations.every(item => !Object.hasOwn(item, 'stdout')
        && !Object.hasOwn(item, 'stderr')));
    }

    const opaqueOutput = path.join(scratch, 'nested-malformed-cursor');
    const opaqueRunner = (_cli, args) => {
      const record = runner(_cli, args);
      if (args.includes('readRuntimeObservationV2')
        && args.some(value => value === 'cursor=opaque-cursor')) {
        const payload = JSON.parse(record.stdout);
        payload.body.history.nextCursor = {
          opaque: 'opaque-cursor-secret',
          nested: {
            token: 'nested-cursor-secret',
            configuredValue: 123456789,
          },
        };
        record.stdout = JSON.stringify(payload);
      }
      return record;
    };
    const opaque = await runCase(
      { inputPath: casePath, outputPath: opaqueOutput, dryRun: false },
      { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
      opaqueRunner,
    );
    assert.equal(opaque.status, 'blocked');
    const opaqueCommandText = await readFile(
      path.join(opaqueOutput, 'commands.jsonl'), 'utf8',
    );
    assert.doesNotMatch(opaqueCommandText, /opaque-cursor-secret|nested-cursor-secret/);
    assert.match(opaqueCommandText, /"nextCursor":"\[REDACTED\]"/);
    assert.doesNotMatch(opaqueCommandText, /configuredValue","source":"123456789/);

    const belowOutput = path.join(scratch, 'pinned-below-limit');
    const belowFixture = childHardCreditObservation({ observedToken: '999999999.999999999' });
    const otherDenial = JSON.parse(belowFixture.pageRaw);
    const other = structuredClone(otherDenial.body.history.items[0]);
    other.factId = '00000000-0000-4000-8000-000000000450';
    other.sequence = 2;
    other.ownerDecision.receiptId = other.factId;
    other.attemptId = '00000000-0000-4000-8000-000000000451';
    other.dispatchId = other.attemptId;
    other.observedValue = 1_000_000_000;
    otherDenial.body.history.items.push(other);
    otherDenial.body.history.highWatermark = 2;
    const belowRunner = (_cli, args) => {
      const record = runner(_cli, args);
      if (args.includes('readRuntimeObservationV2')
        && !args.some(value => value.startsWith('cursor=')
          || value.startsWith(`attemptId=${childHardCreditInput.attemptId}`))) {
        const payload = JSON.parse(firstPageRaw);
        payload.body.history.highWatermark = 2;
        record.stdout = JSON.stringify(payload);
      }
      if (args.includes('readRuntimeObservationV2')
        && args.some(value => value === 'cursor=opaque-cursor')) {
        record.stdout = belowFixture.pageRaw
          .replace('"highWatermark":1', '"highWatermark":2')
          .replace('],"nextCursor":null', `,${JSON.stringify(other)}],"nextCursor":null`);
      }
      if (args.includes('readRuntimeObservationV2')
        && args.some(value => value === `attemptId=${childHardCreditInput.attemptId}`)) {
        record.stdout = `{"status":200,"headers":{},"body":${belowFixture.exactRaw}}`;
      }
      if (args.includes('GET') && args.some(value => value.includes('runtime-observation/v2'))) {
        record.stdout = `{"status":200,"body":${belowFixture.exactRaw}}`;
      }
      return record;
    };
    const belowCasePath = path.join(scratch, 'below-case.json');
    await writeFile(belowCasePath, JSON.stringify({
      ...childHardCreditInput,
      expectedRelation: 'equal',
      apphostSha256,
    }));
    const belowResult = await runCase(
      { inputPath: belowCasePath, outputPath: belowOutput, dryRun: false },
      { ...process.env, AGENTWEAVER_CLI_PATH: cliPath },
      belowRunner,
    );
    assert.equal(belowResult.status, 'blocked');
    assert.match(belowResult.reason, /below its configured value/);
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
  assert.equal(validateInput({ ...modelTurnInput, expectedMaxModelTurns: 10_000 }).expectedMaxModelTurns, 10_000);
  assert.throws(() => validateInput({ ...modelTurnInput, expectedMaxModelTurns: 10_001 }), /schema/);
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

test('no-send cases reject stale page and exact-response bindings', () => {
  for (const [caseInput, factory] of [
    [validateInput(modelTurnInput), modelTurnObservation],
    [validateInput(hardCreditInput), hardCreditObservation],
  ]) {
    const pageCase = factory();
    const firstPage = structuredClone(pageCase.body);
    const secondPage = structuredClone(pageCase.body);
    firstPage.history.items = firstPage.history.items.slice(0, 1);
    firstPage.history.nextCursor = 'opaque-cursor';
    secondPage.history.items = secondPage.history.items.slice(1);
    secondPage.history.nextCursor = null;
    secondPage.binding.acceptedSelectionHash = otherSelectionHash;
    assert.throws(
      () => validateObservation(caseInput, [firstPage, secondPage], pageCase.exact, pageCase.restExact),
      /Observation page 2 response binding.*another accepted selection/,
      `${caseInput.caseId} page binding`,
    );

    const exactCase = factory();
    exactCase.exact.binding.runId = 'other-run';
    assert.throws(
      () => validateObservation(caseInput, [exactCase.body], exactCase.exact, exactCase.restExact),
      /response binding.*another run/,
      `${caseInput.caseId} exact binding`,
    );

    const restCase = factory();
    restCase.restExact.binding.currentExecutionFence += 1;
    assert.throws(
      () => validateObservation(caseInput, [restCase.body], restCase.exact, restCase.restExact),
      /response binding.*execution fence/,
      `${caseInput.caseId} REST binding`,
    );
  }
});

test('hard-credit case passes on current-authority denial, no-send, and guard facts', () => {
  const caseInput = validateInput(hardCreditInput);
  const { body, exact, restExact } = hardCreditObservation();
  const summary = validateObservation(caseInput, [body], exact, restExact);
  assert.equal(summary.dispatchId, '00000000-0000-4000-8000-000000000301');
  assert.equal(summary.factId, body.history.items[4].factId);
  assert.equal(summary.softWarningObserved, true);
  assert.equal(summary.numericMatrix.rows.find(row => row.id === 'credit-below').status, 'passed');
  assert.equal(summary.numericMatrix.rows.find(row => row.id === 'credit-overshoot').status, 'passed');
  assert.equal(summary.numericMatrix.rows.find(row => row.id === 'credit-equality').status, 'blocked');
  assert.equal(summary.numericMatrix.rows.find(row => row.id === 'credit-warning-floor').status, 'passed');
  assert.equal(exactLookupFact(caseInput, [body]), body.history.items[4]);

  const equality = hardCreditObservation();
  equality.body.history.items[3].observedValue = equality.body.history.items[3].configuredValue;
  equality.body.history.items[4].observedValue = equality.body.history.items[4].configuredValue;
  equality.exact.history.items[0].observedValue = equality.exact.history.items[0].configuredValue;
  equality.exact.history.items[1].observedValue = equality.exact.history.items[1].configuredValue;
  equality.restExact = structuredClone(equality.exact);
  const equalitySummary = validateObservation(
    caseInput, [equality.body], equality.exact, equality.restExact,
  );
  assert.equal(equalitySummary.numericMatrix.rows.find(row => row.id === 'credit-equality').status, 'passed');
  assert.equal(equalitySummary.numericMatrix.rows.find(row => row.id === 'credit-overshoot').status, 'blocked');

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
    [5, /hard cap is exhausted/],
    [1, /expected soft warning/],
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
  noSoftWarning.body.history.items[1].softWarningEmitted = false;
  assert.throws(() => validateObservation(
    caseInput, [noSoftWarning.body], noSoftWarning.exact, noSoftWarning.restExact,
  ), /expected soft warning/);
  const notExpected = validateInput({ ...hardCreditInput, expectedSoftWarning: false });
  assert.equal(validateObservation(
    notExpected, [noSoftWarning.body], noSoftWarning.exact, noSoftWarning.restExact,
  ).softWarningObserved, false);
});

test('hard-credit matrix classifies decimal equality and overshoot from the current denial', () => {
  const caseInput = validateInput(hardCreditInput);
  for (const [observed, expected] of [
    [5_000_000_000, 'credit-equality'],
    [5_000_000_000.25, 'credit-overshoot'],
  ]) {
    const { body, exact, restExact } = hardCreditObservation();
    body.history.items[3].observedValue = observed;
    body.history.items[4].observedValue = observed;
    exact.history.items[0].observedValue = observed;
    exact.history.items[1].observedValue = observed;
    restExact.history.items[0].observedValue = observed;
    restExact.history.items[1].observedValue = observed;
    const summary = validateObservation(caseInput, [body], exact, restExact);
    assert.equal(summary.numericMatrix.rows.find(row => row.id === 'credit-equality').status,
      expected === 'credit-equality' ? 'passed' : 'blocked');
    assert.equal(summary.numericMatrix.rows.find(row => row.id === 'credit-overshoot').status,
      expected === 'credit-overshoot' ? 'passed' : 'blocked');
  }
});

test('numeric matrix reports distinct boundaries without changing legacy case status', () => {
  const tool = observation();
  const summary = validateObservation(
    validateInput(input), [tool.body], tool.exact, tool.restExact,
  );
  assert.equal(summary.attemptId, tool.body.history.items[1].attemptId);
  assert.equal(summary.numericMatrix.rows.find(row => row.id === 'tool-at-cap').status, 'passed');
  assert.equal(summary.numericMatrix.rows.find(row => row.id === 'tool-next-denial').status, 'passed');
  assert.equal(summary.numericMatrix.rows.find(row => row.id === 'tool-below-cap').status, 'blocked');
  assert.equal(summary.numericMatrix.complete, false);

  const model = modelTurnObservation();
  const modelSummary = validateObservation(
    validateInput(modelTurnInput), [model.body], model.exact, model.restExact,
  );
  assert.equal(modelSummary.numericMatrix.rows.find(row => row.id === 'model-turn-below-cap').status, 'passed');
  assert.equal(modelSummary.numericMatrix.rows.find(row => row.id === 'model-turn-at-cap').status, 'passed');
  assert.equal(modelSummary.numericMatrix.rows.find(row => row.id === 'model-turn-next-denial').status, 'passed');
});

test('numeric matrix rejects missing and duplicate rows', () => {
  const matrix = buildNumericMatrix();
  assert.throws(() => validateNumericMatrix(matrix.rows.slice(1)), /missing required rows/);
  assert.throws(() => validateNumericMatrix([
    ...matrix.rows.slice(1),
    { ...matrix.rows[1] },
  ]), /unknown or duplicate row/);
  const attemptedPromotion = buildNumericMatrix(
    Object.fromEntries(matrix.rows.map(row => [row.id, 'passed'])),
  );
  assert.equal(attemptedPromotion.complete, false);
  assert.equal(attemptedPromotion.rows.find(row => row.id === 'full-native-proof').status, 'blocked');
  assert.equal(attemptedPromotion.rows.find(row => row.id === 'credit-zero').status, 'blocked');
  const otherNumericPromotion = buildNumericMatrix(
    { 'credit-zero': 'passed' },
    { sourceCaseId: 'numeric-hard-credit-no-send' },
  );
  assert.equal(otherNumericPromotion.rows.find(row => row.id === 'credit-zero').status, 'blocked');
  const promotedCreditZero = matrix.rows.map(row => row.id === 'credit-zero'
    ? { ...row, status: 'passed' }
    : row);
  assert.throws(
    () => validateNumericMatrix(promotedCreditZero, { sourceCaseId: 'numeric-hard-credit-no-send' }),
    /cannot claim passed/,
  );
  const dedicatedCreditZero = buildNumericMatrix(
    { 'credit-zero': 'passed' },
    { sourceCaseId: 'workflow-generation-credit-zero' },
  );
  assert.equal(dedicatedCreditZero.rows.find(row => row.id === 'credit-zero').status, 'passed');
  const promotedFixedRow = matrix.rows.map(row => row.id === 'full-native-proof'
    ? { ...row, status: 'passed' }
    : row);
  assert.throws(() => validateNumericMatrix(promotedFixedRow), /cannot claim passed/);
  assert.throws(() => validateNumericMatrix(
    matrix.rows.map((row, index) => index === 0 ? { ...row, status: 'partial' } : row),
  ), /invalid status/);
});

test('numeric boundary comparisons keep equality separate from overshoot', () => {
  assert.equal(compareBoundary(5, 5), 'equal');
  assert.equal(compareBoundary(6, 5), 'overshoot');
  assert.equal(compareBoundary(4, 5), 'below');
  assert.equal(compareBoundary(5.25, 5.25), 'equal');
  assert.equal(compareBoundary(5.5, 5.25), 'overshoot');
  assert.equal(compareBoundary(5, 5.25), 'below');
  assert.equal(compareBoundary(null, 5), null);
  assert.equal(compareBoundary(Number.MAX_SAFE_INTEGER + 1, 5), null);
});

test('credit warning-floor proof requires ordered warning transition and later floor advance', () => {
  const { body } = hardCreditObservation();
  const guards = body.history.items.filter(fact => fact.kind === 'creditGuardChanged');
  assert.equal(warningFloorTransition(guards, 4_000_000_000), true);
  assert.equal(warningFloorTransition([
    { sequence: 1, softWarningEmitted: false, observedValue: 4.25, unit: 'nanoAiu', scope: 'copilotRun', accountedThroughLedgerEventCount: 1, ownerRevision: 1 },
    { sequence: 2, softWarningEmitted: true, observedValue: 4.5, unit: 'nanoAiu', scope: 'copilotRun', accountedThroughLedgerEventCount: 2, ownerRevision: 2 },
    { sequence: 3, softWarningEmitted: true, observedValue: 4.75, unit: 'nanoAiu', scope: 'copilotRun', accountedThroughLedgerEventCount: 3, ownerRevision: 3 },
  ], 4.5), true);
  const wrongUnit = structuredClone(guards);
  wrongUnit[1].scope = 'project';
  assert.equal(warningFloorTransition(wrongUnit, 4_000_000_000), false);
  assert.equal(warningFloorTransition([
    { sequence: 1, softWarningEmitted: false, observedValue: 4, unit: 'nanoAiu', scope: 'copilotRun', accountedThroughLedgerEventCount: 1, ownerRevision: 1 },
    { sequence: 2, softWarningEmitted: true, observedValue: 5, unit: 'nanoAiu', scope: 'copilotRun', accountedThroughLedgerEventCount: 2, ownerRevision: 2 },
  ], 5), false);
  assert.equal(warningFloorTransition([
    { sequence: 1, softWarningEmitted: false, observedValue: 4, unit: 'nanoAiu', scope: 'copilotRun', accountedThroughLedgerEventCount: 1, ownerRevision: 1 },
    { sequence: 2, softWarningEmitted: true, observedValue: 5, unit: 'nanoAiu', scope: 'copilotRun', accountedThroughLedgerEventCount: 2, ownerRevision: 2 },
    { sequence: 3, softWarningEmitted: true, observedValue: 6, unit: 'nanoAiu', scope: 'copilotRun', accountedThroughLedgerEventCount: 2, ownerRevision: 3 },
  ], 5), false);
});
