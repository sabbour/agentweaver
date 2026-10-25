import assert from 'node:assert/strict';
import test from 'node:test';
import {
  attachSessionMaterialAcceptance,
  createSessionMaterialAcceptance,
} from '../session-material-acceptance.mjs';

const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);

function context() {
  return {
    sourceEvidence: {
      source: { sourceSha, sourceTree },
      integrationDeployment: {
        status: 'deployed',
        sourceSha,
        sourceTree,
        readbackSha256: 'c'.repeat(64),
      },
      services: { status: 'passed', sourceSha, sourceTree },
      providerCatalog: { sourceCommit: sourceSha, uid: 'catalog-uid' },
      agentHostImageMeasurement: {
        status: 'passed',
        sourceSha,
        sourceTree,
        registryDigest: `sha256:${'d'.repeat(64)}`,
        archiveSha256: 'e'.repeat(64),
      },
    },
    environment: {
      actorId: 'authorized-actor',
      tenantId: 'authorized-tenant',
      issuer: 'https://identity.example.test',
      audience: 'agentweaver-api',
      token: 'must-not-be-retained',
    },
    fixtureInput: {
      fixturePlan: {
        expectedPlatformRuntimeRevision: 7,
        selectionContext: { revision: 'catalog-revision-12' },
      },
      pins: {
        ownerFences: {
          projectId: 'owned-project',
          runId: 'owned-run',
          rootSessionId: 'owned-root',
          sessionId: 'owned-session',
          executionFence: 19,
        },
        fixtureScope: {
          fixtureId: 'owned-fixture',
          invocationId: '00000000-0000-4000-8000-000000000235',
          allowedJourneys: ['journal'],
        },
      },
    },
  };
}

test('records exact source and authorized fixture identity while keeping the unsupported deployed journey blocked', () => {
  const result = createSessionMaterialAcceptance(context());

  assert.equal(result.status, 'blocked');
  assert.equal(result.evidenceClass, 'deployed-runtime');
  assert.deepEqual(result.sourceEvidence.source, { sourceSha, sourceTree });
  assert.equal(result.sourceEvidence.deployment.readbackSha256, 'c'.repeat(64));
  assert.equal(result.sourceEvidence.agentHostImageMeasurement.archiveSha256, 'e'.repeat(64));
  assert.equal(result.identity.actorId, 'authorized-actor');
  assert.equal(result.identity.tenantId, 'authorized-tenant');
  assert.equal(result.identity.projectId, 'owned-project');
  assert.equal(result.identity.executionFence, 19);
  assert.equal(result.identity.selectionContextRevision, 'catalog-revision-12');
  assert.equal(result.identity.runtimeInstanceId, null);
  assert.equal(result.identity.registrationRevision, null);
  assert.equal(JSON.stringify(result).includes('must-not-be-retained'), false);
});

test('separates unattempted turn evidence, optional cache fallback, and required turn failure', () => {
  const result = createSessionMaterialAcceptance(context());

  assert.equal(result.turn.request.status, 'not-sent');
  assert.equal(result.turn.result.status, 'not-observed');
  assert.deepEqual(result.turn.acknowledgment, { status: 'not-observed', redactedReceipt: null });
  assert.deepEqual(result.turn.event, { eventId: null, committedPosition: null, cursor: null });
  assert.equal(result.recovery.optionalSdkCache.absent.expectedDisposition, 'rebuild-from-retained-turns');
  assert.equal(result.recovery.optionalSdkCache.expired.expectedDisposition, 'rebuild-from-retained-turns');
  assert.equal(result.recovery.requiredTurnMaterial.missing.expectedDisposition, 'turn-failure');
  assert.equal(result.recovery.requiredTurnMaterial.expired.expectedDisposition, 'turn-failure');
  assert.equal(result.recovery.modelTurnDuringRebuild.count, null);
  assert.equal(result.recovery.repeatedExternalEffects.status, 'not-observed');
  assert.ok(result.ownerBoundaryChecks.every(check => check.status === 'blocked'));
  assert.equal(result.controlledEvidence.status, 'not-executed');
  assert.equal(result.cleanup.status, 'not-started');
  assert.ok(result.blockers.some(reason => reason.includes('runtime authorization')));
});

test('a blocked session-material scenario prevents a success-shaped full P1 result without rewriting surface status', () => {
  const acceptanceResult = {
    kind: 'p1-acceptance-result',
    surfaceStatus: 'passed',
    fullP1Status: 'passed',
    exitCode: 0,
    observations: { existing: { retained: true } },
    probes: [{ id: 'existing-probe', status: 'passed' }],
  };
  const scenario = createSessionMaterialAcceptance(context());

  const result = attachSessionMaterialAcceptance(acceptanceResult, scenario);

  assert.equal(result.surfaceStatus, 'passed');
  assert.equal(result.fullP1Status, 'blocked');
  assert.equal(result.exitCode, 2);
  assert.deepEqual(result.probes, [{ id: 'existing-probe', status: 'passed' }]);
  assert.equal(result.observations.existing.retained, true);
  assert.equal(result.observations.sessionMaterialAcceptance, scenario);
  assert.throws(() => attachSessionMaterialAcceptance(acceptanceResult, {
    ...scenario,
    status: 'passed',
  }), /must remain explicitly blocked/);
});
