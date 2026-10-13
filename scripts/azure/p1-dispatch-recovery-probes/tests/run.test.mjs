import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { runP1DispatchRecoveryProbes } from '../run.mjs';

const probeNames = [
  'numeric-copilot-dispatch-boundaries',
  'checkpoint-restart-duplicate-fence',
];

test('source-only result keeps both required acceptance slots blocked without runtime observations', () => {
  const result = runP1DispatchRecoveryProbes();

  assert.equal(result.kind, 'p1-dispatch-recovery-acceptance');
  assert.equal(result.schemaVersion, 1);
  assert.equal(result.overallStatus, 'blocked');
  assert.deepEqual(result.probes.map(probe => probe.name), probeNames);
  for (const probe of result.probes) {
    assert.equal(probe.status, 'blocked');
    assert.ok(probe.reason.length > 0);
    assert.equal(probe.evidence.kind, 'source-prerequisite-blocker');
    assert.equal(probe.evidence.observed, false);
    assert.deepEqual(probe.evidence.observations, []);
    assert.ok(probe.evidence.requiredReceipts.length > 0);
    for (const receipt of probe.evidence.requiredReceipts) {
      assert.ok(receipt.owner.length > 0);
      assert.ok(receipt.receipt.length > 0);
      assert.ok(receipt.requiredFacts.length > 0);
    }
    assert.deepEqual(probe.cleanup, {
      status: 'not-started',
      createdResourceIds: [],
    });
  }

  const numeric = result.probes[0];
  assert.deepEqual(
    numeric.evidence.sourceGates.map(gate => gate.code).filter(Boolean),
    ['runtime_numeric_limits_unsupported', 'runtime_usage_affinity_unavailable'],
  );
  const usageReceipt = numeric.evidence.requiredReceipts.find(receipt => receipt.owner === 'Events');
  assert.ok(usageReceipt.requiredFacts.some(fact =>
    fact.includes('whole-run priced Copilot total across children')));
  assert.ok(usageReceipt.requiredFacts.some(fact =>
    fact.includes('snapshot/guard revision')));
  assert.ok(usageReceipt.requiredFacts.some(fact =>
    fact.includes('one durable soft-limit warning')));
  assert.ok(usageReceipt.requiredFacts.some(fact =>
    fact.includes('later priced totals advance')));
  assert.match(numeric.reason, /only for workflow-bound registrations/);
  assert.match(numeric.reason, /MaxPromptTokens remains preserved through owner projection/);
  assert.match(numeric.reason, /rejected with runtime_numeric_limits_unsupported before SDK setup/);
  assert.match(numeric.evidence.sourceGates[0].detail, /Workflow-bound MaxToolCalls/);
  assert.match(numeric.evidence.sourceGates[0].detail, /MaxPromptTokens is retained through owner projection/);
  assert.match(numeric.evidence.sourceGates[2].detail, /one durable warning/);
  assert.match(numeric.evidence.sourceGates[2].detail, /not an all-usage-final claim/);
  const restart = result.probes[1];
  assert.match(restart.reason, /P2 manifest suspend\/resume path is explicitly excluded/);
  assert.match(restart.reason, /producer and owner-fenced placement, bootstrap, readiness, and source-derived recovery receipt exist/);
  assert.match(restart.reason, /does not dispatch, invoke, or ingest the native runner/);
  assert.match(restart.reason, /does not dispatch, invoke, or ingest the native runner or collect a replacement-run recovery receipt/);
  assert.match(restart.reason, /GatewayRouteCatalog has no matching mapping/);
  assert.deepEqual(restart.evidence.sourceGates.map(gate => gate.path), [
    'services/environment/Agentweaver.Environment/EnvironmentRuntimePlacementReader.cs',
    'packages/Agentweaver.AgentRuntime/RuntimeAgentHost.cs',
  ]);
  assert.match(restart.evidence.sourceGates[0].detail, /authenticated replacement producer.*exist/);
  assert.match(restart.evidence.sourceGates[0].detail, /does not invoke or dispatch them/);
  assert.match(restart.evidence.sourceGates[1].detail, /RuntimeHostReadinessReceipt includes the source-derived RuntimeSessionRecovery mode and RuntimeHostRecoveryReceipt/);
  assert.match(restart.evidence.sourceGates[1].detail, /does not invoke the runner or collect a run-specific recovery receipt/);
  assert.match(restart.evidence.sourceGates[1].detail, /ResumeCache remains specific to the excluded P2 manifest-resume flow/);
  assert.deepEqual(restart.evidence.requiredReceipts.map(receipt => receipt.receipt), [
    'invocation-owned-gate-join-and-checkpoint',
    'ordinary-replacement-placement',
    'ordinary-fresh-registration-and-bootstrap',
    'ordinary-runtime-recovery-observation',
    'gate-join-result-and-no-duplicate',
  ]);
  const placementReceipt = restart.evidence.requiredReceipts.find(
    receipt => receipt.receipt === 'ordinary-replacement-placement',
  );
  assert.ok(placementReceipt.requiredFacts.some(fact =>
    fact.includes('receiver expected audience and certificate DNS name')));
  assert.ok(placementReceipt.requiredFacts.some(fact =>
    fact.includes('even when registration/readiness succeeds')));
});

test('fixed command emits the same blocked contract and accepts no runtime input', () => {
  const script = fileURLToPath(new URL('../run.mjs', import.meta.url));
  const secretMarker = 'p1-probe-secret-sentinel';
  const result = spawnSync(process.execPath, [script], {
    encoding: 'utf8',
    env: {
      ...process.env,
      AZURE_ACCESS_TOKEN: secretMarker,
      COPILOT_SESSION_TOKEN: secretMarker,
    },
  });

  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stderr, '');
  assert.equal(result.stdout.includes(secretMarker), false);
  const report = JSON.parse(result.stdout);
  assert.deepEqual(report.probes.map(probe => probe.name), probeNames);
  assert.ok(report.probes.every(probe => probe.status === 'blocked'));

  const withInput = spawnSync(process.execPath, [script, '--accept'], { encoding: 'utf8' });
  assert.equal(withInput.status, 2);
  assert.equal(withInput.stdout, '');
  assert.match(withInput.stderr, /^Usage:/);
});
