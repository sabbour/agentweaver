import assert from 'node:assert/strict';
import test from 'node:test';
import { createP1AcceptanceResult } from '../p1-acceptance-result.mjs';

const names = [
  'storage-owner-isolation',
  'network-policy-datapath',
  'sandbox-owner-lifecycle',
  'agenthost-image-readiness',
  'numeric-copilot-dispatch-boundaries',
  'checkpoint-restart-duplicate-fence',
];

function envelope(kind, probeNames, status = 'passed') {
  const probes = probeNames.map(name => ({
    name,
    status,
    reason: status === 'passed' ? null : 'controlled source blocker',
    evidence: {
      kind: 'controlled',
      requiredReceipts: [{ owner: 'controlled-owner', receipt: 'controlled-receipt', requiredFacts: ['fact'] }],
      sourceGates: [],
      observed: status === 'passed',
      observations: [],
    },
    cleanup: { status: 'not-started', createdResourceIds: [] },
  }));
  return {
    kind,
    schemaVersion: 1,
    overallStatus: status,
    startedAt: '2026-10-13T00:00:00.000Z',
    completedAt: '2026-10-13T00:00:01.000Z',
    probes,
  };
}

function nativeProbeResults(status = 'passed') {
  return [
    envelope('p1-native-acceptance', names.slice(0, 4), status),
    envelope('p1-dispatch-recovery-acceptance', names.slice(4), status),
  ];
}

test('preserves the exact native evidence and cleanup while aggregating all six required probe IDs', () => {
  const results = nativeProbeResults('blocked');
  const evidence = results[1].probes[0].evidence;
  const result = createP1AcceptanceResult({
    surface: 'api',
    surfaceStatus: 'passed',
    sourceEvidence: { services: { status: 'passed' }, agentHostImageMeasurement: { status: 'passed' } },
    nativeProbeResults: results,
  });

  assert.equal(result.fullP1Status, 'blocked');
  assert.deepEqual(result.probes.filter(probe => names.includes(probe.id)).map(probe => probe.id).sort(), [...names].sort());
  assert.deepEqual(result.probes.find(probe => probe.id === names[4]).evidence, evidence);
  assert.deepEqual(result.probes.find(probe => probe.id === names[4]).cleanup, results[1].probes[0].cleanup);
});

test('a failed required native probe makes the full P1 result failed', () => {
  const results = nativeProbeResults('passed');
  results[0].probes[1].status = 'failed';
  results[0].probes[1].reason = 'controlled native failure';
  results[0].overallStatus = 'failed';
  const result = createP1AcceptanceResult({
    surface: 'api',
    surfaceStatus: 'passed',
    sourceEvidence: { services: { status: 'passed' }, agentHostImageMeasurement: { status: 'passed' } },
    nativeProbeResults: results,
  });

  assert.equal(result.fullP1Status, 'failed');
  assert.equal(result.exitCode, 1);
});

test('all passed required native probes can produce a passed aggregate', () => {
  const result = createP1AcceptanceResult({
    surface: 'api',
    surfaceStatus: 'passed',
    sourceEvidence: { services: { status: 'passed' }, agentHostImageMeasurement: { status: 'passed' } },
    nativeProbeResults: nativeProbeResults('passed'),
  });

  assert.equal(result.fullP1Status, 'passed');
  assert.equal(result.exitCode, 0);
});

test('preserves the verified four-probe source envelope and blocks only the missing dispatch probes', () => {
  const sourceEnvelope = nativeProbeResults('passed')[0];
  const result = createP1AcceptanceResult({
    surface: 'api',
    surfaceStatus: 'passed',
    sourceEvidence: { services: { status: 'passed' }, agentHostImageMeasurement: { status: 'passed' } },
    nativeProbeResults: [sourceEnvelope],
  });

  assert.equal(result.fullP1Status, 'blocked');
  for (const probe of sourceEnvelope.probes) {
    const aggregate = result.probes.find(item => item.id === probe.name);
    assert.equal(aggregate.status, 'passed');
    assert.deepEqual(aggregate.evidence, probe.evidence);
    assert.deepEqual(aggregate.cleanup, probe.cleanup);
    assert.equal(aggregate.startedAt, sourceEnvelope.startedAt);
    assert.equal(aggregate.completedAt, sourceEnvelope.completedAt);
  }
  for (const name of names.slice(4)) {
    assert.equal(result.probes.find(item => item.id === name).status, 'blocked');
  }
});

test('rejects a missing, duplicate, or inconsistent native probe result', () => {
  const missing = nativeProbeResults();
  missing[0].probes.pop();
  assert.throws(() => createP1AcceptanceResult({
    surface: 'api', surfaceStatus: 'passed', nativeProbeResults: missing,
  }), /missing a required P1 probe/);

  const duplicate = nativeProbeResults();
  duplicate[1].probes[0] = { ...duplicate[1].probes[0], name: duplicate[0].probes[0].name };
  assert.throws(() => createP1AcceptanceResult({
    surface: 'api', surfaceStatus: 'passed', nativeProbeResults: duplicate,
  }), /invalid or duplicated/);

  const inconsistent = nativeProbeResults('blocked');
  inconsistent[1].overallStatus = 'passed';
  assert.throws(() => createP1AcceptanceResult({
    surface: 'api', surfaceStatus: 'passed', nativeProbeResults: inconsistent,
  }), /overall status does not match/);
});
