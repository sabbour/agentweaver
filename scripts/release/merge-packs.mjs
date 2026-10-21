import { createHash } from 'node:crypto';
import {
  copyFileSync, existsSync, lstatSync, mkdirSync, readFileSync, readdirSync,
  renameSync, unlinkSync, writeFileSync,
} from 'node:fs';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateFile } from './validate.mjs';
import { assertImageReusePlan, reusedImageIds } from './image-reuse.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const sha = /^[a-f0-9]{40}$/;
const digest = /^[a-f0-9]{64}$/;

function fail(location, message) {
  throw new Error(`${location}: ${message}`);
}

function git(root, ...args) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

function inside(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (relative !== '..' && !relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative));
}

function resolveWorkspacePath(root, value, location) {
  if (typeof value !== 'string' || value.trim() === '') fail(location, 'expected a non-empty path');
  const resolved = path.resolve(root, value);
  if (!inside(root, resolved)) fail(location, 'path must remain inside the repository');
  return resolved;
}

function safeArtifactPath(value, location) {
  if (typeof value !== 'string' || value === '' || value.includes('\\') ||
      path.posix.isAbsolute(value) || value.split('/').some(part => part === '' || part === '.' || part === '..')) {
    fail(location, 'expected a safe repository-relative artifact path');
  }
  return value;
}

function listRegularFiles(directory, prefix = '') {
  const files = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
    if (entry.isSymbolicLink()) fail(directory, `symbolic links are not allowed in release artifacts: ${relative}`);
    if (entry.isDirectory()) files.push(...listRegularFiles(path.join(directory, entry.name), relative));
    else if (entry.isFile()) files.push(relative);
    else fail(directory, `unsupported filesystem entry in release artifacts: ${relative}`);
  }
  return files.sort();
}

function readJson(file, location) {
  try {
    return JSON.parse(readFileSync(file, 'utf8'));
  } catch (error) {
    fail(location, `cannot read valid JSON: ${error.message}`);
  }
}

function readPack(directory, kind, components, sourceSha, manifestSha256, stage) {
  const stats = lstatSync(directory);
  if (!stats.isDirectory()) fail(directory, 'expected a regular artifact directory');
  const provenance = readJson(path.join(directory, 'provenance.json'), `${directory}/provenance.json`);
  if (provenance.schemaVersion !== 1 || provenance.sourceSha !== sourceSha ||
      provenance.manifestSha256 !== manifestSha256 || provenance.stage !== stage ||
      provenance.integration !== undefined || !Array.isArray(provenance.components) ||
      !Array.isArray(provenance.artifacts)) {
    fail(directory, 'provenance must match the release source, manifest, stage, and ordinary pack mode');
  }
  if (kind === 'packages' && provenance.imageReuse !== undefined) fail(directory, 'package provenance cannot carry an image reuse plan');
  if (provenance.components.length !== components.length || provenance.artifacts.length !== components.length) {
    fail(directory, `provenance must contain exactly the ${kind} selection`);
  }

  const byId = new Map(components.map(component => [component.id, component]));
  const componentRecords = new Map();
  for (const [index, record] of provenance.components.entries()) {
    const expected = components[index];
    if (!expected || record?.id !== expected.id || record.kind !== expected.kind ||
        record.version !== expected.version || record.project !== expected.project ||
        componentRecords.has(record.id)) {
      fail(directory, `component provenance does not match the ${kind} selection`);
    }
    componentRecords.set(record.id, record);
  }

  const allowedFiles = new Set(['provenance.json']);
  const artifactRecords = [];
  const seenComponents = new Set();
  for (const artifact of provenance.artifacts) {
    const component = byId.get(artifact?.componentId);
    const expectedKind = component?.kind === 'service' ? 'image' : 'package';
    if (!component || artifact.kind !== expectedKind || seenComponents.has(component.id) ||
        (kind === 'packages' && expectedKind !== 'package') ||
        (kind === 'images' && expectedKind !== 'image') ||
        !digest.test(artifact.sha256 ?? '') || !Number.isSafeInteger(artifact.size) || artifact.size <= 0) {
      fail(directory, `artifact provenance contains a missing, duplicate, or wrong-kind ${kind} component`);
    }
    const artifactPath = safeArtifactPath(artifact.path, `${directory}/provenance.json`);
    const artifactStats = lstatSync(path.join(directory, artifactPath));
    if (!artifactStats.isFile() || artifactStats.size !== artifact.size) {
      fail(directory, `artifact is missing or has a different size: ${artifactPath}`);
    }
    allowedFiles.add(artifactPath);
    if (artifact.ociArchive !== undefined) {
      const archive = artifact.ociArchive;
      const archivePath = safeArtifactPath(archive?.path, `${directory}/provenance.json`);
      const archiveStats = lstatSync(path.join(directory, archivePath));
      if (!archiveStats.isFile() || archiveStats.size !== archive.size ||
          !digest.test(archive.sha256 ?? '') || !/^sha256:[a-f0-9]{64}$/.test(archive.configDigest ?? '')) {
        fail(directory, `AgentHost OCI archive metadata is incomplete: ${archivePath}`);
      }
      allowedFiles.add(archivePath);
    }
    seenComponents.add(component.id);
    artifactRecords.push(artifact);
  }
  if (seenComponents.size !== components.length) fail(directory, `artifact provenance is incomplete for ${kind}`);
  const actualFiles = listRegularFiles(directory);
  if (JSON.stringify(actualFiles) !== JSON.stringify([...allowedFiles].sort())) {
    fail(directory, `artifact directory contains missing or unlisted ${kind} files`);
  }
  return { provenance, componentRecords, artifacts: artifactRecords };
}

// Combines package and image packs only when both are bound to the same clean source and manifest.
// The publisher rechecks every artifact hash and image contract before it creates a claim or pushes.
export function mergePacks(manifestPath, packagesDir, imagesDir, outDir, {
  root = repositoryRoot,
  platformOnly = false,
  now = () => new Date(),
} = {}) {
  root = path.resolve(root);
  if (git(root, 'status', '--porcelain') !== '') fail('git', 'working tree must be clean before merging release packs');
  const sourceSha = git(root, 'rev-parse', 'HEAD');
  if (!sha.test(sourceSha)) fail('git', 'cannot resolve an exact source commit');

  const resolvedManifest = resolveWorkspacePath(root, manifestPath, 'manifest');
  const manifest = validateFile(resolvedManifest, { root });
  const relativeManifest = path.relative(root, resolvedManifest).replaceAll('\\', '/');
  const sourceManifest = readJsonFromGit(root, `HEAD:${relativeManifest}`);
  if (JSON.stringify(sourceManifest) !== JSON.stringify(manifest)) fail('manifest', 'composition differs from source HEAD');

  const components = platformOnly
    ? manifest.components.filter(component => component.id !== 'Agentweaver.FoundationProbe')
    : manifest.components;
  const packages = components.filter(component => component.kind !== 'service');
  const images = components.filter(component => component.kind === 'service');
  if (packages.length === 0 || images.length === 0) fail('manifest', 'split release requires package and image components');

  const resolvedPackages = resolveWorkspacePath(root, packagesDir, 'packagesDir');
  const resolvedImages = resolveWorkspacePath(root, imagesDir, 'imagesDir');
  const resolvedOut = resolveWorkspacePath(root, outDir, 'outDir');
  if (resolvedPackages === resolvedImages || inside(resolvedPackages, resolvedImages) ||
      inside(resolvedImages, resolvedPackages) || inside(resolvedPackages, resolvedOut) ||
      inside(resolvedImages, resolvedOut) || inside(resolvedOut, resolvedPackages) ||
      inside(resolvedOut, resolvedImages)) {
    fail('paths', 'package, image, and combined artifact directories must not overlap');
  }
  if (existsSync(resolvedOut)) {
    if (!lstatSync(resolvedOut).isDirectory() || readdirSync(resolvedOut).length > 0) {
      fail('outDir', 'expected a fresh, empty combined artifact directory');
    }
  }

  const manifestSha256 = createHash('sha256').update(JSON.stringify(manifest)).digest('hex');
  // Reused images keep their published digest, so the image pack holds only the rebuilt subset.
  const imageReuse = readJson(path.join(resolvedImages, 'provenance.json'), `${resolvedImages}/provenance.json`).imageReuse;
  if (imageReuse !== undefined) {
    if (!platformOnly) fail('imageReuse', 'image reuse applies only to platform-only release packs');
    try {
      assertImageReusePlan(imageReuse, { sourceSha, manifest });
    } catch (error) {
      fail('imageReuse', error.message);
    }
  }
  const reused = reusedImageIds(imageReuse);
  const builtComponents = components.filter(component => !reused.has(component.id));
  const packagePack = readPack(resolvedPackages, 'packages', packages, sourceSha, manifestSha256, manifest.stage);
  const imagePack = readPack(resolvedImages, 'images', images.filter(component => !reused.has(component.id)),
    sourceSha, manifestSha256, manifest.stage);
  if (JSON.stringify(imagePack.provenance.imageReuse) !== JSON.stringify(imageReuse)) {
    fail('imageReuse', 'image pack plan changed while merging');
  }
  const records = new Map([...packagePack.componentRecords, ...imagePack.componentRecords]);
  const artifactRecords = [...packagePack.artifacts, ...imagePack.artifacts].sort((a, b) => a.path.localeCompare(b.path));
  if (records.size !== builtComponents.length || artifactRecords.length !== builtComponents.length) {
    fail('provenance', 'combined component set does not match the selected manifest');
  }

  mkdirSync(resolvedOut, { recursive: true });
  const outputFiles = new Set();
  for (const [directory, pack] of [[resolvedPackages, packagePack], [resolvedImages, imagePack]]) {
    for (const relativePath of listRegularFiles(directory)) {
      if (relativePath === 'provenance.json') continue;
      if (outputFiles.has(relativePath)) fail('artifacts', `duplicate artifact path: ${relativePath}`);
      outputFiles.add(relativePath);
      const destination = path.join(resolvedOut, relativePath);
      mkdirSync(path.dirname(destination), { recursive: true });
      copyFileSync(path.join(directory, relativePath), destination);
    }
  }
  const combined = {
    schemaVersion: 1,
    sourceSha,
    manifestSha256,
    stage: manifest.stage,
    createdAt: now().toISOString(),
    ...(imageReuse !== undefined ? { imageReuse } : {}),
    components: builtComponents.map(component => records.get(component.id)),
    artifacts: artifactRecords,
  };
  const provenancePath = path.join(resolvedOut, 'provenance.json');
  const temporaryPath = `${provenancePath}.tmp-${process.pid}`;
  try {
    writeFileSync(temporaryPath, `${JSON.stringify(combined, null, 2)}\n`, { flag: 'wx' });
    renameSync(temporaryPath, provenancePath);
  } catch (error) {
    try {
      if (existsSync(temporaryPath)) unlinkSync(temporaryPath);
    } catch (cleanupError) {
      throw new AggregateError([error, cleanupError], `${error.message}; temporary provenance cleanup failed: ${cleanupError.message}`);
    }
    throw error;
  }
  if (git(root, 'rev-parse', 'HEAD') !== sourceSha || git(root, 'status', '--porcelain') !== '') {
    fail('git', 'source changed while merging release packs');
  }
  return combined;
}

function readJsonFromGit(root, ref) {
  try {
    return JSON.parse(execFileSync('git', ['show', ref], {
      cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
    }));
  } catch (error) {
    fail('manifest', `cannot read source composition: ${error.message}`);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const [manifest, packages, images, out, ...flags] = process.argv.slice(2);
    if (!manifest || !packages || !images || !out ||
        flags.some(flag => flag !== '--platform-only') ||
        flags.filter(flag => flag === '--platform-only').length > 1) {
      fail('usage', 'node scripts/release/merge-packs.mjs <manifest.json> <packages-dir> <images-dir> <out-dir> [--platform-only]');
    }
    const provenance = mergePacks(manifest, packages, images, out, {
      root: repositoryRoot,
      platformOnly: flags.includes('--platform-only'),
    });
    console.log(`Merged ${provenance.components.length} component(s) at source ${provenance.sourceSha} into ${out}`);
  } catch (error) {
    console.error(`Release pack merge failed: ${error.message}`);
    process.exitCode = 1;
  }
}
