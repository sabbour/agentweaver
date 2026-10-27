import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { numericDenialPayloadHash } from '../numeric-denials.mjs';
import { runP1DispatchRecoveryCli, runP1DispatchRecoveryProbes } from '../run.mjs';
import { validateP1NativeProbeResults } from '../../../harness-shared/p1-acceptance-result.mjs';

// Captured from producer-vectors.cs using the actual C# Seal and HTTP serializer.
const vectors = JSON.parse(readFileSync(new URL('./producer-vectors.json', import.meta.url), 'utf8'));
const otherId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const otherHash = 'c'.repeat(64);

function pinFor(receipt) {
  const expected = Object.fromEntries(Object.entries(receipt).filter(([key]) => ![
    'contractVersion', 'receiptId', 'observedCount', 'sendStateBeforeHash',
    'sendStateAfterHash', 'recordedAt', 'canonicalPayloadHash',
  ].includes(key)));
  return {
    expected,
    recordedAfter: '2026-10-27T19:59:59Z',
    recordedBefore: '2026-10-27T20:00:01Z',
  };
}

function reseal(receipt) {
  return { ...receipt, canonicalPayloadHash: numericDenialPayloadHash(receipt) };
}

async function runCase(receipt, { pin = pinFor(vectors[1].receipt), result, input } = {}) {
  const calls = [];
  const name = pin.expected.limitKind === 'ModelTurns' ? 'modelTurns' : 'toolCalls';
  const report = await runP1DispatchRecoveryProbes({
    numericDenialInput: input ?? { [name]: pin },
    numericDenialDependencies: {
      ownerCaller: {
        async postJson(path, body) {
          calls.push({ path, body });
          assert.equal(path, '/internal/runtime/actions/numeric-denials/read');
          assert.deepEqual(body, {
            runtimeInstanceId: pin.expected.runtimeInstanceId,
            registrationRevision: pin.expected.registrationRevision,
            executionFence: pin.expected.executionFence,
            attemptId: pin.expected.attemptId,
          });
          return result === undefined ? { receipt } : result;
        },
      },
    },
  });
  assert.equal(report.overallStatus, 'blocked');
  assert.equal(report.probes.length, 2);
  assert.ok(report.probes.every(probe => probe.status === 'blocked' &&
    probe.evidence.acceptanceEvidence === false && probe.cleanup.status === 'not-started'));
  assert.doesNotThrow(() => validateP1NativeProbeResults([report]));
  return { report, calls, item: report.liveObservations.numericDenials.cases?.[name] };
}

test('real C# producer vectors hash exactly, including nullable checkpoint and escaped grant revision', () => {
  assert.equal(vectors.length, 4);
  const expectedHashes = [
    '2837058399f81c095125a708dd5ab0d9ce101ac514ba357946d1200a49b2b40f',
    '1fb19f2da2f6d9e7b094a69eaede33b5d1acbc2d713c2bc64e34f59a55cd4643',
    '602bee16663bfc07c76ca1a467163b5452a5151fa61c6abc942d2a57e4d3e1ba',
    '0f85121ad647c5e6d5369a25a08bd1b66acbd29f469a0349c1649021217a9203',
  ];
  for (const [index, vector] of vectors.entries()) {
    assert.equal(vector.receipt.canonicalPayloadHash, expectedHashes[index]);
    assert.equal(createHash('sha256').update(vector.canonicalPayload).digest('hex'), expectedHashes[index]);
    assert.equal(numericDenialPayloadHash(vector.receipt), expectedHashes[index]);
    assert.notEqual(createHash('sha256')
      .update(JSON.stringify({ ...vector.receipt, canonicalPayloadHash: '' })).digest('hex'), expectedHashes[index]);
  }
});

test('producer-shaped denials are partial owner evidence only and call only the fixed read route', async () => {
  for (const { receipt } of vectors) {
    const { report, calls, item } = await runCase(receipt, { pin: pinFor(receipt) });
    assert.equal(calls.length, 1);
    assert.equal(item.status, 'observed');
    assert.deepEqual(item.evidence.receipt, receipt);
    assert.equal(item.evidence.persistedSendStateUnchanged, true);
    assert.equal(item.evidence.nativeNoSendProven, false);
    assert.equal(item.evidence.acceptanceEvidence, false);
    assert.equal(report.liveObservations.numericDenials.status, 'partial');
    assert.equal(report.probes[0].evidence.observations[0].kind,
      'partial-orchestrator-numeric-denial-read-results');
    assert.ok(report.probes[0].evidence.requiredReceipts.some(required =>
      required.receipt === 'native-turn-observations'));
  }
});

test('both numeric readers preserve separate partial results without diluting the required slots', async () => {
  const model = vectors[1].receipt;
  const tool = vectors[3].receipt;
  let count = 0;
  const report = await runP1DispatchRecoveryProbes({
    numericDenialInput: { modelTurns: pinFor(model), toolCalls: pinFor(tool) },
    numericDenialDependencies: {
      ownerCaller: { async postJson() { return { receipt: count++ === 0 ? model : tool }; } },
    },
  });
  assert.equal(count, 2);
  assert.ok(Object.values(report.liveObservations.numericDenials.cases).every(item => item.status === 'observed'));
  assert.equal(report.overallStatus, 'blocked');
  assert.ok(report.probes.every(probe => probe.status === 'blocked' && !probe.evidence.acceptanceEvidence));
});

test('a missing or invalid sibling never erases a genuine partial receipt', async () => {
  for (const second of [{ receipt: null }, { status: 'passed' }]) {
    let count = 0;
    const report = await runP1DispatchRecoveryProbes({
      numericDenialInput: { modelTurns: pinFor(vectors[1].receipt), toolCalls: pinFor(vectors[3].receipt) },
      numericDenialDependencies: {
        ownerCaller: { async postJson() { return count++ === 0 ? { receipt: vectors[1].receipt } : second; } },
      },
    });
    assert.equal(report.liveObservations.numericDenials.cases.modelTurns.status, 'observed');
    assert.equal(report.liveObservations.numericDenials.cases.toolCalls.status, 'blocked');
    assert.equal(report.overallStatus, 'blocked');
    assert.equal(report.probes[0].evidence.acceptanceEvidence, false);
  }
});

test('model/tool denial thresholds include the exact accepted limit, not the preceding count', async () => {
  for (const limitKind of ['ModelTurns', 'ToolCalls']) {
    for (const acceptedLimit of [1, 2, limitKind === 'ModelTurns' ? 1000 : 10000]) {
      for (const delta of [-1, 0, 1]) {
        const receipt = reseal({ ...vectors[1].receipt, limitKind, acceptedLimit, observedCount: acceptedLimit + delta });
        const { item } = await runCase(receipt, { pin: pinFor(receipt) });
        assert.equal(item.status, delta < 0 ? 'blocked' : 'observed', `${limitKind}:${acceptedLimit}:${delta}`);
      }
    }
  }
});

test('resealed wrong attempt, owner, source, grant, selection, fence, limit and checkpoint are rejected', async () => {
  const changes = {
    projectId: 'other', runId: 'other', rootSessionId: 'other', sessionId: 'other',
    runtimeInstanceId: otherId, registrationRevision: 8, sourceRegistrationRevision: 8,
    sourceGrantId: otherId, sourceHash: otherHash, grantId: otherId, grantRevision: 'other',
    executionFence: 10, acceptedSelectionHash: otherHash, ownerRevision: 4, limitKind: 'ToolCalls',
    acceptedLimit: 1, attemptId: otherId, requestHash: otherHash, dispatchId: otherId,
    checkpointId: 'other', checkpointRevision: 5, checkpointHash: otherHash,
    checkpointDecisionStateVersion: 6, workPlanId: 'other', associationId: 'other',
  };
  for (const [key, value] of Object.entries(changes)) {
    const { item } = await runCase(reseal({ ...vectors[1].receipt, [key]: value }));
    assert.equal(item.status, 'blocked', key);
    assert.equal(item.evidence, undefined, key);
  }
});

test('malformed, incomplete, unsafe numeric, changed-send-state and bad hash evidence stays blocked', async () => {
  const original = vectors[1].receipt;
  for (const receipt of [
    { ...original, canonicalPayloadHash: otherHash },
    { ...original, unexpected: true },
    { ...original, contractVersion: 2 },
    reseal({ ...original, sendStateAfterHash: otherHash }),
    reseal({ ...original, observedCount: Number.MAX_SAFE_INTEGER + 1 }),
    reseal({ ...original, checkpointId: null }),
    reseal({ ...original, checkpointDecisionStateVersion: 0 }),
    reseal({ ...original, dispatchId: '00000000-0000-0000-0000-000000000000' }),
    reseal({ ...original, receiptId: 'not-a-guid' }),
    reseal({ ...original, sourceHash: 'B'.repeat(64) }),
    ...Object.keys(original).map(key => Object.fromEntries(Object.entries(original).filter(([name]) => name !== key))),
  ]) {
    const { item } = await runCase(receipt);
    assert.equal(item.status, 'blocked');
    assert.equal(item.evidence, undefined);
  }
  for (const result of [{}, { receipt: null }, { status: 'passed' }, { receipt: original, status: 'passed' }]) {
    const { item } = await runCase(null, { result });
    assert.equal(item.status, 'blocked');
  }
});

test('timestamps preserve .NET ticks, normalize zero fractions, and reject stale, future and invalid dates', async () => {
  const original = vectors[1].receipt;
  for (const recordedAt of ['2026-10-27T19:59:58Z', '2026-10-27T20:00:02Z']) {
    const { item } = await runCase(reseal({ ...original, recordedAt }));
    assert.equal(item.status, 'blocked');
  }
  for (const recordedAt of ['invalid', '2026-02-30T20:00:00Z', '2026-10-27T24:00:00Z',
    '2026-10-27T20:00:00.12345678Z', '2026-10-27T20:00:00+14:01']) {
    const { item } = await runCase({ ...original, recordedAt });
    assert.equal(item.status, 'blocked');
  }
  const receipt = vectors[0].receipt;
  assert.equal(numericDenialPayloadHash({ ...receipt, recordedAt: '2026-10-27T20:00:00.0000000Z' }),
    receipt.canonicalPayloadHash);
  const pin = pinFor(original);
  pin.recordedBefore = '2026-10-27T20:00:00.1234566Z';
  assert.equal((await runCase(original, { pin })).item.status, 'blocked');
  pin.recordedBefore = '2026-10-27T20:00:00.1234567Z';
  assert.equal((await runCase(original, { pin })).item.status, 'observed');
  const future = reseal({ ...original, recordedAt: '9999-01-01T00:00:00Z' });
  const futurePin = pinFor(future);
  futurePin.recordedBefore = '9999-12-31T23:59:59Z';
  assert.equal((await runCase(future, { pin: futurePin })).item.status, 'blocked');
});

test('invalid lookup pins and caller-supplied evidence are rejected before the first read', async () => {
  const good = { modelTurns: pinFor(vectors[1].receipt) };
  for (const input of [
    {}, null, { status: 'passed' }, { ...good, receipt: vectors[1].receipt },
    { modelTurns: { ...good.modelTurns, bearerToken: 'credential-sentinel' } },
    { modelTurns: { ...good.modelTurns, recordedAfter: 'invalid' } },
    { modelTurns: { ...good.modelTurns, expected: { ...good.modelTurns.expected, status: 'passed' } } },
    { modelTurns: { ...good.modelTurns, expected: { ...good.modelTurns.expected, sourceHash: null } } },
    { modelTurns: pinFor(vectors[3].receipt) },
  ]) {
    const { report, calls } = await runCase(null, { input: input === null ? [] : input });
    assert.equal(calls.length, 0);
    assert.equal(report.liveObservations.numericDenials.code, 'numeric_denial_input_invalid');
    assert.equal(JSON.stringify(report).includes('credential-sentinel'), false);
  }
});

test('missing or failed authenticated dependencies cannot turn checks into passes or leak credentials', async () => {
  for (const numericDenialDependencies of [
    undefined, {}, { ownerCaller: {} },
    { ownerCaller: { async postJson() { throw new Error('Bearer credential-sentinel'); } } },
  ]) {
    const report = await runP1DispatchRecoveryProbes({
      numericDenialInput: { modelTurns: pinFor(vectors[1].receipt) },
      numericDenialDependencies,
    });
    assert.equal(report.overallStatus, 'blocked');
    assert.equal(JSON.stringify(report).includes('credential-sentinel'), false);
    assert.equal(report.probes[0].evidence.observed, false);
  }
});

test('CLI wires only numeric lookup pins to provider clients and rejects prebuilt results before import', async t => {
  const directory = await mkdtemp(join(tmpdir(), 'numeric-denial-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const inputPath = join(directory, 'input.json');
  const providerPath = join(directory, 'provider.mjs');
  await writeFile(inputPath, JSON.stringify({ numericDenialInput: { modelTurns: pinFor(vectors[1].receipt) } }));
  await writeFile(providerPath, `export function createP1DispatchRecoveryDependencies({ input }) {
    if (!input.numericDenialInput) throw new Error('missing lookup');
    return { numericDenialDependencies: { ownerCaller: { async postJson(path, body) {
      if (path !== '/internal/runtime/actions/numeric-denials/read' ||
          body.attemptId !== input.numericDenialInput.modelTurns.expected.attemptId) throw new Error('wrong route');
      return ${JSON.stringify({ receipt: vectors[1].receipt })};
    } } } };
  }`);
  const output = [];
  const errors = [];
  const io = { log: value => output.push(value), error: value => errors.push(value) };
  assert.equal(await runP1DispatchRecoveryCli(['--input', inputPath, '--provider-module', providerPath], io), 2);
  assert.deepEqual(errors, []);
  assert.equal(JSON.parse(output[0]).liveObservations.numericDenials.cases.modelTurns.status, 'observed');
  await writeFile(inputPath, JSON.stringify({ numericDenialInput: { receipt: vectors[1].receipt } }));
  assert.equal(await runP1DispatchRecoveryCli(['--input', inputPath, '--provider-module', 'missing.mjs'], io), 2);
  assert.match(errors.pop(), /lookup pins/);
  await writeFile(inputPath, JSON.stringify({ bearerToken: 'credential-sentinel' }));
  assert.equal(await runP1DispatchRecoveryCli(['--input', inputPath, '--provider-module', 'missing.mjs'], io), 2);
  assert.match(errors.pop(), /unknown fields/);
  await writeFile(inputPath, JSON.stringify({ recoveryContext: { owner: { accessToken: 'credential-sentinel' } } }));
  assert.equal(await runP1DispatchRecoveryCli(['--input', inputPath, '--provider-module', 'missing.mjs'], io), 2);
  assert.match(errors.pop(), /Credentials must not/);
});
