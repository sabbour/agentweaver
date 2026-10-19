import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import { runP1DispatchRecoveryProbes } from '../run.mjs';
import { validateP1NativeProbeResults } from '../../../harness-shared/p1-acceptance-result.mjs';

const probeNames = [
  'numeric-copilot-dispatch-boundaries',
  'checkpoint-restart-duplicate-fence',
];

/** Build distinct owner and placement identities for current-recovery tests. */
function createRecoveryContext() {
  const owner = {
    actorId: 'actor-a',
    tenantId: 'tenant-a',
    projectId: 'project-a',
    runId: 'run-a',
    sessionId: 'session-a',
    environmentId: 'environment-a',
    executionFence: 9,
  };
  return {
    owner,
    placement: { resource: { resourceId: 'claim-new', generation: 4 } },
    previousPodUid: 'pod-old',
    expectedPodUid: 'pod-new',
    previousRuntimeInstanceId: '00000000-0000-4000-8000-000000000001',
    expectedRuntimeInstanceId: '00000000-0000-4000-8000-000000000002',
  };
}

/** Build a current readiness receipt with a resource ID distinct from its Pod UID. */
function createRecoveryReadback(context, recoveryMode = 'JournalRebuild') {
  const { owner, placement, expectedPodUid, expectedRuntimeInstanceId } = context;
  const acceptedSelectionHash = 'a'.repeat(64);
  const observationEndpoint = 'https://runtime.example.test/internal/runtime/observations';
  const binding = {
    ...owner,
    placementUid: placement.resource.resourceId,
    placementGeneration: placement.resource.generation,
    acceptedSelectionHash,
    modelSelectionReference: 'selection-a',
    observationEndpoint,
  };
  const registration = {
    runtimeInstanceId: expectedRuntimeInstanceId,
    revision: 3,
    state: 'active',
    expiresAt: '2099-01-01T00:00:00.000Z',
    binding,
  };
  const source = {
    runtimeInstanceId: registration.runtimeInstanceId,
    registrationRevision: registration.revision,
    sdkSessionId: `agentweaver-session-${createHash('sha256')
      .update([owner.tenantId, owner.projectId, owner.runId, owner.sessionId].join('\0'), 'utf8')
      .digest('hex')}`,
    sdkVersion: '1.0',
    runtimeVersion: '1.0',
    sourceMode: 'hosted-copilot',
    meterSource: 'copilot.nano_aiu',
    modelSelectionReference: binding.modelSelectionReference,
    acceptedSelectionHash,
    modelId: 'model-a',
    reasoningEffort: 'high',
  };
  const sourceGrant = {
    grantId: '00000000-0000-4000-8000-000000000011',
    runtimeInstanceId: registration.runtimeInstanceId,
    registrationRevision: registration.revision,
    revision: 2,
    issuer: 'https://issuer.example.test',
    purpose: 'Observe',
    audience: observationEndpoint,
    state: 'Active',
    configurationHash: 'b'.repeat(64),
    expiresAt: '2098-01-01T00:00:00.000Z',
    recordedAt: '2097-01-01T00:00:00.000Z',
  };
  const recovery = {
    contractVersion: 1,
    runtimeInstanceId: registration.runtimeInstanceId,
    registrationRevision: registration.revision,
    executionFence: owner.executionFence,
    recoveryMode,
    source,
    ...(recoveryMode === 'JournalRebuild'
      ? { journal: { throughPosition: 12, journalSha256: 'c'.repeat(64) } }
      : {
        nativeCacheAcknowledgment: {
          contractVersion: 1,
          identity: {
            projectId: owner.projectId,
            runId: owner.runId,
            sessionId: owner.sessionId,
          },
          eventId: '00000000-0000-4000-8000-000000000021',
          position: 12,
          reference: {
            purpose: 'runtime-sdk-cache',
            byteLength: 64,
            material: {
              contractVersion: 1,
              kind: 'sdkCache',
              tenantId: owner.tenantId,
              sha256: 'd'.repeat(64),
              runtimeInstanceId: registration.runtimeInstanceId,
              registrationRevision: registration.revision,
              executionFence: owner.executionFence,
              acceptedSelectionHash,
              sdkVersion: source.sdkVersion,
              runtimeVersion: source.runtimeVersion,
              modelSelectionReference: source.modelSelectionReference,
              modelId: source.modelId,
              reasoningEffort: source.reasoningEffort,
            },
          },
        },
      }),
  };
  return {
    registration,
    placementObservation: {
      resource: { ...placement.resource },
      pod: { uid: expectedPodUid },
    },
    readinessReceipt: {
      contractVersion: 1,
      runtimeInstanceId: registration.runtimeInstanceId,
      registrationRevision: registration.revision,
      executionFence: owner.executionFence,
      sourceGrant,
      recovery,
    },
  };
}

test('missing live inputs keep both required acceptance slots blocked', async () => {
  const result = await runP1DispatchRecoveryProbes();

  assert.equal(result.kind, 'p1-dispatch-recovery-acceptance');
  assert.equal(result.schemaVersion, 1);
  assert.equal(result.overallStatus, 'blocked');
  assert.deepEqual(result.ownerObservation, { status: 'not-requested' });
  assert.deepEqual(result.liveObservations, {
    creditDenials: { status: 'not-requested' },
    replacementRecovery: { status: 'not-requested' },
  });
  assert.deepEqual(result.probes.map(probe => probe.name), probeNames);
  for (const probe of result.probes) {
    assert.equal(probe.status, 'blocked');
    assert.ok(probe.reason.length > 0);
    assert.equal(probe.evidence.kind, 'source-prerequisite-blocker');
    assert.equal(probe.evidence.observed, false);
    assert.equal(probe.evidence.acceptanceEvidence, false);
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
  assert.match(numeric.reason, /existing Host reader can verify persisted zero-limit and hard-credit no-send receipts/);
  assert.match(numeric.reason, /MaxPromptTokens is rejected before SDK setup/);
  assert.match(numeric.evidence.sourceGates[0].detail, /Workflow-bound MaxToolCalls/);
  assert.match(numeric.evidence.sourceGates[0].detail, /MaxPromptTokens remains unsupported before SDK setup/);
  assert.match(numeric.evidence.sourceGates[2].detail, /one durable soft-limit warning/);
  assert.match(numeric.evidence.sourceGates[2].detail, /not an all-usage-final claim/);
  const restart = result.probes[1];
  assert.match(restart.reason, /P2 manifest suspend\/resume path is excluded/);
  assert.match(restart.reason, /no live replacement recovery receipt or before\/after gate-join and no-duplicate receipts were read/);
  assert.deepEqual(restart.evidence.sourceGates.map(gate => gate.path), [
    'services/environment/Agentweaver.Environment/EnvironmentRuntimePlacementReader.cs',
    'packages/Agentweaver.AgentRuntime/RuntimeAgentHostEndpoints.cs',
    'packages/Agentweaver.Identity/RuntimeHostContracts.cs',
  ]);
  assert.match(restart.evidence.sourceGates[0].detail, /does not abandon or reprovision a Host/);
  assert.match(restart.evidence.sourceGates[1].detail, /readiness endpoint returns RuntimeHostRecoveryReceipt/);
  assert.match(restart.evidence.sourceGates[2].detail, /does not prove the prior dispatch result or absence of duplicate effects/);
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
    fact.includes('actual replacement Host endpoint checked against the receiver expected audience')));
});

test('read-only owner observations are recorded but cannot claim dispatch or cleanup acceptance', async () => {
  const calls = [];
  const runtime = {
    registration: { state: 'Available', value: { runtimeInstanceId: 'runtime-a', revision: 2, state: 'Active' } },
    execution: {
      state: 'Available',
      value: {
        observation: { status: 'Succeeded', checkpointId: 'checkpoint-a' },
        checkpointParent: { hasParentMetadata: true, parentCheckpointId: null },
      },
    },
    creditGuard: { state: 'Unknown', value: null },
    dispatches: { state: 'Available', value: { items: [], truncated: false } },
    toolReservations: { state: 'Available', value: { items: [], truncated: false } },
  };
  const environment = {
    environmentId: 'environment-a',
    fence: { lifecycleGeneration: 3 },
    providerAttempts: { attempts: [{ state: 'InProgress' }] },
    lateResourceCleanups: { items: [{ operationId: 'operation-a' }], hasMore: false },
  };
  const result = await runP1DispatchRecoveryProbes({
    observationClient: {
      async readRuntimeObservationV1(owner) {
        calls.push(['runtime', owner]);
        return runtime;
      },
      async readEnvironmentEvidence(context) {
        calls.push(['environment', context]);
        return environment;
      },
    },
    observationContext: {
      owner: { projectId: 'project-a', runId: 'run-a', sessionId: 'session-a' },
      environmentCreation: { snapshot: { fence: { owner: { environmentId: 'environment-a' } } } },
      operationId: 'operation-a',
    },
  });

  assert.equal(calls.length, 2);
  assert.equal(result.overallStatus, 'blocked');
  assert.equal(result.ownerObservation.status, 'observed');
  assert.equal(result.ownerObservation.value.acceptanceEvidence, false);
  assert.equal(result.ownerObservation.value.raceObserved, false);
  assert.equal(result.ownerObservation.value.cleanupVerified, false);
  assert.equal(result.ownerObservation.value.runtime.creditGuard.state, 'Unknown');
  assert.equal(result.ownerObservation.value.environment.providerAttempts.count, 1);
  assert.equal(result.liveObservations.creditDenials.status, 'not-requested');
  assert.ok(result.probes.every(probe => probe.status === 'blocked'));
});

test('a live replacement recovery receipt is preserved but cannot replace missing no-duplicate receipts', async () => {
  const recoveryContext = createRecoveryContext();
  const readback = createRecoveryReadback(recoveryContext);
  const result = await runP1DispatchRecoveryProbes({
    runtimeReadinessProvider: {
      async readCurrent() { return readback; },
    },
    recoveryContext,
  });

  assert.equal(result.overallStatus, 'blocked');
  assert.equal(result.liveObservations.replacementRecovery.status, 'observed');
  assert.equal(result.liveObservations.replacementRecovery.value.recoveryMode, 'JournalRebuild');
  assert.equal(result.liveObservations.replacementRecovery.value.replacementPodUid, 'pod-new');
  assert.equal(result.liveObservations.replacementRecovery.value.placementResourceId, 'claim-new');
  const probe = result.probes.find(item => item.name === 'checkpoint-restart-duplicate-fence');
  assert.equal(probe.status, 'blocked');
  assert.equal(probe.evidence.acceptanceEvidence, false);
  assert.equal(probe.evidence.observations[0].value.journal.throughPosition, 12);
  assert.match(probe.reason, /no source-owned producer returns the before\/after gate-join checkpoint/);
});

test('replacement recovery rejects revoked, stale, cross-owner, or mismatched live receipts', async () => {
  const corruptions = [
    ['revoked registration', readback => { readback.registration.state = 'revoked'; }],
    ['expired registration', readback => { readback.registration.expiresAt = '2000-01-01T00:00:00.000Z'; }],
    ['different live Pod UID', readback => { readback.placementObservation.pod.uid = 'pod-other'; }],
    ['different source session', readback => { readback.readinessReceipt.recovery.source.sdkSessionId = 'agentweaver-session-other'; }],
  ];

  for (const [label, corrupt] of corruptions) {
    const recoveryContext = createRecoveryContext();
    const readback = createRecoveryReadback(recoveryContext);
    corrupt(readback);
    const result = await runP1DispatchRecoveryProbes({
      runtimeReadinessProvider: { async readCurrent() { return readback; } },
      recoveryContext,
    });
    assert.equal(result.liveObservations.replacementRecovery.status, 'unavailable', label);
    assert.equal(result.probes[1].status, 'blocked', label);
    assert.match(result.liveObservations.replacementRecovery.reason, /does not match the run-bound owner and placement/, label);
  }
});

test('NativeCache recovery rejects an acknowledgment from another session owner', async () => {
  const recoveryContext = createRecoveryContext();
  const readback = createRecoveryReadback(recoveryContext, 'NativeCache');
  readback.readinessReceipt.recovery.nativeCacheAcknowledgment.identity.projectId = 'project-other';
  const result = await runP1DispatchRecoveryProbes({
    runtimeReadinessProvider: { async readCurrent() { return readback; } },
    recoveryContext,
  });

  assert.equal(result.liveObservations.replacementRecovery.status, 'unavailable');
  assert.match(result.liveObservations.replacementRecovery.reason, /cache recovery acknowledgment is missing or invalid/);
  assert.equal(result.probes[1].status, 'blocked');
});

test('NativeCache recovery retains an acknowledgment bound to the current owner and source', async () => {
  const recoveryContext = createRecoveryContext();
  const readback = createRecoveryReadback(recoveryContext, 'NativeCache');
  const result = await runP1DispatchRecoveryProbes({
    runtimeReadinessProvider: { async readCurrent() { return readback; } },
    recoveryContext,
  });

  assert.equal(result.liveObservations.replacementRecovery.status, 'observed');
  assert.equal(
    result.liveObservations.replacementRecovery.value.nativeCacheAcknowledgment.eventId,
    '00000000-0000-4000-8000-000000000021',
  );
  assert.equal(result.overallStatus, 'blocked');
});

test('failed owner observations are explicit and do not become successful evidence', async () => {
  const result = await runP1DispatchRecoveryProbes({
    observationClient: {
      async readRuntimeObservationV1() { throw new Error('runtime observation owner unavailable'); },
      async readEnvironmentEvidence() { throw new Error('should not be called'); },
    },
    observationContext: {
      owner: { projectId: 'project-a', runId: 'run-a', sessionId: 'session-a' },
      environmentCreation: {},
      operationId: 'operation-a',
    },
  });

  assert.deepEqual(result.ownerObservation, {
    status: 'unavailable',
    reason: 'runtime observation owner unavailable',
  });
  assert.equal(result.overallStatus, 'blocked');
  assert.ok(result.probes.every(probe => probe.status === 'blocked'));
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
  assert.doesNotThrow(() => validateP1NativeProbeResults([report]));
  assert.deepEqual(report.probes.map(probe => probe.name), probeNames);
  assert.ok(report.probes.every(probe => probe.status === 'blocked'));

  const withInput = spawnSync(process.execPath, [script, '--accept'], { encoding: 'utf8' });
  assert.equal(withInput.status, 2);
  assert.equal(withInput.stdout, '');
  assert.match(withInput.stderr, /^Usage:/);
});
