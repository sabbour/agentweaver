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
  // The planner reads the same commits many times (once per image, and again at each baseline
  // build commit), so cache whole trees and blob text by commit and object ID.
  const trees = new Map();
  const blobs = new Map();
  return {
    git: (...args) => git(...args).trim(),
    raw: (...args) => git(...args),
    tree(commit) {
      if (!trees.has(commit)) {
        // Read the committed tree, not the working copy, so the result depends only on the commit.
        // -z keeps paths with spaces or special characters intact.
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

/** Resolves a project-relative path to a repository path, or undefined when it leaves the repository. */
function normalize(base, value) {
  // MSBuild files use Windows separators; Git paths always use forward slashes.
  const resolved = path.posix.normalize(path.posix.join(base, value.replaceAll('\\', '/')));
  // A path outside the repository cannot be fingerprinted from Git, so the caller treats it as unresolved.
  if (resolved === '..' || resolved.startsWith('../') || path.posix.isAbsolute(resolved)) return undefined;
  return resolved === '.' ? '' : resolved.replace(/\/$/, '');
}

/** Turns a glob such as `../Shared/**\/*.cs` into its fixed directory prefix `../Shared`. */
function stripWildcards(value) {
  // Watching the whole directory above the first wildcard over-includes files, which can only cause
  // an extra rebuild. Matching the glob exactly could miss a file and reuse a stale image.
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
  // `prefixes` are directories whose every tracked file is an input (project folders, globbed
  // includes). `files` are single files that are inputs whether or not they exist yet.
  const prefixes = new Set();
  // Every image depends on the release tooling and on repository-wide Docker/Git settings, so a
  // change to any of them rebuilds all images. This is deliberately conservative.
  const files = new Set([...IMAGE_BUILD_TOOLING, ...SHARED_ROOT_INPUTS]);
  const unresolved = new Set();
  const visited = new Set();
  const addAncestors = directory => {
    // MSBuild imports Directory.Build.* and NuGet reads its configuration from the project
    // directory and every parent up to the root. Record each candidate path even when it does not
    // exist, so that adding such a file later changes the fingerprint and forces a rebuild.
    const parts = directory ? directory.split('/') : [];
    for (let depth = parts.length; depth >= 0; depth -= 1) {
      const base = parts.slice(0, depth).join('/');
      for (const name of ANCESTOR_INPUTS) {
        const file = base ? `${base}/${name}` : name;
        files.add(file);
        // An existing .props/.targets file can import or include more files, so follow it too.
        if (/\.(props|targets)$/.test(name) && tree.has(file)) walk(file);
      }
    }
  };
  const walk = file => {
    // Project graphs can contain cycles and shared references; visit each file once.
    if (visited.has(file)) return;
    visited.add(file);
    const entry = tree.get(file);
    files.add(file);
    if (!entry) {
      // A reference to an untracked file means Git cannot describe the real build input. Report it
      // so the image is rebuilt instead of reused on an incomplete fingerprint.
      unresolved.add(`${file} is referenced but not tracked at ${commit}`);
      return;
    }
    const directory = path.posix.dirname(file) === '.' ? '' : path.posix.dirname(file);
    const isProject = /\.(cs|fs|vb)proj$/.test(file);
    if (isProject) {
      // SDK-style projects compile every file under their folder by default, so the whole folder
      // is an input, plus the ancestor MSBuild and NuGet files that apply to it.
      prefixes.add(directory);
      addAncestors(directory);
    }
    const text = reader.blob(entry.object);
    // A text scan of path-like attributes, not a full MSBuild evaluation. It finds
    // ProjectReference, Import, Compile/Content/None includes and similar items. Missing a
    // conditional branch only adds inputs (extra rebuilds); it never removes them.
    for (const match of text.matchAll(/\b(Include|Update|Remove|Exclude|Project)\s*=\s*(["'])(.*?)\2/g)) {
      const [, attribute, , list] = match;
      // MSBuild item lists are separated by semicolons.
      for (const item of list.split(';').map(value => value.trim()).filter(Boolean)) {
        // This property is the file's own directory, which the relative resolution below already uses.
        const value = item.replaceAll('$(MSBuildThisFileDirectory)', './');
        // PackageReference Include="Name" and similar values are package IDs, not paths. Their
        // versions come from Directory.Packages.props, which is already an input.
        const looksLikePath = /[\\/]/.test(value) || attribute === 'Project';
        if (!looksLikePath) continue;
        if (value.includes('$(') || value.includes('@(') || value.includes('%(')) {
          // Fail closed: without evaluating MSBuild properties we cannot know which files this
          // path points to, so the image must be rebuilt.
          unresolved.add(`${file} uses an MSBuild expression in ${attribute}="${item}"`);
          continue;
        }
        const resolved = normalize(directory, stripWildcards(value.replaceAll('\\', '/')));
        if (resolved === undefined) {
          unresolved.add(`${file} references ${item} outside the repository`);
        } else if (/\.((cs|fs|vb)proj|props|targets)$/.test(resolved) && !/[*?]/.test(value)) {
          // Follow project references and imports, so transitive dependencies are covered.
          walk(resolved);
        } else if (resolved !== '' || isProject) {
          // Plain files and globbed directories. A glob that resolves to the repository root from a
          // non-project file is ignored rather than turning every file into an input.
          prefixes.add(resolved);
        }
      }
    }
  };
  if (component.id === 'Agentweaver.Web') {
    // The web image bundles the Vite application and an ASP.NET host, so both are inputs.
    prefixes.add(WEB_DIRECTORY);
    walk(WEB_HOST_PROJECT);
  } else {
    if (!/\.(cs|fs|vb)proj$/.test(component.project ?? '')) {
      unresolved.add(`${component.id} has no supported image project`);
    } else {
      walk(component.project);
    }
    // AgentHost's Dockerfile copies directories that no project references; pack.mjs and this
    // planner read the same exported list so they cannot disagree.
    if (component.id === AGENTHOST_ID) for (const directory of AGENTHOST_INPUT_DIRECTORIES) prefixes.add(directory);
  }
  const prefixList = [...prefixes].sort();
  const entries = [];
  // Collect mode and object ID for every matching tracked file. The Git object ID changes whenever
  // the file content changes, so content does not need to be read again here.
  for (const [file, entry] of tree) {
    if (files.has(file) || prefixList.some(prefix => prefix === '' ? false : file === prefix || file.startsWith(`${prefix}/`))) {
      entries.push([file, entry.mode, entry.object]);
    }
  }
  // Sort by code unit, not locale, so the fingerprint is the same on every machine.
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
  // Include the version and project so a version bump alone changes the fingerprint, and include
  // the schema so a change to this algorithm never matches a fingerprint made the old way.
  // Unresolved notes are hashed too, but callers already refuse reuse when any exist.
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

/** Splits a raw annotated tag object into its header lines and message body. */
function parseTagMessage(text) {
  // Git separates tag headers from the message with the first blank line.
  const separator = text.indexOf('\n\n');
  if (separator < 0) return { headers: text, message: '' };
  return { headers: text.slice(0, separator), message: text.slice(separator + 2).trim() };
}

/**
 * Finds the nearest first-parent ancestor of the source with a durable published product receipt.
 * Partial or failed results are skipped because they prove no complete release.
 */
export function findBaselinePublication(reader, sourceSha) {
  // Index every publication result tag by the source commit it names. publish.mjs writes one
  // `agentweaver-publication/<sha>/result` tag per release run, with the receipt as its message.
  const results = new Map();
  const refs = reader.git('for-each-ref', '--format=%(refname:strip=2) %(objecttype) %(objectname) %(*objectname)',
    'refs/tags/agentweaver-publication/');
  for (const line of refs.split(/\r?\n/).filter(Boolean)) {
    const [name, type, object, peeled] = line.split(' ');
    const match = PUBLICATION_RESULT.exec(name);
    if (!match) continue;
    // Only an annotated tag carries the receipt and a tag object we can recheck. A lightweight tag,
    // or one that points at a different commit than its name says, was not written by publish.mjs.
    // Fail instead of skipping it: silently ignoring a corrupted record could pick an older baseline.
    if (type !== 'tag' || peeled !== match[1]) {
      fail(`durable publication result ${name} is not an annotated tag on its source commit; rerun with rebuild_all_images`);
    }
    results.set(match[1], { name, tagObject: object });
  }
  let parents;
  try {
    // Walk only the first-parent chain of v1, starting before the release commit itself. Releases
    // are published from v1 commits, so this chain is the release history. Following second parents
    // would reach feature-branch commits and could select a receipt that v1 never released from.
    parents = reader.git('rev-list', '--first-parent', `${sourceSha}^`);
  } catch {
    // A root commit has no parent and therefore no earlier release: every image is built.
    return undefined;
  }
  for (const commit of parents.split(/\r?\n/).filter(Boolean)) {
    const result = results.get(commit);
    if (!result) continue;
    const { headers, message } = parseTagMessage(reader.raw('cat-file', 'tag', result.tagObject));
    // Recheck the tag object itself so that the receipt is bound to this commit and tag name.
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
    // Accept only `published`. A `partial` or `failed` run may have pushed some images but not
    // others, and its receipt does not prove that every listed digest is complete and usable.
    // Keep walking to an older, fully published release instead.
    if (receipt.status !== 'published') continue;
    return { sourceSha: commit, tag: result.name, tagObject: result.tagObject, receipt };
  }
  return undefined;
}

/**
 * True for receipts from continuation, Probe-replacement or older publication modes. Their image
 * entries do not follow the current planned/published layout, so they are never a reuse baseline.
 */
function isLegacyReceipt(receipt) {
  return !Array.isArray(receipt.requiredPackageIds) || receipt.schemaVersion !== 1 || receipt.mode !== undefined ||
    receipt.continuation !== undefined || receipt.initialProbeReplacement !== undefined ||
    receipt.samplerProbeReplacement !== undefined || !Array.isArray(receipt.planned) || !Array.isArray(receipt.published);
}

// Remove an older `reused` block; the planner attaches a fresh one that names the original build.
const stripReuse = entry => {
  const { reused, ...rest } = entry;
  return rest;
};

/** Validates one baseline image entry and returns the commit that actually built it. */
function readBaselineImage(reader, baseline, component, repository) {
  const planned = baseline.receipt.planned.filter(entry => entry?.id === component.id);
  const published = baseline.receipt.published.filter(entry => entry?.id === component.id);
  // An image absent from the baseline (for example a new service) is simply built.
  if (planned.length === 0 && published.length === 0) return undefined;
  // Any other inconsistency means the receipt cannot be trusted, so stop with an explicit error
  // rather than quietly building; the operator decides whether to rerun with rebuild_all_images.
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
  // If the baseline itself reused this image, follow its record back to the commit that really
  // built it. A chain of reuses therefore always points at the first build, never at a release
  // commit whose source was not used to build the image.
  const buildSourceSha = plan.reused === undefined ? baseline.sourceSha : plan.reused.sourceSha;
  if (!SHA.test(buildSourceSha ?? '')) reject('build source');
  if (plan.reused !== undefined && (!SHA.test(plan.reused.sourceTree ?? '') ||
      !/^sha256:[a-f0-9]{64}$/.test(plan.reused.inputFingerprint ?? '') ||
      !SHA.test(plan.reused.receiptSourceSha ?? ''))) {
    reject('reuse provenance');
  }
  // The published reference must be `<repository>@sha256:<digest>`. A tag is mutable, so only a
  // digest proves which bytes are reused. The tag must also be the one built from buildSourceSha.
  const digest = typeof result.image === 'string' && result.image.startsWith(`${repository}@`)
    ? result.image.slice(repository.length + 1) : '';
  if (plan.destination !== `${repository}:${componentImageTag(plan.version, buildSourceSha)}` || !DIGEST.test(digest)) {
    reject('destination tag or immutable digest');
  }
  // AgentHost carries extra OCI measurement evidence that deployment checks. It must describe the
  // same build and digest, otherwise the reused entry would fail later at deploy time.
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
  // The build commit must exist locally and sit in the baseline's history. Otherwise its inputs
  // cannot be fingerprinted, and an unverifiable image is not reusable.
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
  // The recorded tree is what deployment checks against the image label; it must match Git.
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
  // Several images may share one original build commit, so cache fingerprints per commit and image.
  const fingerprints = new Map();
  const fingerprintAt = (commit, component) => {
    const key = `${commit}\0${component.id}\0${component.version}`;
    if (!fingerprints.has(key)) fingerprints.set(key, imageInputFingerprint(reader, commit, component));
    return fingerprints.get(key);
  };
  // Choose the baseline once. Without a usable baseline every image is built, and the reason is
  // recorded in the plan so the workflow log explains why nothing was reused.
  let baseline;
  let baselineReason;
  if (rebuildAll) {
    // Explicit escape hatch: skip the baseline lookup entirely, including its malformed-receipt errors.
    baselineReason = 'rebuild of every product image was explicitly requested';
  } else {
    const found = findBaselinePublication(reader, sourceSha);
    if (!found) baselineReason = 'no earlier published product receipt exists on the first-parent history';
    else if (isLegacyReceipt(found.receipt)) {
      // Older receipts are valid history but lack the layout reuse needs, so build everything once.
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
  // Decide each image independently. Every check below can only choose `build`; reuse is the single
  // outcome reached after all checks pass.
  for (const component of components) {
    const current = fingerprintAt(sourceSha, component);
    const record = { id: component.id, version: component.version, inputFingerprint: current.fingerprint };
    const build = reason => plan.components.push({ ...record, action: 'build', reason });
    if (!baseline) {
      build(baselineReason);
      continue;
    }
    if (current.unresolved.length > 0) {
      // An incomplete input list cannot prove "unchanged"; trade a rebuild for correctness.
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
    // Require the same version: the version is baked into the image tag and labels (the web image
    // also bakes it into IMAGE_TAG), and a retag cannot change those baked values.
    if (prior.plan.version !== component.version) {
      build(`version changed from ${prior.plan.version}`);
      continue;
    }
    // Compare with the commit that built the image, not the baseline release commit. After a chain
    // of reuses, small drifts between releases would otherwise add up unnoticed.
    const original = fingerprintAt(prior.buildSourceSha, component);
    if (original.unresolved.length > 0 || original.fingerprint !== current.fingerprint) {
      build(`image inputs changed since ${prior.buildSourceSha}`);
      continue;
    }
    // Reuse: copy the original plan/result entries unchanged (tag, digest, archive hash, AgentHost
    // measurement) and attach where they came from. Nothing claims the image was built from sourceSha.
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
  // The plan travels as a workflow artifact between jobs, so pack, merge and publish recheck it
  // here instead of trusting it. Entries must follow manifest order, one per product image.
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
      // A reused entry must name an earlier build commit (never the current source), keep its
      // original tag and a digest reference, and carry the same provenance in every copy.
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
      // A build entry must not smuggle in an old digest that publish would copy into the receipt.
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
      // Read both references: the digest proves the bytes still exist, and the tag proves nobody
      // moved the original version tag to different bytes since the baseline was published.
      byDigest = readDescriptor(image);
      byTag = readDescriptor(entry.planned.destination);
    } catch (error) {
      // Any registry error (missing, unauthorized, network) is a failure, not "probably fine".
      fail(`reused ${entry.id} image cannot be read from the registry (${error.message}); rerun with rebuild_all_images`);
    }
    if (byDigest?.digest !== digest) {
      fail(`reused ${entry.id} digest ${digest} is missing from the registry; rerun with rebuild_all_images`);
    }
    // Never repair a moved tag here: tags are immutable by policy, so a mismatch needs a human.
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
    // Plan from the committed manifest and refuse a locally edited copy, so the plan describes HEAD.
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
    // `wx` refuses to overwrite an existing plan from an earlier step or run.
    writeFileSync(path.resolve(out), `${JSON.stringify(plan, null, 2)}\n`, { flag: 'wx' });
    console.log(`Image reuse plan for ${sourceSha}${plan.baseline ? ` against ${plan.baseline.tag}` : ` (${plan.baselineReason})`}:`);
    for (const entry of plan.components) console.log(`  ${entry.action.padEnd(5)} ${entry.id} ${entry.version}: ${entry.reason}`);
  } catch (error) {
    console.error(`Image reuse planning failed: ${error.message}`);
    process.exitCode = 1;
  }
}
