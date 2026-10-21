/**
 * Decides which product service images a v1 release must build and which it can reuse unchanged.
 *
 * This module is the single definition of what feeds each product image. Preparation, merge, and
 * publication all call it, so they cannot disagree about whether an image changed. Inputs are
 * derived from the source tree at each commit:
 * the transitive ProjectReference closure, out-of-directory MSBuild items and imports,
 * ancestor Directory.Build/Directory.Packages/global.json/NuGet configuration, Dockerfiles and
 * base-image pins inside the component directories, the AgentHost Docker context, and the
 * release tooling that defines how images are built. No path list is maintained by hand.
 *
 * Reuse never retags or rebuilds. A reused image keeps its original immutable digest, its
 * original source-bound tag, and the provenance of the commit that built it. Missing or legacy
 * baselines rebuild everything; malformed baseline evidence fails explicitly.
 */
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { componentImageRepository, componentImageTag } from './pack.mjs';
import { AGENTHOST_ID, AGENTHOST_INPUT_DIRECTORIES } from './agenthost-image.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const SHA = /^[a-f0-9]{40}$/;
const HEX64 = /^[a-f0-9]{64}$/;
const DIGEST = /^sha256:[a-f0-9]{64}$/;
const PUBLICATION_RESULT = /^agentweaver-publication\/([a-f0-9]{40})\/result$/;
/** Version of the input-fingerprint algorithm; changing it makes every image rebuild once. */
export const IMAGE_INPUT_FINGERPRINT_SCHEMA = 1;
/** Version of the reuse plan embedded in image pack provenance. */
export const IMAGE_REUSE_PLAN_SCHEMA = 1;
/** Release tooling that defines how product images are built; any change rebuilds every image. */
export const IMAGE_BUILD_TOOLING = Object.freeze([
  '.github/workflows/v1-release-pack.yml',
  'scripts/release/agenthost-image.mjs',
  'scripts/release/docker-archive.mjs',
  'scripts/release/image-reuse.mjs',
  'scripts/release/measure-agenthost-image.mjs',
  'scripts/release/pack.mjs',
  'scripts/release/prepare-build-inputs.mjs',
]);
/** Repository-wide files that affect checkout bytes or Docker contexts for every image. */
const SHARED_ROOT_INPUTS = Object.freeze(['.dockerignore', '.gitattributes']);
/** MSBuild and NuGet files that apply to every project beneath their directory. */
const ANCESTOR_INPUTS = Object.freeze([
  'Directory.Build.props', 'Directory.Build.targets', 'Directory.Build.rsp', 'Directory.Packages.props',
  'global.json', 'nuget.config', 'NuGet.config', 'NuGet.Config', '.editorconfig', '.globalconfig',
]);
const WEB_DIRECTORY = 'apps/web';
const WEB_HOST_PROJECT = 'apps/web/host/Agentweaver.Web.Host.csproj';

function fail(message) {
  throw new Error(`image reuse: ${message}`);
}

const hash = value => createHash('sha256').update(value).digest('hex');

/** Creates a cached reader for Git trees and blobs at exact commits. */
export function createGitReader(root = repositoryRoot, run) {
  const git = run ?? ((...args) => execFileSync('git', args, {
    cwd: root, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'],
  }));
  const trees = new Map();
  const blobs = new Map();
  return {
    git: (...args) => git(...args).trim(),
    raw: (...args) => git(...args),
    tree(commit) {
      if (!trees.has(commit)) {
        const entries = new Map();
        for (const record of git('ls-tree', '-r', '--full-tree', '-z', commit).split('\0')) {
          if (!record) continue;
          const tab = record.indexOf('\t');
          const [mode, type, object] = record.slice(0, tab).split(' ');
          entries.set(record.slice(tab + 1), { mode, type, object });
        }
        trees.set(commit, entries);
      }
      return trees.get(commit);
    },
    blob(object) {
      if (!blobs.has(object)) blobs.set(object, git('cat-file', 'blob', object));
      return blobs.get(object);
    },
  };
}

function normalize(base, value) {
  const resolved = path.posix.normalize(path.posix.join(base, value.replaceAll('\\', '/')));
  if (resolved === '..' || resolved.startsWith('../') || path.posix.isAbsolute(resolved)) return undefined;
  return resolved === '.' ? '' : resolved.replace(/\/$/, '');
}

function stripWildcards(value) {
  const segments = value.split('/');
  const index = segments.findIndex(segment => /[*?]/.test(segment));
  return index < 0 ? value : segments.slice(0, index).join('/');
}

/**
 * Resolves every tracked input that feeds one product image at an exact commit.
 * Unresolvable MSBuild paths are reported so the caller rebuilds rather than guessing.
 */
export function resolveImageInputs(reader, commit, component) {
  const tree = reader.tree(commit);
  const prefixes = new Set();
  const files = new Set([...IMAGE_BUILD_TOOLING, ...SHARED_ROOT_INPUTS]);
  const unresolved = new Set();
  const visited = new Set();
  const addAncestors = directory => {
    const parts = directory ? directory.split('/') : [];
    for (let depth = parts.length; depth >= 0; depth -= 1) {
      const base = parts.slice(0, depth).join('/');
      for (const name of ANCESTOR_INPUTS) {
        const file = base ? `${base}/${name}` : name;
        files.add(file);
        if (/\.(props|targets)$/.test(name) && tree.has(file)) walk(file);
      }
    }
  };
  const walk = file => {
    if (visited.has(file)) return;
    visited.add(file);
    const entry = tree.get(file);
    files.add(file);
    if (!entry) {
      unresolved.add(`${file} is referenced but not tracked at ${commit}`);
      return;
    }
    const directory = path.posix.dirname(file) === '.' ? '' : path.posix.dirname(file);
    const isProject = /\.(cs|fs|vb)proj$/.test(file);
    if (isProject) {
      prefixes.add(directory);
      addAncestors(directory);
    }
    const text = reader.blob(entry.object);
    for (const match of text.matchAll(/\b(Include|Update|Remove|Exclude|Project)\s*=\s*(["'])(.*?)\2/g)) {
      const [, attribute, , list] = match;
      for (const item of list.split(';').map(value => value.trim()).filter(Boolean)) {
        const value = item.replaceAll('$(MSBuildThisFileDirectory)', './');
        const looksLikePath = /[\\/]/.test(value) || attribute === 'Project';
        if (!looksLikePath) continue;
        if (value.includes('$(') || value.includes('@(') || value.includes('%(')) {
          unresolved.add(`${file} uses an MSBuild expression in ${attribute}="${item}"`);
          continue;
        }
        const resolved = normalize(directory, stripWildcards(value.replaceAll('\\', '/')));
        if (resolved === undefined) {
          unresolved.add(`${file} references ${item} outside the repository`);
        } else if (/\.((cs|fs|vb)proj|props|targets)$/.test(resolved) && !/[*?]/.test(value)) {
          walk(resolved);
        } else if (resolved !== '' || isProject) {
          prefixes.add(resolved);
        }
      }
    }
  };
  if (component.id === 'Agentweaver.Web') {
    prefixes.add(WEB_DIRECTORY);
    walk(WEB_HOST_PROJECT);
  } else {
    if (!/\.(cs|fs|vb)proj$/.test(component.project ?? '')) {
      unresolved.add(`${component.id} has no supported image project`);
    } else {
      walk(component.project);
    }
    if (component.id === AGENTHOST_ID) for (const directory of AGENTHOST_INPUT_DIRECTORIES) prefixes.add(directory);
  }
  const prefixList = [...prefixes].sort();
  const entries = [];
  for (const [file, entry] of tree) {
    if (files.has(file) || prefixList.some(prefix => prefix === '' ? false : file === prefix || file.startsWith(`${prefix}/`))) {
      entries.push([file, entry.mode, entry.object]);
    }
  }
  entries.sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0));
  return {
    prefixes: prefixList,
    declaredFiles: [...files].sort(),
    entries,
    unresolved: [...unresolved].sort(),
  };
}

/** Hashes the component identity and every resolved input path, mode, and Git object. */
export function imageInputFingerprint(reader, commit, component) {
  const inputs = resolveImageInputs(reader, commit, component);
  const fingerprint = `sha256:${hash(JSON.stringify({
    schema: IMAGE_INPUT_FINGERPRINT_SCHEMA,
    component: { id: component.id, kind: component.kind, version: component.version, project: component.project },
    prefixes: inputs.prefixes,
    declaredFiles: inputs.declaredFiles,
    entries: inputs.entries,
    unresolved: inputs.unresolved,
  }))}`;
  return { fingerprint, unresolved: inputs.unresolved, inputCount: inputs.entries.length };
}

function parseTagMessage(text) {
  const separator = text.indexOf('\n\n');
  if (separator < 0) return { headers: text, message: '' };
  return { headers: text.slice(0, separator), message: text.slice(separator + 2).trim() };
}

/**
 * Finds the nearest first-parent ancestor of the source with a durable published product receipt.
 * Partial or failed results are skipped because they prove no complete release.
 */
export function findBaselinePublication(reader, sourceSha) {
  const results = new Map();
  const refs = reader.git('for-each-ref', '--format=%(refname:strip=2) %(objecttype) %(objectname) %(*objectname)',
    'refs/tags/agentweaver-publication/');
  for (const line of refs.split(/\r?\n/).filter(Boolean)) {
    const [name, type, object, peeled] = line.split(' ');
    const match = PUBLICATION_RESULT.exec(name);
    if (!match) continue;
    if (type !== 'tag' || peeled !== match[1]) {
      fail(`durable publication result ${name} is not an annotated tag on its source commit; rerun with rebuild_all_images`);
    }
    results.set(match[1], { name, tagObject: object });
  }
  let parents;
  try {
    parents = reader.git('rev-list', '--first-parent', `${sourceSha}^`);
  } catch {
    return undefined;
  }
  for (const commit of parents.split(/\r?\n/).filter(Boolean)) {
    const result = results.get(commit);
    if (!result) continue;
    const { headers, message } = parseTagMessage(reader.raw('cat-file', 'tag', result.tagObject));
    if (!headers.split('\n').includes(`object ${commit}`) || !headers.split('\n').includes(`tag ${result.name}`)) {
      fail(`durable publication result ${result.name} does not bind its source commit; rerun with rebuild_all_images`);
    }
    let receipt;
    try {
      receipt = JSON.parse(message);
    } catch (error) {
      fail(`durable publication result ${result.name} is not valid JSON (${error.message}); rerun with rebuild_all_images`);
    }
    if (!receipt || typeof receipt !== 'object' || Array.isArray(receipt) || receipt.sourceSha !== commit) {
      fail(`durable publication result ${result.name} is malformed or bound to another source; rerun with rebuild_all_images`);
    }
    if (receipt.status !== 'published') continue;
    return { sourceSha: commit, tag: result.name, tagObject: result.tagObject, receipt };
  }
  return undefined;
}

function isLegacyReceipt(receipt) {
  return !Array.isArray(receipt.requiredPackageIds) || receipt.schemaVersion !== 1 || receipt.mode !== undefined ||
    receipt.continuation !== undefined || receipt.initialProbeReplacement !== undefined ||
    receipt.samplerProbeReplacement !== undefined || !Array.isArray(receipt.planned) || !Array.isArray(receipt.published);
}

const stripReuse = entry => {
  const { reused, ...rest } = entry;
  return rest;
};

/** Validates one baseline image entry and returns the commit that actually built it. */
function readBaselineImage(reader, baseline, component, repository) {
  const planned = baseline.receipt.planned.filter(entry => entry?.id === component.id);
  const published = baseline.receipt.published.filter(entry => entry?.id === component.id);
  if (planned.length === 0 && published.length === 0) return undefined;
  const reject = detail => fail(`baseline receipt ${baseline.tag} has an invalid ${component.id} image entry (${detail}); rerun with rebuild_all_images`);
  if (planned.length !== 1 || published.length !== 1) reject('missing or duplicate plan/result');
  const [plan] = planned;
  const [result] = published;
  if (plan.kind !== 'image' || result.kind !== 'image' || plan.version !== result.version ||
      typeof plan.version !== 'string' || !HEX64.test(plan.sha256 ?? '') || result.archiveSha256 !== plan.sha256) {
    reject('kind, version, or archive hash');
  }
  if (JSON.stringify(plan.reused) !== JSON.stringify(result.reused)) reject('plan and result reuse records differ');
  // A baseline published to another registry or repository is valid evidence, just not reusable here.
  const publishedRepository = typeof result.image === 'string' && result.image.includes('@')
    ? result.image.slice(0, result.image.lastIndexOf('@')) : undefined;
  if (publishedRepository !== undefined && publishedRepository !== repository) {
    return { otherRepository: publishedRepository };
  }
  const buildSourceSha = plan.reused === undefined ? baseline.sourceSha : plan.reused.sourceSha;
  if (!SHA.test(buildSourceSha ?? '')) reject('build source');
  if (plan.reused !== undefined && (!SHA.test(plan.reused.sourceTree ?? '') ||
      !/^sha256:[a-f0-9]{64}$/.test(plan.reused.inputFingerprint ?? '') ||
      !SHA.test(plan.reused.receiptSourceSha ?? ''))) {
    reject('reuse provenance');
  }
  const digest = typeof result.image === 'string' && result.image.startsWith(`${repository}@`)
    ? result.image.slice(repository.length + 1) : '';
  if (plan.destination !== `${repository}:${componentImageTag(plan.version, buildSourceSha)}` || !DIGEST.test(digest)) {
    reject('destination tag or immutable digest');
  }
  if (component.id === AGENTHOST_ID) {
    const measurement = result.preparedOciMeasurement;
    if (measurement?.sourceSha !== buildSourceSha || measurement?.image?.digest !== digest ||
        measurement.image.version !== plan.version || result.imageConfigDigest !== measurement.image.configDigest ||
        !HEX64.test(result.ociArchiveSha256 ?? '')) {
      reject('AgentHost measurement provenance');
    }
  } else if (result.preparedOciMeasurement !== undefined || result.imageConfigDigest !== undefined ||
      result.ociArchiveSha256 !== undefined) {
    reject('unexpected AgentHost evidence');
  }
  let isAncestor = true;
  try {
    reader.git('merge-base', '--is-ancestor', buildSourceSha, baseline.sourceSha);
  } catch {
    isAncestor = false;
  }
  if (!isAncestor) reject(`build source ${buildSourceSha} is not an available ancestor of the baseline`);
  let sourceTree;
  try {
    sourceTree = reader.git('rev-parse', `${buildSourceSha}^{tree}`);
  } catch {
    reject(`build source ${buildSourceSha} is not available`);
  }
  if (plan.reused !== undefined && plan.reused.sourceTree !== sourceTree) reject('reuse source tree');
  return { plan: stripReuse(plan), result: stripReuse(result), buildSourceSha, sourceTree, digest };
}

/** Product service images published by the automated v1 release, in manifest order. */
export function productImageComponents(manifest) {
  return manifest.components.filter(component =>
    component.kind === 'service' && component.id !== 'Agentweaver.FoundationProbe');
}

/**
 * Builds the reuse plan for every product service image at an exact source.
 * Each image is reused only when the nearest published baseline proves a valid digest for the same
 * version and the image's resolved inputs are identical at the commit that built it and at source.
 */
export function planImageReuse({ manifest, sourceSha, registry, rebuildAll = false, root = repositoryRoot, reader }) {
  if (!SHA.test(sourceSha ?? '')) fail('an exact 40-character source SHA is required');
  if (typeof registry !== 'string' || !/^[a-z0-9.-]+(?::[0-9]+)?(?:\/[a-z0-9._-]+)*$/.test(registry)) {
    fail('a normalized image registry is required');
  }
  reader ??= createGitReader(root);
  const components = productImageComponents(manifest);
  if (components.length === 0) fail('the manifest has no product service images');
  const fingerprints = new Map();
  const fingerprintAt = (commit, component) => {
    const key = `${commit}\0${component.id}\0${component.version}`;
    if (!fingerprints.has(key)) fingerprints.set(key, imageInputFingerprint(reader, commit, component));
    return fingerprints.get(key);
  };
  let baseline;
  let baselineReason;
  if (rebuildAll) {
    baselineReason = 'rebuild of every product image was explicitly requested';
  } else {
    const found = findBaselinePublication(reader, sourceSha);
    if (!found) baselineReason = 'no earlier published product receipt exists on the first-parent history';
    else if (isLegacyReceipt(found.receipt)) {
      baselineReason = `baseline receipt ${found.tag} predates incremental publication`;
    } else {
      baseline = found;
    }
  }
  const plan = {
    schemaVersion: IMAGE_REUSE_PLAN_SCHEMA,
    fingerprintSchema: IMAGE_INPUT_FINGERPRINT_SCHEMA,
    sourceSha,
    registry,
    rebuildAll: Boolean(rebuildAll),
    baseline: baseline ? { sourceSha: baseline.sourceSha, tag: baseline.tag, tagObject: baseline.tagObject } : null,
    ...(baselineReason ? { baselineReason } : {}),
    components: [],
  };
  for (const component of components) {
    const current = fingerprintAt(sourceSha, component);
    const record = { id: component.id, version: component.version, inputFingerprint: current.fingerprint };
    const build = reason => plan.components.push({ ...record, action: 'build', reason });
    if (!baseline) {
      build(baselineReason);
      continue;
    }
    if (current.unresolved.length > 0) {
      build(`inputs cannot be fully resolved: ${current.unresolved.join('; ')}`);
      continue;
    }
    const repository = `${registry}/${componentImageRepository(component.id)}`;
    const prior = readBaselineImage(reader, baseline, component, repository);
    if (!prior) {
      build(`no image in baseline receipt ${baseline.tag}`);
      continue;
    }
    if (prior.otherRepository !== undefined) {
      build(`baseline image was published to ${prior.otherRepository}, not ${repository}`);
      continue;
    }
    if (prior.plan.version !== component.version) {
      build(`version changed from ${prior.plan.version}`);
      continue;
    }
    const original = fingerprintAt(prior.buildSourceSha, component);
    if (original.unresolved.length > 0 || original.fingerprint !== current.fingerprint) {
      build(`image inputs changed since ${prior.buildSourceSha}`);
      continue;
    }
    const reused = {
      sourceSha: prior.buildSourceSha,
      sourceTree: prior.sourceTree,
      inputFingerprint: original.fingerprint,
      receiptSourceSha: baseline.sourceSha,
    };
    plan.components.push({
      ...record,
      action: 'reuse',
      reason: `version and image inputs unchanged since ${prior.buildSourceSha}`,
      reused,
      planned: { ...prior.plan, reused },
      published: { ...prior.result, reused },
    });
  }
  return plan;
}

/** Validates a reuse plan shape against the exact source and product image selection. */
export function assertImageReusePlan(plan, { sourceSha, manifest }) {
  const components = productImageComponents(manifest);
  if (!plan || typeof plan !== 'object' || Array.isArray(plan) || plan.schemaVersion !== IMAGE_REUSE_PLAN_SCHEMA ||
      plan.fingerprintSchema !== IMAGE_INPUT_FINGERPRINT_SCHEMA || plan.sourceSha !== sourceSha ||
      typeof plan.registry !== 'string' || typeof plan.rebuildAll !== 'boolean' ||
      !Array.isArray(plan.components) || plan.components.length !== components.length) {
    fail('reuse plan does not match the exact source and product image selection');
  }
  for (const [index, entry] of plan.components.entries()) {
    const component = components[index];
    if (entry?.id !== component.id || entry.version !== component.version ||
        !/^sha256:[a-f0-9]{64}$/.test(entry.inputFingerprint ?? '') || typeof entry.reason !== 'string' ||
        !['build', 'reuse'].includes(entry.action)) {
      fail(`reuse plan entry for ${component.id} is malformed`);
    }
    if (entry.action === 'reuse') {
      const repository = `${plan.registry}/${componentImageRepository(component.id)}`;
      if (plan.rebuildAll || !plan.baseline || !SHA.test(entry.reused?.sourceSha ?? '') ||
          entry.reused.sourceSha === sourceSha || entry.reused.inputFingerprint !== entry.inputFingerprint ||
          entry.planned?.id !== component.id || entry.published?.id !== component.id ||
          entry.planned.version !== component.version || entry.published.version !== component.version ||
          entry.planned.destination !== `${repository}:${componentImageTag(component.version, entry.reused.sourceSha)}` ||
          !String(entry.published.image ?? '').startsWith(`${repository}@sha256:`) ||
          JSON.stringify(entry.planned.reused) !== JSON.stringify(entry.reused) ||
          JSON.stringify(entry.published.reused) !== JSON.stringify(entry.reused)) {
        fail(`reuse plan entry for ${component.id} has inconsistent original provenance`);
      }
    } else if (entry.reused !== undefined || entry.planned !== undefined || entry.published !== undefined) {
      fail(`reuse plan build entry for ${component.id} carries reuse evidence`);
    }
  }
  return plan;
}

/** Returns the identifiers of images the plan reuses. */
export function reusedImageIds(plan) {
  return new Set((plan?.components ?? []).filter(entry => entry.action === 'reuse').map(entry => entry.id));
}

/**
 * Confirms in the registry that each reused digest still exists and its original source-bound tag
 * still resolves to that digest. It reads only and never creates or moves a tag.
 */
export function verifyReusedImages(plan, readDescriptor) {
  for (const entry of plan.components.filter(item => item.action === 'reuse')) {
    const image = entry.published.image;
    const digest = image.slice(image.lastIndexOf('@') + 1);
    let byDigest;
    let byTag;
    try {
      byDigest = readDescriptor(image);
      byTag = readDescriptor(entry.planned.destination);
    } catch (error) {
      fail(`reused ${entry.id} image cannot be read from the registry (${error.message}); rerun with rebuild_all_images`);
    }
    if (byDigest?.digest !== digest) {
      fail(`reused ${entry.id} digest ${digest} is missing from the registry; rerun with rebuild_all_images`);
    }
    if (byTag?.digest !== digest) {
      fail(`original tag ${entry.planned.destination} no longer resolves to ${digest}; refusing reuse, rerun with rebuild_all_images`);
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    const option = name => {
      const index = args.indexOf(name);
      if (index < 0) return undefined;
      if (!args[index + 1] || args[index + 1].startsWith('--')) fail(`${name} requires a value`);
      return args[index + 1];
    };
    const manifestPath = args[0];
    const out = option('--out');
    const registry = option('--registry') ?? process.env.RELEASE_REGISTRY ?? 'ghcr.io/sabbour';
    const flag = process.env.RELEASE_REBUILD_ALL_IMAGES;
    if (flag !== undefined && flag !== '' && flag !== 'true' && flag !== 'false') {
      fail('RELEASE_REBUILD_ALL_IMAGES must be true or false');
    }
    if (!manifestPath || manifestPath.startsWith('--') || !out) {
      fail('usage: node scripts/release/image-reuse.mjs <manifest.json> --out <plan.json> [--registry <registry>] [--rebuild-all]');
    }
    const reader = createGitReader(repositoryRoot);
    const sourceSha = reader.git('rev-parse', 'HEAD');
    const relative = path.relative(repositoryRoot, path.resolve(manifestPath)).replaceAll('\\', '/');
    const manifest = JSON.parse(reader.raw('show', `${sourceSha}:${relative}`));
    if (JSON.stringify(manifest) !== JSON.stringify(JSON.parse(readFileSync(path.resolve(manifestPath), 'utf8')))) {
      fail('composition differs from source HEAD');
    }
    const plan = planImageReuse({
      manifest, sourceSha, registry, reader,
      rebuildAll: args.includes('--rebuild-all') || flag === 'true',
    });
    mkdirSync(path.dirname(path.resolve(out)), { recursive: true });
    writeFileSync(path.resolve(out), `${JSON.stringify(plan, null, 2)}\n`, { flag: 'wx' });
    console.log(`Image reuse plan for ${sourceSha}${plan.baseline ? ` against ${plan.baseline.tag}` : ` (${plan.baselineReason})`}:`);
    for (const entry of plan.components) console.log(`  ${entry.action.padEnd(5)} ${entry.id} ${entry.version}: ${entry.reason}`);
  } catch (error) {
    console.error(`Image reuse planning failed: ${error.message}`);
    process.exitCode = 1;
  }
}
