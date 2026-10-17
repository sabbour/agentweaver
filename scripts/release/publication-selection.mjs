// Select changed NuGet versions from a verified application receipt; existing package versions cannot be pushed again.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { resolveCiComparisonBase } from './changesets.mjs';
import { verifyPreparation } from './preparation.mjs';
import { validateFile } from './validate.mjs';

const sha = /^[a-f0-9]{40}$/;

function fail(location, message) {
  throw new Error(`${location}: ${message}`);
}

export function resolveVersionApplicationSourceSha(root, { eventName, testedSourceSha, baseSha }) {
  if (!sha.test(testedSourceSha ?? '') || !sha.test(baseSha ?? '')) {
    fail('release source', 'expected full tested-source and comparison-base commit SHAs');
  }
  if (eventName === 'push' || eventName === 'workflow_dispatch') {
    return resolveCiComparisonBase(root, {
      eventName, testedSourceSha, baseSha, sourceHeadSha: '',
    });
  }
  fail('release source', 'publication requires a v1 push or workflow_dispatch event');
}

export function selectPublicationPackageComponents(manifest, plan) {
  if (!Array.isArray(manifest?.components) || !Array.isArray(plan?.components)) {
    fail('release selection', 'expected manifest components and a verified version plan');
  }
  const byId = new Map(manifest.components.map((component) => [component.id, component]));
  if (byId.size !== manifest.components.length) fail('release selection', 'manifest component IDs must be unique');
  const seen = new Set();
  const packages = [];
  for (const entry of plan.components) {
    const component = byId.get(entry?.id);
    if (!component || seen.has(entry.id) || typeof entry.toVersion !== 'string' ||
        component.version !== entry.toVersion) {
      fail('release selection', `version plan does not match the current manifest for ${entry?.id ?? 'an unknown component'}`);
    }
    seen.add(entry.id);
    if (component.kind !== 'service') packages.push(component.id);
  }
  return packages.sort();
}

export function readPublicationPackageComponents(root, receiptSourceSha, manifestPath = 'releases/foundation.json') {
  if (!sha.test(receiptSourceSha ?? '')) fail('release receipt', 'expected a full source commit SHA');
  root = path.resolve(root);
  const resolvedManifest = path.resolve(root, manifestPath);
  const relativeManifest = path.relative(root, resolvedManifest).replaceAll('\\', '/');
  if (relativeManifest !== manifestPath || !/^releases\/[a-z0-9][a-z0-9-]*\.json$/.test(relativeManifest)) {
    fail('manifest', 'expected a repository-relative release composition path');
  }
  const receiptPath = path.join(root, 'releases', 'receipts', `${receiptSourceSha}.json`);
  let receipt;
  try {
    receipt = JSON.parse(readFileSync(receiptPath, 'utf8'));
  } catch (error) {
    fail(receiptPath, `cannot read a valid version-application receipt: ${error.message}`);
  }
  if (receipt.sourceSha !== receiptSourceSha || receipt.manifestPath !== relativeManifest) {
    fail(receiptPath, 'receipt source and manifest path must match the exact release comparison');
  }
  const verified = verifyPreparation(root, receipt);
  const currentManifestText = readFileSync(resolvedManifest, 'utf8');
  if (currentManifestText !== verified.files.get(relativeManifest)) {
    fail(relativeManifest, 'current composition differs from the source-verified version application');
  }
  const manifest = validateFile(resolvedManifest, { root });
  return selectPublicationPackageComponents(manifest, verified.plan);
}
