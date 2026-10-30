import test from 'node:test';
import assert from 'node:assert/strict';
import {
  validateP1JourneyInput,
  validateP1RunnerPlanInput,
} from '../p1-journey-input.mjs';

const fences = {
  projectId: 'controlled-project',
  runId: 'controlled-run',
  rootSessionId: 'controlled-root',
  sessionId: 'controlled-session',
  executionFence: 3,
};
const PROVIDER_SOURCE = 'c'.repeat(40);
const now = () => Date.parse('2026-10-13T00:00:00.000Z');

function input() {
  const invocationId = 'controlled-invocation';
  return {
    executionIntent: 'execute',
    journey: 'journal',
    environment: {
      baseUrl: 'https://environment.controlled.test/',
      issuer: 'https://identity.controlled.test/',
      audience: 'agentweaver-controlled',
      actorId: 'controlled-actor',
      tenantId: 'controlled-tenant',
    },
    pins: {
      context: 'p1-control',
      namespace: 'agentweaver',
      providerReceipt: {
        configMapName: 'provider-catalog-v1',
        sourceCommit: PROVIDER_SOURCE,
        uid: 'controlled-provider-uid',
        resourceVersion: '12',
      },
      runtimeReceipt: { generation: 2 },
      storageReceipt: { pvUid: 'controlled-pv' },
      ownerFences: fences,
      fixtureScope: {
        kind: 'p1-runner-owned',
        status: 'ready',
        invocationId,
        fixtureId: 'controlled-fixture',
        ...fences,
        expiresAt: '2026-10-14T00:00:00.000Z',
        allowedJourneys: ['journal'],
        ownershipReceipts: [fences.projectId, fences.runId, fences.rootSessionId, fences.sessionId]
          .map(resourceId => ({
            resourceType: 'owner-resource',
            resourceId,
            invocationId,
            creationReceipt: { resourceId },
            observedReceipt: { resourceId },
            ownerFence: fences.executionFence,
          })),
      },
    },
  };
}

function planInput() {
  const value = input();
  delete value.pins.ownerFences;
  delete value.pins.fixtureScope;
  value.fixturePlan = {
    expectedPlatformRuntimeRevision: 7,
    selectionContext: {
      revision: 'provider-catalog-v1',
      availableModelSelectionReferences: ['platform-model'],
      providerRequirements: [],
      requiredEgress: [],
    },
    deniedActor: { actorId: 'different-actor', tenantId: 'controlled-tenant' },
    gate: null,
    message: null,
  };
  return value;
}

test('accepts an execute input with actual invocation-owned resource receipts and exact fences', () => {
  const result = validateP1JourneyInput(input(), { now });
  assert.equal(result.fixture.invocationId, 'controlled-invocation');
  assert.equal(result.fences.executionFence, 3);
});

test('requires explicit execute intent and rejects secret-bearing input', () => {
  const rehearsal = input();
  rehearsal.executionIntent = 'preview';
  assert.throws(() => validateP1JourneyInput(rehearsal, { now }), /executionIntent/);

  const missingIntent = planInput();
  delete missingIntent.executionIntent;
  assert.throws(() => validateP1RunnerPlanInput(missingIntent, { now }), /executionIntent/);

  const nonExecutePlan = planInput();
  nonExecutePlan.executionIntent = 'preview';
  assert.throws(() => validateP1RunnerPlanInput(nonExecutePlan, { now }), /executionIntent/);

  const secret = input();
  secret.pins.runtimeReceipt.accessToken = 'do-not-include';
  assert.throws(() => validateP1JourneyInput(secret, { now }), /Credentials/);
});

test('rejects an invocation, resource, fence, expiry, or allowed-journey mismatch', () => {
  const mutations = [
    value => { value.pins.fixtureScope.ownershipReceipts[0].invocationId = 'another-invocation'; },
    value => { value.pins.fixtureScope.ownershipReceipts[0].observedReceipt.resourceId = 'foreign-resource'; },
    value => { value.pins.fixtureScope.ownershipReceipts[0].ownerFence++; },
    value => { value.pins.fixtureScope.expiresAt = '2026-10-12T00:00:00.000Z'; },
    value => { value.pins.fixtureScope.allowedJourneys = ['remote-mcp']; },
  ];
  for (const mutate of mutations) {
    const value = input();
    mutate(value);
    assert.throws(() => validateP1JourneyInput(value, { now }));
  }
});

test('P1 product-test inputs do not require publication, source, image, or archive receipts', () => {
  const value = input();
  assert.doesNotThrow(() => validateP1JourneyInput(value, { now }));
  assert.equal(Object.hasOwn(value, 'deploymentEvidence'), false);
  assert.equal(Object.hasOwn(value.pins, 'sourceReceipt'), false);
  assert.equal(Object.hasOwn(value.pins, 'imageReceipt'), false);
});

test('accepts an execution plan without caller-created owner receipts', () => {
  const value = validateP1RunnerPlanInput(planInput(), { now });
  assert.equal(value.journey, 'journal');
  assert.equal(value.fixturePlan.expectedPlatformRuntimeRevision, 7);
  assert.equal(value.fixturePlan.selectionContext.revision, 'provider-catalog-v1');
  assert.equal(Object.hasOwn(value.pins, 'fixtureScope'), false);
  assert.equal(Object.hasOwn(value.pins, 'ownerFences'), false);
});

test('allows omitted distinct denied actor while retaining validation when one is supplied', () => {
  const oneAccount = planInput();
  delete oneAccount.fixturePlan.deniedActor;
  const validated = validateP1RunnerPlanInput(oneAccount, { now });
  assert.equal(Object.hasOwn(validated.fixturePlan, 'deniedActor'), false);

  const sameActor = planInput();
  sameActor.fixturePlan.deniedActor = {
    actorId: sameActor.environment.actorId,
    tenantId: sameActor.environment.tenantId,
  };
  assert.throws(() => validateP1RunnerPlanInput(sameActor, { now }), /must be different/);

  const malformed = planInput();
  malformed.fixturePlan.deniedActor = { actorId: 'distinct-actor' };
  assert.throws(() => validateP1RunnerPlanInput(malformed, { now }), /exactly: actorId, tenantId/);
});

test('requires a positive expected platform-runtime revision in runner fixture plans', () => {
  const missing = planInput();
  delete missing.fixturePlan.expectedPlatformRuntimeRevision;
  assert.throws(() => validateP1RunnerPlanInput(missing, { now }), /expectedPlatformRuntimeRevision/);

  for (const revision of [0, -1, 1.5, Number.MAX_SAFE_INTEGER + 1]) {
    const invalid = planInput();
    invalid.fixturePlan.expectedPlatformRuntimeRevision = revision;
    assert.throws(() => validateP1RunnerPlanInput(invalid, { now }), /positive expected platform runtime revision/);
  }
});

test('rejects caller-supplied owner receipts, malformed fixture plans, and plan credentials', () => {
  const claimed = planInput();
  claimed.pins.ownerFences = { runId: 'claimed-run' };
  assert.throws(() => validateP1RunnerPlanInput(claimed, { now }), /caller-supplied owner fences/);

  const missingContext = planInput();
  delete missingContext.fixturePlan.selectionContext.revision;
  assert.throws(() => validateP1RunnerPlanInput(missingContext, { now }), /run selection context/);

  const secret = planInput();
  secret.fixturePlan.setupToken = 'do-not-include';
  assert.throws(() => validateP1RunnerPlanInput(secret, { now }), /Credentials/);
});
