import { redact } from './redaction.mjs';

const CONTROLLED_EVIDENCE = [
  'tests/Agentweaver.Identity.Broker.Tests/RuntimeRegistrationOwnerIntegrationTests.cs',
  'tests/Agentweaver.Identity.Broker.Tests/RuntimeSessionRecoveryTests.cs',
  'tests/Agentweaver.EventsAndSessions.Tests/SessionMaterialPostgresTests.cs',
];

function sessionMaterialBlockers() {
  return [
    'The authorized P1 client exposes Gateway operations, while session-material routes are internal Events owner routes requiring runtime authorization that this identity provider does not issue.',
    'The runner-owned fixture permits only its selected P1 surface journey; it has no approved session-material fixture creation or bounded material cleanup boundary.',
    'The authorized P1 flow has no native Host replacement boundary or receipt for compatible cache restore, journal rebuild, or proving that recovery did not invoke a model turn or repeat external effects.',
    'The authorized P1 flow has no supported fault hook for current-authority revocation, stale fence or registration, duplicate or changed retry, or missing required turn material.',
  ];
}

function sourceSnapshot(sourceEvidence) {
  if (!sourceEvidence || typeof sourceEvidence !== 'object') return null;
  return redact({
    source: sourceEvidence.source ?? null,
    deployment: sourceEvidence.integrationDeployment ?? sourceEvidence.releaseDeployment ?? null,
    services: sourceEvidence.services ?? null,
    providerCatalog: sourceEvidence.providerCatalog ?? null,
    agentHostImageMeasurement: sourceEvidence.agentHostImageMeasurement ?? null,
    agentHostRuntime: sourceEvidence.agentHostRuntime ?? null,
  });
}

function identitySnapshot(environment, fixtureInput) {
  const fences = fixtureInput?.pins?.ownerFences ?? {};
  const fixture = fixtureInput?.pins?.fixtureScope ?? {};
  return redact({
    actorId: environment?.actorId ?? null,
    tenantId: environment?.tenantId ?? null,
    issuer: environment?.issuer ?? null,
    audience: environment?.audience ?? null,
    projectId: fences.projectId ?? fixture.projectId ?? null,
    runId: fences.runId ?? fixture.runId ?? null,
    rootSessionId: fences.rootSessionId ?? fixture.rootSessionId ?? null,
    sessionId: fences.sessionId ?? fixture.sessionId ?? null,
    executionFence: fences.executionFence ?? fixture.executionFence ?? null,
    fixtureId: fixture.fixtureId ?? null,
    invocationId: fixture.invocationId ?? null,
    allowedJourneys: fixture.allowedJourneys ?? [],
    platformRuntimeRevision: fixtureInput?.fixturePlan?.expectedPlatformRuntimeRevision ?? null,
    selectionContextRevision: fixtureInput?.fixturePlan?.selectionContext?.revision ?? null,
    runtimeInstanceId: null,
    registrationRevision: null,
  });
}

export function createSessionMaterialAcceptance({ sourceEvidence, environment, fixtureInput } = {}) {
  const blockers = sessionMaterialBlockers();
  const identity = identitySnapshot(environment, fixtureInput);
  const result = {
    kind: 'p1-session-material-acceptance',
    schemaVersion: 1,
    status: 'blocked',
    evidenceClass: 'deployed-runtime',
    sourceEvidence: sourceSnapshot(sourceEvidence),
    identity,
    turn: {
      status: 'blocked',
      request: {
        status: 'not-sent',
        method: 'POST',
        route: '/internal/sessions/{sessionId}/material',
        materialKind: 'TurnContent',
        redactedBody: null,
      },
      result: { status: 'not-observed', redactedBody: null },
      acknowledgment: { status: 'not-observed', redactedReceipt: null },
      event: { eventId: null, committedPosition: null, cursor: null },
    },
    ownerBoundaryChecks: [
      'current-authority-revocation',
      'stale-fence-or-registration',
      'duplicate-and-changed-retry',
      'missing-required-turn-material',
    ].map(name => ({ name, status: 'blocked', reason: blockers[3] })),
    recovery: {
      status: 'blocked',
      compatibleReplacementRuntime: { status: 'not-attempted', registrationRevision: null },
      optionalSdkCache: {
        absent: { status: 'not-probed', expectedDisposition: 'rebuild-from-retained-turns' },
        expired: { status: 'not-probed', expectedDisposition: 'rebuild-from-retained-turns' },
      },
      requiredTurnMaterial: {
        missing: { status: 'not-probed', expectedDisposition: 'turn-failure' },
        expired: { status: 'not-probed', expectedDisposition: 'turn-failure' },
      },
      modelTurnDuringRebuild: { status: 'not-observed', count: null },
      repeatedExternalEffects: { status: 'not-observed' },
      reason: blockers[2],
    },
    controlledEvidence: {
      status: 'not-executed',
      evidenceClass: 'controlled-source',
      sources: CONTROLLED_EVIDENCE,
    },
    cleanup: {
      status: 'not-started',
      target: null,
      reason: 'No session-material fixture was created; the existing P1 fixture cleanup remains separately recorded.',
    },
    blockers,
  };
  return redact(result);
}

export function attachSessionMaterialAcceptance(acceptanceResult, scenario) {
  if (!acceptanceResult || acceptanceResult.kind !== 'p1-acceptance-result'
    || !scenario || scenario.kind !== 'p1-session-material-acceptance'
    || scenario.status !== 'blocked') {
    throw new Error('The current session-material scenario must remain explicitly blocked.');
  }
  acceptanceResult.observations = {
    ...acceptanceResult.observations,
    sessionMaterialAcceptance: scenario,
  };
  if (acceptanceResult.fullP1Status !== 'failed') {
    acceptanceResult.fullP1Status = 'blocked';
    acceptanceResult.exitCode = 2;
  }
  return acceptanceResult;
}
