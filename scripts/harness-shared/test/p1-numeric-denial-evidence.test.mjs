import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { createP1AcceptanceResult } from '../p1-acceptance-result.mjs';
import { runP1DispatchRecoveryProbes } from '../../azure/p1-dispatch-recovery-probes/run.mjs';
// Register the controlled reader regressions on the existing harness CI path.
import '../../azure/p1-dispatch-recovery-probes/tests/numeric-denials.test.mjs';

test('validated numeric owner evidence survives aggregation without satisfying either required probe', async () => {
  const [{ receipt }] = JSON.parse(readFileSync(new URL(
    '../../azure/p1-dispatch-recovery-probes/tests/producer-vectors.json', import.meta.url), 'utf8'));
  const expected = Object.fromEntries(Object.entries(receipt).filter(([key]) => ![
    'contractVersion', 'receiptId', 'observedCount', 'sendStateBeforeHash',
    'sendStateAfterHash', 'recordedAt', 'canonicalPayloadHash',
  ].includes(key)));
  const report = await runP1DispatchRecoveryProbes({
    numericDenialInput: {
      modelTurns: { expected, recordedAfter: '2026-10-27T19:59:59Z', recordedBefore: '2026-10-27T20:00:01Z' },
    },
    numericDenialDependencies: { ownerCaller: { async postJson() { return { receipt }; } } },
  });
  assert.equal(report.liveObservations.numericDenials.cases.modelTurns.status, 'observed');
  const aggregate = createP1AcceptanceResult({
    surface: 'api',
    surfaceStatus: 'passed',
    nativeProbeResults: [report],
  });
  assert.equal(aggregate.fullP1Status, 'blocked');
  for (const name of ['numeric-copilot-dispatch-boundaries', 'checkpoint-restart-duplicate-fence']) {
    const probe = aggregate.probes.find(item => item.id === name);
    assert.equal(probe.required, true);
    assert.equal(probe.status, 'blocked');
    assert.equal(probe.evidence.acceptanceEvidence, false);
  }
  assert.deepEqual(aggregate.probes.find(item => item.id === 'numeric-copilot-dispatch-boundaries').evidence,
    report.probes[0].evidence);
});
