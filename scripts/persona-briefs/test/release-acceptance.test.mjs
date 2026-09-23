import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
  abnormalTriggers,
  canCloseRepair,
  computeAnomalyId,
  deriveAnomalies,
  isAbnormalReleaseAcceptance,
  validateCoordinatorDisposition,
  validateReleaseAcceptanceManifest,
} from '../release-acceptance.mjs';
import { validateReleaseAcceptance } from '../release-acceptance-gate.mjs';
import { loadChallengeCatalog } from '../challenge-catalog.mjs';

const HASH = 'a'.repeat(64);
const REVISION = 'revision-123';
const PROJECT_ID = 'project-1';
const EXECUTION_ID = 'execution-1';
const RUN_ID = 'run-1';

function evidence(surface, type = 'surface-transcript') {
  return {
    type,
    path: `${surface}.json`,
    sha256: HASH,
    deployedRevision: REVISION,
    projectId: PROJECT_ID,
    executionId: EXECUTION_ID,
    runId: RUN_ID,
    challengeId: 'release-repair-disposition-v1',
    challengeVersion: 1,
    surface,
  };
}

function manifest() {
  return {
    schemaVersion: 'agentweaver.release-acceptance-result/v1',
    resultId: 'result-1',
    resultStatus: 'PASS',
    release: {
      version: '0.34.0',
      releaseId: 'agentweaver-0.34.0',
      deployedRevision: REVISION,
      deploymentIdentity: 'staging-a',
      verifiedAt: '2026-09-22T22:52:06Z',
    },
    feature: {
      featureId: 'issue-1519',
      refs: ['sabbour/agentweaver#1519'],
      behaviorId: 'release-repair-contract',
    },
    challenge: {
      challengeId: 'release-repair-disposition-v1',
      challengeVersion: 1,
      catalogVersion: 1,
      projectId: PROJECT_ID,
      executionId: EXECUTION_ID,
      runId: RUN_ID,
    },
    claimResults: [{
      claimId: 'release-repair-contract-deterministic-v1',
      priority: 'P0',
      verdict: 'PASS',
      requiredSurfaces: ['api', 'ui'],
      surfaceResults: [
        {
          surface: 'api',
          status: 'PASS',
          evidence: [evidence('api')],
        },
        {
          surface: 'ui',
          status: 'PASS',
          evidence: [evidence('ui')],
        },
      ],
      evidenceIntegrity: 'VALID',
      termination: 'COMPLETED',
      cleanup: { required: true, status: 'SUCCEEDED' },
    }],
    evidence: {
      root: 'release-acceptance/result-1',
      items: [evidence('api', 'deployment-record'), evidence('ui', 'deployment-record')],
    },
    anomalies: [],
    patchMilestone: {
      resolutionStatus: 'UNRESOLVED',
      milestoneId: null,
      title: null,
      derivedFromReleaseId: 'agentweaver-0.34.0',
    },
    authority: {
      harnessGitHubWrite: false,
      judgeGitHubWrite: false,
      coordinatorDispositionRequired: true,
    },
  };
}

function makeAbnormal(mutate) {
  const value = manifest();
  mutate(value.claimResults[0], value);
  value.resultStatus = 'ABNORMAL';
  value.anomalies = deriveAnomalies(value);
  return value;
}

test('abnormal predicate is deterministic across every blocking structured field', () => {
  const cases = [
    ['P0_FAIL', (claim) => { claim.verdict = 'FAIL'; }],
    ['P1_PARTIAL', (claim) => { claim.priority = 'P1'; claim.verdict = 'PARTIAL'; }],
    ['INDETERMINATE', (claim) => { claim.verdict = 'INDETERMINATE'; }],
    ['EVIDENCE_MISSING', (claim) => { claim.evidenceIntegrity = 'MISSING'; }],
    ['EVIDENCE_STALE', (claim) => { claim.evidenceIntegrity = 'STALE'; }],
    ['EVIDENCE_MIXED_REVISION', (claim) => { claim.evidenceIntegrity = 'MIXED_REVISION'; }],
    ['TERMINATION_TIMEOUT', (claim) => { claim.termination = 'TIMEOUT'; }],
    ['TERMINATION_BUDGET_TERMINATED', (claim) => { claim.termination = 'BUDGET_TERMINATED'; }],
    ['CLEANUP_FAILED', (claim) => { claim.cleanup.status = 'FAILED'; }],
    ['REQUIRED_SURFACE_ABSENT', (claim) => { claim.surfaceResults.pop(); }],
  ];

  for (const [expected, mutate] of cases) {
    const value = makeAbnormal(mutate);
    assert.equal(isAbnormalReleaseAcceptance(value), true, expected);
    assert.ok(abnormalTriggers(value).some((trigger) => trigger.code === expected), expected);
    const validation = validateReleaseAcceptanceManifest(value);
    assert.equal(validation.ok, true, `${expected}: ${validation.errors.join('\n')}`);
  }
});

test('narrative fields cannot change the abnormal predicate', () => {
  const value = manifest();
  value.resultId = 'FAIL catastrophic broken inconsistent';
  assert.equal(isAbnormalReleaseAcceptance(value), false);
  assert.equal(validateReleaseAcceptanceManifest(value).ok, true);
});

test('release acceptance manifest rejects undeclared narrative properties', () => {
  const value = manifest();
  value.summary = 'A narrative must never become authority-bearing.';
  const result = validateReleaseAcceptanceManifest(value);
  assert.equal(result.ok, false);
  assert.match(result.errors.join('\n'), /additional properties/);
});

test('empty evidence cannot pass and produces structured abnormal triggers', () => {
  const value = manifest();
  value.evidence.items = [];
  value.claimResults[0].surfaceResults.forEach((surface) => { surface.evidence = []; });
  const result = validateReleaseAcceptanceManifest(value);
  assert.equal(result.ok, false);
  assert.ok(result.triggers.some((trigger) => trigger.code === 'EVIDENCE_MISSING'));
});

test('schema validation rejects P0 PARTIAL and every required nested execution field', () => {
  const partial = manifest();
  partial.claimResults[0].verdict = 'PARTIAL';
  assert.equal(validateReleaseAcceptanceManifest(partial).ok, false);

  const requiredFields = [
    ['release', 'deploymentIdentity'],
    ['release', 'verifiedAt'],
    ['challenge', 'catalogVersion'],
    ['challenge', 'projectId'],
    ['challenge', 'executionId'],
    ['challenge', 'runId'],
  ];
  for (const [container, field] of requiredFields) {
    const value = manifest();
    delete value[container][field];
    assert.equal(validateReleaseAcceptanceManifest(value).ok, false, `${container}.${field}`);
  }

  for (const field of [
    'type',
    'path',
    'sha256',
    'deployedRevision',
    'projectId',
    'executionId',
    'runId',
    'challengeId',
    'challengeVersion',
    'surface',
  ]) {
    const value = manifest();
    delete value.claimResults[0].surfaceResults[0].evidence[0][field];
    assert.equal(validateReleaseAcceptanceManifest(value).ok, false, `evidence.${field}`);
  }
});

test('stable anomaly identity deduplicates by feature, challenge version, surface, and claim', () => {
  const base = {
    releaseFeatureId: 'issue-1519',
    challengeId: 'release-repair-disposition-v1',
    challengeVersion: 1,
    surface: 'ui',
    failedClaimId: 'release-repair-contract-deterministic-v1',
  };
  const first = computeAnomalyId(base);
  const second = computeAnomalyId({ ...base });
  assert.equal(first, second);
  assert.match(first, /^ra1:[a-f0-9]{64}$/);
  assert.notEqual(first, computeAnomalyId({ ...base, surface: 'api' }));
  assert.notEqual(first, computeAnomalyId({ ...base, challengeVersion: 2 }));
});

test('manifest rejects narrative status overrides, mixed revisions, and incorrect anomaly identity', () => {
  const value = makeAbnormal((claim) => { claim.verdict = 'FAIL'; });
  value.resultStatus = 'PASS';
  value.evidence.items[0].deployedRevision = 'other-revision';
  value.anomalies[0].anomalyId = `ra1:${'b'.repeat(64)}`;
  const result = validateReleaseAcceptanceManifest(value);
  assert.equal(result.ok, false);
  assert.match(result.errors.join('\n'), /resultStatus must be ABNORMAL/);
  assert.match(result.errors.join('\n'), /invalid stable identity|exactly match anomalies/);
  assert.ok(result.triggers.some((trigger) => trigger.code === 'EVIDENCE_MIXED_REVISION'));
});

function repairRecord() {
  const anomalyTuple = {
    releaseFeatureId: 'issue-1519',
    challengeId: 'release-repair-disposition-v1',
    challengeVersion: 1,
    surface: 'api',
    failedClaimId: 'release-repair-contract-deterministic-v1',
  };
  const anomalyId = computeAnomalyId(anomalyTuple);
  return {
    anomalyId,
    acceptedManifest: {
      resultId: 'result-1',
      anomalyId,
      featureId: anomalyTuple.releaseFeatureId,
      challengeId: anomalyTuple.challengeId,
      challengeVersion: anomalyTuple.challengeVersion,
      claimId: anomalyTuple.failedClaimId,
      surface: anomalyTuple.surface,
      deployedRevision: 'failed-deployed',
    },
    release: {
      releaseId: 'agentweaver-0.34.0',
      deployedRevision: 'failed-deployed',
    },
    challenge: {
      challengeId: 'release-repair-disposition-v1',
      challengeVersion: 1,
      claimIds: ['release-repair-contract-deterministic-v1'],
      requiredSurfaces: ['api', 'ui'],
    },
    classification: 'PRODUCT_DEFECT',
    evidenceRefs: ['failed-result.json'],
    patchMilestone: {
      resolutionStatus: 'RESOLVED',
      milestoneId: '7',
      title: 'v0.34.1',
      derivedFromReleaseId: 'agentweaver-0.34.0',
      anomalyId,
    },
    repairIssue: {
      autoClosePermitted: false,
      issueRef: 'sabbour/agentweaver#1600',
      anomalyId,
    },
    authoritativeRecords: {
      coordinatorDisposition: { recordId: 'coordinator-disposition:1', anomalyId },
      acceptedManifest: { recordId: 'accepted-manifest:1', anomalyId, resultId: 'result-1' },
      deployment: { recordId: 'deployment:1', anomalyId, deployedRevision: 'failed-deployed' },
      repairInclusion: { recordId: 'repair-inclusion:1', anomalyId, deployedRevision: 'repair-deployed' },
      focusedRetest: { recordId: 'focused-retest:1', anomalyId, deployedRevision: 'repair-deployed' },
    },
    repair: {
      sourceRevision: 'repair-source',
      actualDeployedRevision: 'repair-deployed',
      containsRepairRevision: true,
    },
    focusedRetest: {
      deployedRevision: 'repair-deployed',
      allClaimsPass: true,
      allRequiredSurfacesPass: true,
      evidenceIntegrity: 'VALID',
      termination: 'COMPLETED',
      cleanup: 'SUCCEEDED',
      representativeRerunRequired: false,
    },
    disposition: { type: 'REPAIRED' },
  };
}

test('repair closure requires resolved milestone, no auto-close, deployed repair, and clean focused retest', () => {
  const eligible = canCloseRepair(repairRecord());
  assert.equal(eligible.ok, false);
  assert.equal(eligible.structurallyEligible, true);
  assert.match(eligible.errors.join('\n'), /trusted resolver/);

  const autoClose = repairRecord();
  autoClose.repairIssue.autoClosePermitted = true;
  assert.match(canCloseRepair(autoClose).errors.join('\n'), /auto-close/);

  const staleRetest = repairRecord();
  staleRetest.focusedRetest.deployedRevision = 'older';
  assert.match(canCloseRepair(staleRetest).errors.join('\n'), /focused-retest record|deployed repair revision/);

  const missingSurface = repairRecord();
  missingSurface.focusedRetest.allRequiredSurfacesPass = false;
  assert.match(canCloseRepair(missingSurface).errors.join('\n'), /all required surfaces/);
});

test('no-product-repair disposition stays structural and cannot self-authorize closure', () => {
  const record = repairRecord();
  record.disposition = {
    type: 'NO_PRODUCT_REPAIR',
    category: 'HARNESS_CONTRACT_DEFECT',
    rationale: 'The claim exceeded the approved product contract.',
    evidenceRefs: ['contract.json', 'corrected-result.json'],
    affectedClaimIds: ['release-repair-contract-deterministic-v1'],
  };
  assert.equal(validateCoordinatorDisposition(record).ok, true);
  assert.equal(canCloseRepair(record).ok, false);
  assert.equal(canCloseRepair(record).structurallyEligible, true);

  record.patchMilestone.resolutionStatus = 'AMBIGUOUS';
  assert.match(validateCoordinatorDisposition(record).errors.join('\n'), /patch milestone must be resolved/);

  record.patchMilestone.resolutionStatus = 'RESOLVED';
  record.authoritativeRecords.coordinatorDisposition.recordId = '';
  assert.match(validateCoordinatorDisposition(record).errors.join('\n'), /coordinatorDisposition.recordId/);
});

test('closure rejects arbitrary authority strings and anomaly tuple substitution', () => {
  const arbitrary = repairRecord();
  arbitrary.disposition.reviewers = [{ identity: 'admin', decision: 'APPROVE' }];
  arbitrary.disposition.approvedByCoordinator = { identity: 'coordinator' };
  assert.equal(canCloseRepair(arbitrary).ok, false);

  const substituted = repairRecord();
  substituted.acceptedManifest.claimId = 'different-claim';
  assert.match(
    validateCoordinatorDisposition(substituted).errors.join('\n'),
    /immutable accepted manifest anomaly tuple|disposition challenge/,
  );
});

test('post-deployment release gate requires representative and focused exact-revision evidence', () => {
  const catalog = structuredClone(loadChallengeCatalog());
  catalog.releasePolicy.representativeChallengeId = 'release-repair-disposition-v1';
  const featureManifest = {
    schemaVersion: 'agentweaver.release-feature-manifest/v1',
    release: {
      version: '0.34.0',
      deployedRevision: REVISION,
      deploymentIdentity: 'staging-a',
    },
    features: [{
      id: 'issue-1519',
      refs: ['sabbour/agentweaver#1519'],
      shippedBehaviors: [{
        id: 'release-repair-contract',
        claimIds: ['release-repair-contract-deterministic-v1'],
        affectedSurfaces: ['api'],
      }],
    }],
  };
  const result = manifest();
  result.feature.behaviorId = 'release-repair-contract';
  result.evidence.items = [
    evidence('api', 'deployed-release-revision'),
    evidence('api', 'structural-validation'),
    evidence('api', 'artifact-file'),
    evidence('api', 'artifact-hash'),
  ];
  assert.equal(validateReleaseAcceptance({
    catalog,
    featureManifest,
    results: [result],
  }).ok, true);

  result.claimResults[0].surfaceResults[0].evidence = [];
  assert.equal(validateReleaseAcceptance({
    catalog,
    featureManifest,
    results: [result],
  }).ok, false);
});
