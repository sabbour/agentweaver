import { createHash } from 'node:crypto';

const REQUIRED_MANIFEST_KEYS = [
  'schemaVersion',
  'resultId',
  'release',
  'feature',
  'challenge',
  'claimResults',
  'evidence',
  'patchMilestone',
  'authority',
];
const MANIFEST_KEYS = new Set([...REQUIRED_MANIFEST_KEYS, 'resultStatus', 'anomalies']);
const RELEASE_KEYS = new Set(['version', 'releaseId', 'deployedRevision', 'deploymentIdentity', 'verifiedAt']);
const FEATURE_KEYS = new Set(['featureId', 'refs', 'behaviorId']);
const CHALLENGE_KEYS = new Set(['challengeId', 'challengeVersion', 'catalogVersion', 'executionId']);
const CLAIM_KEYS = new Set([
  'claimId',
  'priority',
  'verdict',
  'requiredSurfaces',
  'surfaceResults',
  'evidenceIntegrity',
  'termination',
  'cleanup',
]);
const SURFACE_RESULT_KEYS = new Set(['surface', 'status', 'evidence']);
const EVIDENCE_ITEM_KEYS = new Set(['path', 'sha256', 'deployedRevision']);
const EVIDENCE_KEYS = new Set(['root', 'items']);
const ANOMALY_KEYS = new Set(['anomalyId', 'claimId', 'surface', 'triggerCodes', 'blocksReleaseAcceptance']);
const PATCH_KEYS = new Set(['resolutionStatus', 'milestoneId', 'title', 'derivedFromReleaseId']);
const AUTHORITY_KEYS = new Set(['harnessGitHubWrite', 'judgeGitHubWrite', 'coordinatorDispositionRequired']);
const EVIDENCE_INTEGRITY_FAILURES = new Set(['MISSING', 'STALE', 'MIXED_REVISION', 'HASH_MISMATCH']);
const ABNORMAL_TERMINATIONS = new Set(['TIMEOUT', 'BUDGET_TERMINATED', 'INFRASTRUCTURE_TERMINATED']);
const ABNORMAL_SURFACE_STATUSES = new Set([
  'PARTIAL',
  'FAIL',
  'INDETERMINATE',
  'NOT_RUN',
  'UNAVAILABLE_EXPECTED',
  'UNAVAILABLE_UNEXPECTED',
]);

function isObject(value) {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function nonEmpty(value) {
  return typeof value === 'string' && value.trim().length > 0;
}

function validateClosed(value, allowed, label, errors) {
  if (!isObject(value)) {
    errors.push(`${label} must be an object`);
    return false;
  }
  for (const key of Object.keys(value)) {
    if (!allowed.has(key)) errors.push(`${label} contains unsupported property "${key}"`);
  }
  return true;
}

function addTrigger(triggers, code, claimId, surface = null) {
  const key = `${code}:${claimId ?? ''}:${surface ?? ''}`;
  if (!triggers.some((item) => item.key === key)) triggers.push({ key, code, claimId, surface });
}

export function computeAnomalyId({
  releaseFeatureId,
  challengeId,
  challengeVersion,
  surface,
  failedClaimId,
}) {
  const tuple = {
    releaseFeatureId,
    challengeId,
    challengeVersion,
    surface: String(surface ?? '').toLowerCase(),
    failedClaimId,
  };
  for (const [key, value] of Object.entries(tuple)) {
    if (!nonEmpty(value) && !(key === 'challengeVersion' && Number.isInteger(value))) {
      throw new Error(`anomaly identity field "${key}" is required`);
    }
  }
  const canonical = JSON.stringify(tuple);
  return `ra1:${createHash('sha256').update(canonical).digest('hex')}`;
}

export function abnormalTriggers(manifest) {
  const triggers = [];
  const requiredRevision = manifest?.release?.deployedRevision;
  for (const claim of manifest?.claimResults ?? []) {
    if (claim.priority === 'P0' && claim.verdict === 'FAIL') {
      addTrigger(triggers, 'P0_FAIL', claim.claimId);
    }
    if (claim.priority === 'P1' && ['PARTIAL', 'FAIL'].includes(claim.verdict)) {
      addTrigger(triggers, `P1_${claim.verdict}`, claim.claimId);
    }
    if (claim.verdict === 'INDETERMINATE') addTrigger(triggers, 'INDETERMINATE', claim.claimId);
    if (EVIDENCE_INTEGRITY_FAILURES.has(claim.evidenceIntegrity)) {
      addTrigger(triggers, `EVIDENCE_${claim.evidenceIntegrity}`, claim.claimId);
    }
    if (ABNORMAL_TERMINATIONS.has(claim.termination)) {
      addTrigger(triggers, `TERMINATION_${claim.termination}`, claim.claimId);
    }
    if (claim.cleanup?.required === true && ['FAILED', 'INDETERMINATE'].includes(claim.cleanup.status)) {
      addTrigger(triggers, `CLEANUP_${claim.cleanup.status}`, claim.claimId);
    }

    const surfaceResults = new Map((claim.surfaceResults ?? []).map((result) => [result.surface, result]));
    for (const surface of claim.requiredSurfaces ?? []) {
      const result = surfaceResults.get(surface);
      if (!result) {
        addTrigger(triggers, 'REQUIRED_SURFACE_ABSENT', claim.claimId, surface);
      } else if (ABNORMAL_SURFACE_STATUSES.has(result.status)) {
        addTrigger(triggers, `SURFACE_${result.status}`, claim.claimId, surface);
      }
    }
    for (const result of claim.surfaceResults ?? []) {
      for (const evidence of result.evidence ?? []) {
        if (!nonEmpty(evidence.path) || !nonEmpty(evidence.sha256)) {
          addTrigger(triggers, 'EVIDENCE_MISSING', claim.claimId, result.surface);
        }
        if (nonEmpty(requiredRevision) && evidence.deployedRevision !== requiredRevision) {
          addTrigger(triggers, 'EVIDENCE_MIXED_REVISION', claim.claimId, result.surface);
        }
      }
    }
  }
  for (const evidence of manifest?.evidence?.items ?? []) {
    if (!nonEmpty(evidence.path) || !nonEmpty(evidence.sha256)) {
      for (const claim of manifest?.claimResults ?? []) addTrigger(triggers, 'EVIDENCE_MISSING', claim.claimId);
    }
    if (nonEmpty(requiredRevision) && evidence.deployedRevision !== requiredRevision) {
      for (const claim of manifest?.claimResults ?? []) addTrigger(triggers, 'EVIDENCE_MIXED_REVISION', claim.claimId);
    }
  }
  return triggers.map(({ key, ...trigger }) => trigger);
}

export function isAbnormalReleaseAcceptance(manifest) {
  return abnormalTriggers(manifest).length > 0;
}

export function deriveAnomalies(manifest) {
  const claims = new Map((manifest?.claimResults ?? []).map((claim) => [claim.claimId, claim]));
  const grouped = new Map();
  for (const trigger of abnormalTriggers(manifest)) {
    const surfaces = trigger.surface
      ? [trigger.surface]
      : claims.get(trigger.claimId)?.requiredSurfaces ?? [];
    for (const surface of surfaces) {
      const anomalyId = computeAnomalyId({
        releaseFeatureId: manifest.feature?.featureId,
        challengeId: manifest.challenge?.challengeId,
        challengeVersion: manifest.challenge?.challengeVersion,
        surface,
        failedClaimId: trigger.claimId,
      });
      if (!grouped.has(anomalyId)) {
        grouped.set(anomalyId, {
          anomalyId,
          claimId: trigger.claimId,
          surface,
          triggerCodes: [],
          blocksReleaseAcceptance: true,
        });
      }
      const anomaly = grouped.get(anomalyId);
      if (!anomaly.triggerCodes.includes(trigger.code)) anomaly.triggerCodes.push(trigger.code);
    }
  }
  return [...grouped.values()]
    .map((anomaly) => ({ ...anomaly, triggerCodes: anomaly.triggerCodes.sort() }))
    .sort((a, b) => a.anomalyId.localeCompare(b.anomalyId));
}

export function validateReleaseAcceptanceManifest(manifest) {
  const errors = [];
  if (!isObject(manifest)) return { ok: false, errors: ['manifest must be an object'] };
  validateClosed(manifest, MANIFEST_KEYS, 'manifest', errors);
  for (const key of REQUIRED_MANIFEST_KEYS) {
    if (!(key in manifest)) errors.push(`manifest.${key} is required`);
  }
  validateClosed(manifest.release, RELEASE_KEYS, 'manifest.release', errors);
  validateClosed(manifest.feature, FEATURE_KEYS, 'manifest.feature', errors);
  validateClosed(manifest.challenge, CHALLENGE_KEYS, 'manifest.challenge', errors);
  validateClosed(manifest.evidence, EVIDENCE_KEYS, 'manifest.evidence', errors);
  validateClosed(manifest.patchMilestone, PATCH_KEYS, 'manifest.patchMilestone', errors);
  validateClosed(manifest.authority, AUTHORITY_KEYS, 'manifest.authority', errors);
  for (const [index, item] of (manifest.evidence?.items ?? []).entries()) {
    validateClosed(item, EVIDENCE_ITEM_KEYS, `manifest.evidence.items[${index}]`, errors);
  }
  for (const [claimIndex, claim] of (manifest.claimResults ?? []).entries()) {
    validateClosed(claim, CLAIM_KEYS, `manifest.claimResults[${claimIndex}]`, errors);
    validateClosed(
      claim.cleanup,
      new Set(['required', 'status']),
      `manifest.claimResults[${claimIndex}].cleanup`,
      errors,
    );
    for (const [surfaceIndex, surface] of (claim.surfaceResults ?? []).entries()) {
      validateClosed(
        surface,
        SURFACE_RESULT_KEYS,
        `manifest.claimResults[${claimIndex}].surfaceResults[${surfaceIndex}]`,
        errors,
      );
      for (const [evidenceIndex, evidence] of (surface.evidence ?? []).entries()) {
        validateClosed(
          evidence,
          EVIDENCE_ITEM_KEYS,
          `manifest.claimResults[${claimIndex}].surfaceResults[${surfaceIndex}].evidence[${evidenceIndex}]`,
          errors,
        );
      }
    }
  }
  for (const [index, anomaly] of (manifest.anomalies ?? []).entries()) {
    validateClosed(anomaly, ANOMALY_KEYS, `manifest.anomalies[${index}]`, errors);
  }
  if (manifest.schemaVersion !== 'agentweaver.release-acceptance-result/v1') {
    errors.push('manifest.schemaVersion must equal agentweaver.release-acceptance-result/v1');
  }
  if (!nonEmpty(manifest.resultId)) errors.push('manifest.resultId must be a non-empty string');
  if (!nonEmpty(manifest.release?.releaseId)) errors.push('manifest.release.releaseId is required');
  if (!nonEmpty(manifest.release?.version)) errors.push('manifest.release.version is required');
  if (!nonEmpty(manifest.release?.deployedRevision)) errors.push('manifest.release.deployedRevision is required');
  if (!nonEmpty(manifest.feature?.featureId)) errors.push('manifest.feature.featureId is required');
  if (!nonEmpty(manifest.challenge?.challengeId)) errors.push('manifest.challenge.challengeId is required');
  if (!Number.isInteger(manifest.challenge?.challengeVersion)) {
    errors.push('manifest.challenge.challengeVersion must be an integer');
  }
  if (!Array.isArray(manifest.claimResults) || manifest.claimResults.length === 0) {
    errors.push('manifest.claimResults must be a non-empty array');
  }
  for (const [index, claim] of (manifest.claimResults ?? []).entries()) {
    const allowedVerdicts = claim.priority === 'P0'
      ? ['PASS', 'FAIL', 'INDETERMINATE']
      : claim.priority === 'P1'
        ? ['PASS', 'PARTIAL', 'FAIL', 'INDETERMINATE']
        : [];
    if (!allowedVerdicts.includes(claim.verdict)) {
      errors.push(`manifest.claimResults[${index}].verdict is invalid for ${claim.priority ?? 'unknown priority'}`);
    }
    if (claim.cleanup?.required === true && claim.cleanup?.status === 'NOT_REQUIRED') {
      errors.push(`manifest.claimResults[${index}].cleanup cannot be NOT_REQUIRED when cleanup is required`);
    }
    if (claim.cleanup?.required === false && claim.cleanup?.status !== 'NOT_REQUIRED') {
      errors.push(`manifest.claimResults[${index}].cleanup must be NOT_REQUIRED when cleanup is not required`);
    }
  }
  if (manifest.authority?.harnessGitHubWrite !== false || manifest.authority?.judgeGitHubWrite !== false) {
    errors.push('Harness and Judge GitHub write authority must be false');
  }
  if (manifest.authority?.coordinatorDispositionRequired !== true) {
    errors.push('manifest.authority.coordinatorDispositionRequired must be true');
  }

  const triggers = abnormalTriggers(manifest);
  const expectedStatus = triggers.length ? 'ABNORMAL' : 'PASS';
  if (manifest.resultStatus !== expectedStatus) {
    errors.push(`manifest.resultStatus must be ${expectedStatus} from structured fields`);
  }
  const expectedAnomalies = deriveAnomalies(manifest);
  const anomalyIds = new Set();
  for (const anomaly of manifest.anomalies ?? []) {
    const expected = computeAnomalyId({
      releaseFeatureId: manifest.feature?.featureId,
      challengeId: manifest.challenge?.challengeId,
      challengeVersion: manifest.challenge?.challengeVersion,
      surface: anomaly.surface,
      failedClaimId: anomaly.claimId,
    });
    if (anomaly.anomalyId !== expected) errors.push(`anomaly ${anomaly.claimId}/${anomaly.surface} has invalid stable identity`);
    if (anomalyIds.has(anomaly.anomalyId)) errors.push(`duplicate anomalyId "${anomaly.anomalyId}"`);
    anomalyIds.add(anomaly.anomalyId);
  }
  if (!Array.isArray(manifest.anomalies)) {
    errors.push('manifest.anomalies must be an array');
  } else {
    const actual = [...manifest.anomalies]
      .map((anomaly) => ({ ...anomaly, triggerCodes: [...(anomaly.triggerCodes ?? [])].sort() }))
      .sort((a, b) => String(a.anomalyId).localeCompare(String(b.anomalyId)));
    if (JSON.stringify(actual) !== JSON.stringify(expectedAnomalies)) {
      errors.push('manifest.anomalies must exactly match anomalies derived from structured claim results');
    }
  }
  return { ok: errors.length === 0, errors, triggers };
}

export function validateCoordinatorDisposition(record) {
  const errors = [];
  if (!isObject(record)) return { ok: false, errors: ['disposition record must be an object'] };
  if (!/^ra1:[a-f0-9]{64}$/.test(record.anomalyId ?? '')) {
    errors.push('disposition record requires a stable ra1 anomalyId');
  }
  if (!nonEmpty(record.release?.releaseId) || !nonEmpty(record.release?.deployedRevision)) {
    errors.push('disposition record requires the release under acceptance and its deployed revision');
  }
  if (!nonEmpty(record.challenge?.challengeId)
    || !Number.isInteger(record.challenge?.challengeVersion)
    || !Array.isArray(record.challenge?.claimIds)
    || record.challenge.claimIds.length === 0
    || !Array.isArray(record.challenge?.requiredSurfaces)
    || record.challenge.requiredSurfaces.length === 0) {
    errors.push('disposition record requires challenge version, failed claims, and required surfaces');
  }
  if (!nonEmpty(record.classification)) errors.push('disposition record requires a validated classification');
  if (!Array.isArray(record.evidenceRefs) || record.evidenceRefs.length === 0) {
    errors.push('disposition record requires evidence references');
  }
  if (record.patchMilestone?.resolutionStatus !== 'RESOLVED') {
    errors.push('patch milestone must be resolved before disposition');
  }
  if (!nonEmpty(record.patchMilestone?.milestoneId) || !nonEmpty(record.patchMilestone?.title)) {
    errors.push('resolved patch milestone requires milestoneId and title');
  }
  if (!nonEmpty(record.patchMilestone?.derivedFromReleaseId)) {
    errors.push('patch milestone must be derived from the release under acceptance');
  } else if (record.patchMilestone.derivedFromReleaseId !== record.release?.releaseId) {
    errors.push('patch milestone release derivation must match the release under acceptance');
  }
  if (record.repairIssue?.autoClosePermitted !== false) {
    errors.push('repair issue merge auto-close must be disabled');
  }
  if (!nonEmpty(record.repairIssue?.owner?.identity)) errors.push('repair issue requires an assigned owner identity');

  if (record.disposition?.type === 'NO_PRODUCT_REPAIR') {
    const allowed = new Set([
      'HARNESS_CONTRACT_DEFECT',
      'CATALOG_CLAIM_DEFECT',
      'EVIDENCE_PROCESSING_DEFECT',
      'DOCUMENTED_ENVIRONMENT_NOT_APPLICABLE',
      'CLAIM_WITHDRAWN_BEFORE_RELEASE',
      'DOCUMENTED_EXTERNAL_DEPENDENCY',
      'DUPLICATE_ROOT_CAUSE',
      'VERIFIED_FALSE_POSITIVE',
    ]);
    if (!allowed.has(record.disposition.category)) errors.push('no-product-repair disposition category is invalid');
    if (!nonEmpty(record.disposition.rationale)) errors.push('no-product-repair disposition requires rationale');
    if (!Array.isArray(record.disposition.evidenceRefs) || record.disposition.evidenceRefs.length === 0) {
      errors.push('no-product-repair disposition requires evidence references');
    }
    if (!Array.isArray(record.disposition.affectedClaimIds)
      || record.disposition.affectedClaimIds.length === 0) {
      errors.push('no-product-repair disposition requires affected claim IDs');
    }
    if (!Array.isArray(record.disposition.reviewers) || record.disposition.reviewers.length === 0) {
      errors.push('no-product-repair disposition requires reviewer identity');
    } else if (record.disposition.reviewers.some((reviewer) =>
      !nonEmpty(reviewer.identity) || reviewer.decision !== 'APPROVE')) {
      errors.push('each no-product-repair reviewer must have an identity and APPROVE decision');
    }
    if (!nonEmpty(record.disposition.approvedByCoordinator?.identity)) {
      errors.push('no-product-repair disposition requires coordinator approval');
    }
  }
  return { ok: errors.length === 0, errors };
}

export function canCloseRepair(record) {
  const disposition = validateCoordinatorDisposition(record);
  if (!disposition.ok) return disposition;
  if (record.disposition?.type === 'NO_PRODUCT_REPAIR') return { ok: true, errors: [] };
  const errors = [];
  if (record.repair?.containsRepairRevision !== true) errors.push('deployed revision must contain the intended repair revision');
  if (!nonEmpty(record.repair?.actualDeployedRevision)) errors.push('actual deployed repair revision is required');
  if (record.focusedRetest?.deployedRevision !== record.repair?.actualDeployedRevision) {
    errors.push('focused retest must run against the deployed repair revision');
  }
  if (record.focusedRetest?.allClaimsPass !== true) errors.push('focused retest must pass all affected claims');
  if (record.focusedRetest?.allRequiredSurfacesPass !== true) {
    errors.push('focused retest must pass all required surfaces');
  }
  if (record.focusedRetest?.evidenceIntegrity !== 'VALID') errors.push('focused retest evidence must be valid');
  if (record.focusedRetest?.termination !== 'COMPLETED') errors.push('focused retest must complete');
  if (record.focusedRetest?.cleanup !== 'SUCCEEDED') errors.push('focused retest cleanup must succeed');
  if (record.focusedRetest?.representativeRerunRequired === true
    && record.focusedRetest?.representativeRerunPassed !== true) {
    errors.push('representative integration rerun must pass');
  }
  return { ok: errors.length === 0, errors };
}
