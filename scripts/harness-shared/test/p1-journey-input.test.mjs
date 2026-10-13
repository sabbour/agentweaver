import test from 'node:test';
import assert from 'node:assert/strict';
import { validateP1JourneyInput } from '../p1-journey-input.mjs';

const fences = {
  projectId: 'controlled-project',
  runId: 'controlled-run',
  rootSessionId: 'controlled-root',
  sessionId: 'controlled-session',
  executionFence: 3,
};
const SOURCE_SHA = 'a'.repeat(40);
const SOURCE_TREE = 'b'.repeat(40);
const PROVIDER_SOURCE = 'c'.repeat(40);
const now = () => Date.parse('2026-10-13T00:00:00.000Z');

function input() {
  const invocationId = 'controlled-invocation';
  return {
    executionIntent: 'execute',
    journey: 'journal',
    deploymentEvidence: {
      publicationPath: 'artifacts/p1/publication.json',
      manifestPath: 'releases/foundation.json',
      agentHostArchivePath: 'artifacts/p1/Agentweaver.AgentHost.oci.tar',
    },
    environment: {
      baseUrl: 'https://environment.controlled.test/',
      issuer: 'https://identity.controlled.test/',
      audience: 'agentweaver-controlled',
      actorId: 'controlled-actor',
      tenantId: 'controlled-tenant',
    },
    pins: {
      context: 'p1-control',
      namespace: 'agentweaver-v1-p0',
      providerReceipt: {
        configMapName: 'provider-catalog-v1',
        sourceCommit: PROVIDER_SOURCE,
        uid: 'controlled-provider-uid',
        resourceVersion: '12',
      },
      runtimeReceipt: { generation: 2 },
      storageReceipt: { pvUid: 'controlled-pv' },
      sourceReceipt: { sourceSha: SOURCE_SHA, sourceTree: SOURCE_TREE },
      imageReceipt: {
        registryImage: `ghcr.io/sabbour/agentweaver/agenthost@sha256:${'d'.repeat(64)}`,
        registryDigest: `sha256:${'d'.repeat(64)}`,
        archiveSha256: 'e'.repeat(64),
        manifestDigest: `sha256:${'f'.repeat(64)}`,
        configDigest: `sha256:${'1'.repeat(64)}`,
        compressedPullBytes: 1024,
      },
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

test('accepts an execute input with actual invocation-owned resource receipts and exact fences', () => {
  const result = validateP1JourneyInput(input(), { now });
  assert.equal(result.fixture.invocationId, 'controlled-invocation');
  assert.equal(result.fences.executionFence, 3);
});

test('rejects non-execute intent and secret-bearing input', () => {
  const rehearsal = input();
  rehearsal.executionIntent = 'preview';
  assert.throws(() => validateP1JourneyInput(rehearsal, { now }), /executionIntent/);
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

test('rejects a malformed deployment evidence path or mismatched image receipt pin', () => {
  const missingPath = input();
  delete missingPath.deploymentEvidence.publicationPath;
  assert.throws(() => validateP1JourneyInput(missingPath, { now }), /deployment evidence input/);

  const mutableImage = input();
  mutableImage.pins.imageReceipt.registryImage = 'ghcr.io/sabbour/agentweaver/agenthost:latest';
  assert.throws(() => validateP1JourneyInput(mutableImage, { now }), /immutable registry/);
});
