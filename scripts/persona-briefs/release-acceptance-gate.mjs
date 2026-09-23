#!/usr/bin/env node
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  loadChallengeCatalog,
  selectReleaseChallenges,
  validateReleaseFeatureManifest,
} from './challenge-catalog.mjs';
import { validateReleaseAcceptanceManifest } from './release-acceptance.mjs';
import { validateJsonSchema } from './schema-validator.mjs';

const PACKAGE_DIR = path.dirname(fileURLToPath(import.meta.url));
export const RELEASE_ACCEPTANCE_BUNDLE_SCHEMA_PATH = path.join(
  PACKAGE_DIR,
  'release-acceptance-bundle-v1.schema.json',
);

function loadJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, 'utf8'));
}

function sha256(filePath) {
  return createHash('sha256').update(fs.readFileSync(filePath)).digest('hex');
}

function resolveBundleFile(bundleRoot, relativePath) {
  if (!relativePath || path.isAbsolute(relativePath)) {
    throw new Error(`bundle artifact path must be relative: ${relativePath}`);
  }
  const root = fs.realpathSync(bundleRoot);
  const candidate = path.resolve(root, relativePath);
  if (!fs.existsSync(candidate)) throw new Error(`bundle artifact is missing: ${relativePath}`);
  const resolved = fs.realpathSync(candidate);
  const fromRoot = path.relative(root, resolved);
  if (!fromRoot || fromRoot.startsWith('..') || path.isAbsolute(fromRoot)) {
    throw new Error(`bundle artifact resolves outside the approved bundle root: ${relativePath}`);
  }
  if (!fs.statSync(resolved).isFile()) throw new Error(`bundle artifact is not a file: ${relativePath}`);
  return resolved;
}

function verifyBundleFile(bundleRoot, file) {
  const resolved = resolveBundleFile(bundleRoot, file.path);
  if (sha256(resolved) !== file.sha256) {
    throw new Error(`bundle artifact hash mismatch: ${file.path}`);
  }
  return resolved;
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
  expectedDeployment = null,
}) {
  const errors = [];
  const declaration = validateReleaseDeclaration({
    catalog,
    featureManifest,
    expectedDeployment,
  });
  if (!declaration.ok) return declaration;
  const { selection } = declaration;
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

export function validateReleaseDeclaration({
  catalog = loadChallengeCatalog(),
  featureManifest,
  expectedDeployment = null,
}) {
  const featureValidation = validateReleaseFeatureManifest(featureManifest);
  if (!featureValidation.ok) return { ok: false, errors: featureValidation.errors };
  const selection = selectReleaseChallenges(catalog, featureManifest);
  if (!selection.ok) return { ok: false, errors: selection.errors };
  const errors = [];
  if (expectedDeployment) {
    for (const field of ['version', 'deployedRevision', 'deploymentIdentity']) {
      if (featureManifest.release[field] !== expectedDeployment[field]) {
        errors.push(`release feature manifest ${field} does not match the verified deployment`);
      }
    }
  }
  return { ok: errors.length === 0, errors, selection, release: featureManifest.release };
}

export function runReleaseDeclarationGate({
  featureManifestPath,
  expectedDeployment,
}) {
  if (!featureManifestPath) throw new Error('--feature-manifest is required');
  const featureManifest = loadJson(featureManifestPath);
  const result = validateReleaseDeclaration({ featureManifest, expectedDeployment });
  if (!result.ok) throw new Error(result.errors.join('\n'));
  return { ...result, featureManifest };
}

export function runReleaseAcceptanceGate({
  featureManifestPath,
  resultPaths,
  expectedDeployment = null,
}) {
  if (!featureManifestPath) throw new Error('--feature-manifest is required');
  if (!Array.isArray(resultPaths) || resultPaths.length === 0) {
    throw new Error('at least one --result is required');
  }
  const result = validateReleaseAcceptance({
    featureManifest: loadJson(featureManifestPath),
    results: resultPaths.map(loadJson),
    expectedDeployment,
  });
  if (!result.ok) throw new Error(result.errors.join('\n'));
  return result;
}

export function runCanonicalReleaseAcceptanceGate({
  featureManifestPath,
  bundlePath,
  expectedDeployment,
  catalog = loadChallengeCatalog(),
}) {
  if (!bundlePath) throw new Error('--acceptance-bundle is required');
  const resolvedBundlePath = fs.realpathSync(bundlePath);
  const bundleRoot = path.dirname(resolvedBundlePath);
  const bundle = loadJson(resolvedBundlePath);
  const schema = validateJsonSchema(
    RELEASE_ACCEPTANCE_BUNDLE_SCHEMA_PATH,
    bundle,
    'acceptance bundle',
  );
  if (!schema.ok) throw new Error(schema.errors.join('\n'));
  for (const field of ['version', 'deployedRevision', 'deploymentIdentity']) {
    if (bundle.deployment[field] !== expectedDeployment[field]) {
      throw new Error(`acceptance bundle ${field} does not match the verified deployment`);
    }
  }

  const artifactByPath = new Map();
  for (const artifact of bundle.artifacts) {
    if (artifactByPath.has(artifact.path)) {
      throw new Error(`acceptance bundle contains duplicate artifact path: ${artifact.path}`);
    }
    verifyBundleFile(bundleRoot, artifact);
    artifactByPath.set(artifact.path, artifact);
  }

  const results = bundle.results.map((reference) => {
    if (reference.mediaType !== 'application/json') {
      throw new Error(`result manifest must use application/json: ${reference.path}`);
    }
    const result = loadJson(verifyBundleFile(bundleRoot, reference));
    if (result.resultId !== reference.resultId
      || result.challenge?.challengeId !== reference.scenarioId
      || result.challenge?.executionId !== reference.executionId) {
      throw new Error(`result manifest IDs do not match bundle reference: ${reference.path}`);
    }
    const evidenceItems = [
      ...(result.evidence?.items ?? []),
      ...(result.claimResults ?? []).flatMap((claim) =>
        (claim.surfaceResults ?? []).flatMap((surface) => surface.evidence ?? [])),
    ];
    for (const evidence of evidenceItems) {
      if (evidence.bundleId !== bundle.bundleId
        || evidence.batchId !== bundle.batchId
        || evidence.resultId !== result.resultId
        || evidence.scenarioId !== result.challenge.challengeId
        || evidence.executionId !== result.challenge.executionId) {
        throw new Error(`evidence IDs do not match canonical bundle/result: ${evidence.path}`);
      }
      const artifact = artifactByPath.get(evidence.path);
      if (!artifact) throw new Error(`evidence artifact is absent from canonical bundle: ${evidence.path}`);
      if (artifact.sha256 !== evidence.sha256
        || artifact.mediaType !== evidence.mediaType
        || artifact.resultId !== evidence.resultId
        || artifact.scenarioId !== evidence.scenarioId
        || artifact.executionId !== evidence.executionId) {
        throw new Error(`evidence metadata does not match canonical bundle: ${evidence.path}`);
      }
    }
    return result;
  });

  const result = validateReleaseAcceptance({
    catalog,
    featureManifest: loadJson(featureManifestPath),
    results,
    expectedDeployment,
  });
  if (!result.ok) throw new Error(result.errors.join('\n'));
  return {
    ...result,
    authoritative: true,
    bundleId: bundle.bundleId,
    batchId: bundle.batchId,
    bundlePath: resolvedBundlePath,
  };
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
    process.stdout.write(`${JSON.stringify({
      valid: true,
      authoritative: false,
      note: 'Release acceptance closes only at azure:deploy-from-release.',
      ...result,
    }, null, 2)}\n`);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}
