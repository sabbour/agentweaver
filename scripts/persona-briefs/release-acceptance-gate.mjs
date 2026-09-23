#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  loadChallengeCatalog,
  selectReleaseChallenges,
  validateReleaseFeatureManifest,
} from './challenge-catalog.mjs';
import { validateReleaseAcceptanceManifest } from './release-acceptance.mjs';

function loadJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, 'utf8'));
}

function findPassingClaim(result, claimContract, requiredSurfaces) {
  const claim = result.claimResults.find((item) => item.claimId === claimContract.id);
  if (!claim || claim.verdict !== 'PASS' || claim.evidenceIntegrity !== 'VALID'
    || claim.termination !== 'COMPLETED') return false;
  if (claim.cleanup.required && claim.cleanup.status !== 'SUCCEEDED') return false;
  const requiredSurfaceEvidence = requiredSurfaces.every((surface) => {
    const surfaceResult = claim.surfaceResults.find((item) => item.surface === surface);
    return surfaceResult?.status === 'PASS' && surfaceResult.evidence.length > 0;
  });
  const evidenceTypes = new Set([
    ...result.evidence.items,
    ...claim.surfaceResults.flatMap((surface) => surface.evidence),
  ].map((item) => item.type));
  return requiredSurfaceEvidence
    && claimContract.requiredEvidence.every((type) => evidenceTypes.has(type));
}

export function validateReleaseAcceptance({
  catalog = loadChallengeCatalog(),
  featureManifest,
  results,
}) {
  const errors = [];
  const featureValidation = validateReleaseFeatureManifest(featureManifest);
  if (!featureValidation.ok) return { ok: false, errors: featureValidation.errors };

  const selection = selectReleaseChallenges(catalog, featureManifest);
  if (!selection.ok) return { ok: false, errors: selection.errors };
  if (!Array.isArray(results) || results.length === 0) {
    return { ok: false, errors: ['release acceptance requires at least one result manifest'] };
  }

  const validResults = [];
  for (const [index, result] of results.entries()) {
    const validation = validateReleaseAcceptanceManifest(result);
    if (!validation.ok) {
      errors.push(...validation.errors.map((error) => `results[${index}]: ${error}`));
      continue;
    }
    if (result.release.version !== featureManifest.release.version) {
      errors.push(`results[${index}] release version does not match the feature manifest`);
    }
    if (result.release.deployedRevision !== featureManifest.release.deployedRevision) {
      errors.push(`results[${index}] deployed revision does not match the feature manifest`);
    }
    if (result.release.deploymentIdentity !== featureManifest.release.deploymentIdentity) {
      errors.push(`results[${index}] deployment identity does not match the feature manifest`);
    }
    const challenge = catalog.entries.find(
      (entry) => entry.id === result.challenge.challengeId,
    );
    if (!challenge
      || result.challenge.challengeVersion !== challenge.version
      || result.challenge.catalogVersion !== catalog.catalogVersion) {
      errors.push(`results[${index}] challenge or catalog version is not the selected catalog contract`);
    }
    if (result.resultStatus !== 'PASS' || result.anomalies.length !== 0) {
      errors.push(`results[${index}] contains unresolved abnormal anomalies`);
    }
    validResults.push(result);
  }

  const representative = catalog.entries.find(
    (entry) => entry.id === selection.representativeChallengeId,
  );
  const representativeResults = validResults.filter(
    (result) => result.challenge.challengeId === representative?.id
      && result.challenge.challengeVersion === representative?.version,
  );
  if (!representative || !representative.claims.every((claim) =>
    representativeResults.some((result) =>
      findPassingClaim(result, claim, representative.surfaces.required)))) {
    errors.push('selected representative challenge lacks complete passing execution evidence');
  }

  for (const focused of selection.focusedChallenges) {
    const challenge = catalog.entries.find((entry) => entry.id === focused.challengeId);
    for (const coverage of focused.coverage) {
      const claimContract = challenge?.claims.find((claim) => claim.id === coverage.claimId);
      const matching = validResults.filter((result) =>
        result.challenge.challengeId === focused.challengeId
        && result.feature.featureId === coverage.featureId
        && result.feature.behaviorId === coverage.behaviorId);
      if (!claimContract || !matching.some((result) =>
        findPassingClaim(result, claimContract, coverage.requiredSurfaces))) {
        errors.push(
          `${coverage.featureId}/${coverage.behaviorId}/${focused.challengeId}/${coverage.claimId} `
          + `lacks passing evidence for surfaces: ${coverage.requiredSurfaces.join(', ')}`,
        );
      }
    }
  }

  return {
    ok: errors.length === 0,
    errors,
    release: featureManifest.release,
    representativeChallengeId: selection.representativeChallengeId,
    focusedChallenges: selection.focusedChallenges,
    resultCount: results.length,
  };
}

export function runReleaseAcceptanceGate({ featureManifestPath, resultPaths }) {
  if (!featureManifestPath) throw new Error('--feature-manifest is required');
  if (!Array.isArray(resultPaths) || resultPaths.length === 0) {
    throw new Error('at least one --result is required');
  }
  const result = validateReleaseAcceptance({
    featureManifest: loadJson(featureManifestPath),
    results: resultPaths.map(loadJson),
  });
  if (!result.ok) throw new Error(result.errors.join('\n'));
  return result;
}

function parseArgs(argv) {
  let featureManifestPath;
  const resultPaths = [];
  for (let index = 0; index < argv.length; index += 1) {
    if (argv[index] === '--feature-manifest') featureManifestPath = argv[++index];
    else if (argv[index] === '--result') resultPaths.push(argv[++index]);
    else throw new Error(`Unknown argument: ${argv[index]}`);
  }
  return { featureManifestPath, resultPaths };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const result = runReleaseAcceptanceGate(parseArgs(process.argv.slice(2)));
    process.stdout.write(`${JSON.stringify({ accepted: true, ...result }, null, 2)}\n`);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}
