import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { validateJsonSchema } from './schema-validator.mjs';

const PACKAGE_DIR = path.dirname(fileURLToPath(import.meta.url));
export const RELEASE_ACCEPTANCE_SCHEMA_PATH = path.join(
  PACKAGE_DIR,
  'release-acceptance-result-v1.schema.json',
);
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
  const challenge = manifest?.challenge;
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
      } else if (!Array.isArray(result.evidence) || result.evidence.length === 0) {
        addTrigger(triggers, 'EVIDENCE_MISSING', claim.claimId, surface);
      }
    }
    for (const result of claim.surfaceResults ?? []) {
      for (const evidence of result.evidence ?? []) {
        if (!nonEmpty(evidence.type) || !nonEmpty(evidence.path) || !nonEmpty(evidence.sha256)) {
          addTrigger(triggers, 'EVIDENCE_MISSING', claim.claimId, result.surface);
        }
        if (nonEmpty(requiredRevision) && evidence.deployedRevision !== requiredRevision) {
          addTrigger(triggers, 'EVIDENCE_MIXED_REVISION', claim.claimId, result.surface);
        }
        if (evidence.projectId !== challenge?.projectId
          || evidence.executionId !== challenge?.executionId
          || evidence.runId !== challenge?.runId
          || evidence.challengeId !== challenge?.challengeId
          || evidence.challengeVersion !== challenge?.challengeVersion
          || evidence.catalogVersion !== challenge?.catalogVersion
          || evidence.surface !== result.surface) {
          addTrigger(triggers, 'EVIDENCE_BINDING_MISMATCH', claim.claimId, result.surface);
        }
      }
    }
  }
  if (!Array.isArray(manifest?.evidence?.items) || manifest.evidence.items.length === 0) {
    for (const claim of manifest?.claimResults ?? []) {
      addTrigger(triggers, 'EVIDENCE_MISSING', claim.claimId);
    }
  }
  for (const evidence of manifest?.evidence?.items ?? []) {
    if (!nonEmpty(evidence.type) || !nonEmpty(evidence.path) || !nonEmpty(evidence.sha256)) {
      for (const claim of manifest?.claimResults ?? []) addTrigger(triggers, 'EVIDENCE_MISSING', claim.claimId);
    }
    if (nonEmpty(requiredRevision) && evidence.deployedRevision !== requiredRevision) {
      for (const claim of manifest?.claimResults ?? []) addTrigger(triggers, 'EVIDENCE_MIXED_REVISION', claim.claimId);
    }
    if (evidence.projectId !== challenge?.projectId
      || evidence.executionId !== challenge?.executionId
      || evidence.runId !== challenge?.runId
      || evidence.challengeId !== challenge?.challengeId
      || evidence.challengeVersion !== challenge?.challengeVersion
      || evidence.catalogVersion !== challenge?.catalogVersion) {
      for (const claim of manifest?.claimResults ?? []) {
        addTrigger(triggers, 'EVIDENCE_BINDING_MISMATCH', claim.claimId, evidence.surface);
      }
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
  const schemaValidation = validateJsonSchema(RELEASE_ACCEPTANCE_SCHEMA_PATH, manifest, 'manifest');
  if (!schemaValidation.ok) {
    return { ok: false, errors: schemaValidation.errors, triggers: abnormalTriggers(manifest) };
  }
  const triggers = abnormalTriggers(manifest);
  const errors = [];
  for (const [index, claim] of manifest.claimResults.entries()) {
    if (claim.cleanup.required && claim.cleanup.status === 'NOT_REQUIRED') {
      errors.push(`manifest.claimResults[${index}].cleanup cannot be NOT_REQUIRED when cleanup is required`);
    }
    if (!claim.cleanup.required && claim.cleanup.status !== 'NOT_REQUIRED') {
      errors.push(`manifest.claimResults[${index}].cleanup must be NOT_REQUIRED when cleanup is not required`);
    }
  }
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
  const binding = record.acceptedManifest;
  if (!nonEmpty(binding?.resultId)
    || !nonEmpty(binding?.featureId)
    || !nonEmpty(binding?.challengeId)
    || !Number.isInteger(binding?.challengeVersion)
    || !nonEmpty(binding?.claimId)
    || !nonEmpty(binding?.surface)
    || !nonEmpty(binding?.deployedRevision)) {
    errors.push('accepted manifest binding requires result, feature, challenge, claim, surface, and revision');
  } else {
    const expectedAnomalyId = computeAnomalyId({
      releaseFeatureId: binding.featureId,
      challengeId: binding.challengeId,
      challengeVersion: binding.challengeVersion,
      surface: binding.surface,
      failedClaimId: binding.claimId,
    });
    if (record.anomalyId !== expectedAnomalyId || binding.anomalyId !== expectedAnomalyId) {
      errors.push('anomalyId must match the immutable accepted manifest anomaly tuple');
    }
  }
  if (!nonEmpty(record.release?.releaseId) || !nonEmpty(record.release?.deployedRevision)) {
    errors.push('disposition record requires the release under acceptance and its deployed revision');
  } else if (binding?.deployedRevision !== record.release.deployedRevision) {
    errors.push('accepted manifest revision must match the release under acceptance');
  }
  if (!nonEmpty(record.challenge?.challengeId)
    || !Number.isInteger(record.challenge?.challengeVersion)
    || !Array.isArray(record.challenge?.claimIds)
    || record.challenge.claimIds.length === 0
    || !Array.isArray(record.challenge?.requiredSurfaces)
    || record.challenge.requiredSurfaces.length === 0) {
    errors.push('disposition record requires challenge version, failed claims, and required surfaces');
  } else if (binding?.challengeId !== record.challenge.challengeId
    || binding?.challengeVersion !== record.challenge.challengeVersion
    || !record.challenge.claimIds.includes(binding?.claimId)
    || !record.challenge.requiredSurfaces.includes(binding?.surface)) {
    errors.push('accepted anomaly tuple must match the disposition challenge, claim, and surface');
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
  if (record.patchMilestone?.anomalyId !== record.anomalyId) {
    errors.push('patch milestone must be bound to the accepted anomalyId');
  }
  if (record.repairIssue?.autoClosePermitted !== false || !nonEmpty(record.repairIssue?.issueRef)) {
    errors.push('repair issue merge auto-close must be disabled');
  }
  if (record.repairIssue?.anomalyId !== record.anomalyId) {
    errors.push('repair issue must be bound to the accepted anomalyId');
  }
  const authorityRecords = record.authoritativeRecords;
  for (const field of [
    'coordinatorDisposition',
    'acceptedManifest',
    'deployment',
    'repairInclusion',
    'focusedRetest',
  ]) {
    const authoritative = authorityRecords?.[field];
    if (!nonEmpty(authoritative?.recordId)) {
      errors.push(`authoritativeRecords.${field}.recordId is required`);
    }
    if (authoritative?.anomalyId !== record.anomalyId) {
      errors.push(`authoritativeRecords.${field} must be bound to the accepted anomalyId`);
    }
  }
  if (authorityRecords?.acceptedManifest?.resultId !== binding?.resultId) {
    errors.push('authoritative accepted-manifest record must reference the accepted resultId');
  }
  if (authorityRecords?.deployment?.deployedRevision !== record.release?.deployedRevision) {
    errors.push('authoritative deployment record must reference the failed deployed revision');
  }
  if (record.disposition?.type !== 'NO_PRODUCT_REPAIR') {
    if (authorityRecords?.repairInclusion?.deployedRevision !== record.repair?.actualDeployedRevision) {
      errors.push('authoritative repair-inclusion record must reference the deployed repair revision');
    }
    if (authorityRecords?.focusedRetest?.deployedRevision !== record.focusedRetest?.deployedRevision) {
      errors.push('authoritative focused-retest record must reference the retested revision');
    }
  }

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
  }
  return { ok: errors.length === 0, errors };
}

export function canCloseRepair(record) {
  const disposition = validateCoordinatorDisposition(record);
  if (!disposition.ok) return { ok: false, structurallyEligible: false, errors: disposition.errors };
  const errors = [];
  if (record.disposition?.type !== 'NO_PRODUCT_REPAIR') {
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
  }
  const structurallyEligible = errors.length === 0;
  return {
    ok: false,
    structurallyEligible,
    errors: structurallyEligible
      ? ['closure requires a trusted resolver to authenticate authoritative record references']
      : errors,
  };
}
