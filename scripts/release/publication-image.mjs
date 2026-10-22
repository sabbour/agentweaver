import { isDeepStrictEqual } from 'node:util';
import { componentImageRepository, componentImageTag } from './pack.mjs';

const SHA = /^[a-f0-9]{40}$/;
const HEX_SHA256 = /^[a-f0-9]{64}$/;
const IMAGE_DIGEST = /^sha256:[a-f0-9]{64}$/;

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function requireAncestor(exec, root, sourceSha, target, description, fail) {
  // A source-bound digest must come from the publication's Git history, not just a receipt field.
  const result = exec('git', ['merge-base', '--is-ancestor', sourceSha, target], { cwd: root, check: false });
  if (result.status !== 0) fail(`${description} ${sourceSha} is not an ancestor of ${target}.`);
}

/**
 * Selects one image whose manifest version, archive hash, build tag, and source tree agree.
 * Reused images retain their original source commit; their receipt provenance is never inferred.
 */
export function selectPublishedComponentImage(publication, manifest, componentId, {
  git, exec, root, label = componentId,
}) {
  const fail = message => {
    throw new Error(`${label} publication image: ${message}`);
  };
  if (!isRecord(publication) || publication.schemaVersion !== 1 ||
      !SHA.test(publication.sourceSha ?? '') || publication.status !== 'published' ||
      !HEX_SHA256.test(publication.provenanceSha256 ?? '') ||
      publication.mode === 'images-only' ||
      (publication.requiredPackageIds !== undefined &&
        (!Array.isArray(publication.requiredPackageIds) ||
          new Set(publication.requiredPackageIds).size !== publication.requiredPackageIds.length ||
          publication.requiredPackageIds.some(id => typeof id !== 'string'))) ||
      !Array.isArray(publication.planned) || !Array.isArray(publication.published)) {
    fail('publication receipt is missing, partial, or not a standard release publication.');
  }
  if (!isRecord(manifest) || manifest.stage !== 'draft' || !Array.isArray(manifest.components)) {
    fail('source manifest is not a tracked draft with a component list.');
  }
  const components = manifest.components.filter(component => component?.id === componentId);
  if (components.length !== 1) {
    fail(`source manifest must contain exactly one ${componentId} component.`);
  }
  const component = components[0];
  if (component.kind !== 'service' || typeof component.version !== 'string' ||
      component.version.trim() !== component.version || component.version.length === 0) {
    fail(`source manifest has no valid ${componentId} version.`);
  }

  const planned = publication.planned.filter(entry => entry?.id === componentId);
  const published = publication.published.filter(entry => entry?.id === componentId);
  if (planned.length !== 1 || published.length !== 1) {
    fail(`publication receipt must contain exactly one planned and published ${label} image.`);
  }
  const plan = planned[0];
  const result = published[0];
  if (plan.kind !== 'image' || result.kind !== 'image' ||
      plan.version !== component.version || result.version !== component.version ||
      !HEX_SHA256.test(plan.sha256 ?? '') || result.archiveSha256 !== plan.sha256) {
    fail(`${label} publication receipt does not prove the exact source-manifest artifact.`);
  }

  let imageSourceSha = publication.sourceSha;
  let imageSourceTree;
  if (plan.reused !== undefined || result.reused !== undefined) {
    // A reused image keeps its original build source; both receipt halves must agree on that provenance.
    const reused = plan.reused;
    if (publication.requiredPackageIds === undefined ||
        !isRecord(reused) || !isDeepStrictEqual(result.reused, reused) ||
        !SHA.test(reused.sourceSha ?? '') || reused.sourceSha === publication.sourceSha ||
        !SHA.test(reused.sourceTree ?? '') || !SHA.test(reused.receiptSourceSha ?? '') ||
        !IMAGE_DIGEST.test(reused.inputFingerprint ?? '')) {
      fail(`reused ${label} image provenance is malformed or differs between the plan and result.`);
    }
    imageSourceSha = reused.sourceSha;
    imageSourceTree = reused.sourceTree;
    requireAncestor(exec, root, imageSourceSha, publication.sourceSha, 'reused image source', fail);
  }

  const repository = `ghcr.io/sabbour/${componentImageRepository(componentId)}`;
  const expectedTag = publication.requiredPackageIds === undefined
    ? component.version : componentImageTag(component.version, imageSourceSha);
  const imagePrefix = `${repository}@`;
  const publishedDigest = typeof result.image === 'string' && result.image.startsWith(imagePrefix)
    ? result.image.slice(imagePrefix.length) : undefined;
  if (plan.destination !== `${repository}:${expectedTag}` ||
      !IMAGE_DIGEST.test(publishedDigest ?? '') || /^0+$/.test(publishedDigest?.slice(7) ?? '')) {
    fail(`${label} image tag, repository, digest, and source manifest do not agree.`);
  }

  // Bind the source tree to Git; a receipt's tree string alone is not evidence of the commit contents.
  const actualImageSourceTree = git('rev-parse', `${imageSourceSha}^{tree}`).trim();
  if (!SHA.test(actualImageSourceTree) ||
      (imageSourceTree !== undefined && imageSourceTree !== actualImageSourceTree)) {
    fail(`${label} image source tree does not match its exact Git source commit.`);
  }
  return {
    sourceSha: publication.sourceSha,
    imageSourceSha,
    imageSourceTree: actualImageSourceTree,
    version: component.version,
    image: `${repository}@${publishedDigest}`,
    digest: publishedDigest,
  };
}
