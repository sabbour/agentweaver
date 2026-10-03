// Dependency-free artifact preparation: runs a locked `dotnet restore` + `build` + `pack`/container
// publish sequence for each manifest component into a fresh, per-run output
// directory, then records a provenance receipt (source SHA, component versions, and a SHA-256 hash
// of every artifact it produced). This never publishes or pushes anywhere — it only prepares local
// artifacts for a human to inspect and manually publish. See releases/README.md.
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validateFile } from './validate.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const sha = /^[a-f0-9]{40}$/;

function fail(location, message) {
  throw new Error(`${location}: ${message}`);
}

function defaultGit(root) {
  return (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

function defaultStatus(root) {
  return execFileSync('git', ['status', '--porcelain'], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

function defaultDotnet(root) {
  const bin = process.env.DOTNET_HOST_PATH || 'dotnet';
  return (args) => {
    const result = spawnSync(bin, args, { cwd: root, stdio: 'inherit' });
    if (result.error) fail('dotnet', `failed to launch "dotnet ${args.join(' ')}": ${result.error.message}`);
    if (result.status !== 0) fail('dotnet', `"dotnet ${args.join(' ')}" exited with code ${result.status}`);
  };
}

function listFilesRecursive(dir, readdir, stat) {
  const out = [];
  for (const entry of readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) out.push(...listFilesRecursive(full, readdir, stat));
    else if (entry.isFile()) out.push(full);
  }
  return out;
}

function hashFile(file, readFile) {
  return createHash('sha256').update(readFile(file)).digest('hex');
}

/**
 * Restores, builds, and packs every packable manifest component into `outDir`, then writes an
 * atomic `provenance.json` receipt once every package has been produced successfully. Fails closed
 * (no provenance written) on: a dirty working tree before or after the build (rejects drift from a
 * build step that unexpectedly touched checked-in files), a non-empty `outDir` (a fresh directory is
 * required per run so artifacts from different runs are never mixed), or any failing `dotnet`
 * invocation. Never pushes or publishes any artifact anywhere.
 */
export function packComponents(manifest, {
  root = repositoryRoot,
  git = defaultGit(root),
  status = () => defaultStatus(root),
  dotnet = defaultDotnet(root),
  readdirSync: readdir = readdirSync,
  readFileSync: readFile = readFileSync,
  writeFileSync: writeFileImpl = writeFileSync,
  renameSync: renameImpl = renameSync,
  mkdirSync: mkdirImpl = mkdirSync,
  existsSync: existsImpl = existsSync,
  statSync: stat = statSync,
  now = () => new Date(),
  outDir,
} = {}) {
  if (typeof outDir !== 'string' || outDir.trim() === '') fail('outDir', 'expected an output directory path');
  const components = manifest.components;
  if (components.length === 0) fail('manifest.components', 'no components to prepare');

  const before = status();
  if (before.trim() !== '') fail('git', 'working tree must be clean before packing: commit or stash pending changes first');

  const sourceSha = git('rev-parse', 'HEAD');
  if (!sha.test(sourceSha)) fail('git', 'cannot resolve a full 40-character commit SHA for HEAD');
  if (manifest.stage === 'release' && manifest.evidence.sourceSha !== sourceSha) fail('manifest.evidence', 'released composition evidence must match preparation HEAD');
  const expectedArtifacts = new Map();
  const locks = new Map();
  for (const component of components) {
    const project = path.resolve(root, component.project);
    const lock = path.join(path.dirname(project), 'packages.lock.json');
    if (!existsImpl(lock)) fail(component.project, 'checked-in packages.lock.json is required before preparation');
    const lockPath = path.relative(root, lock).replaceAll('\\', '/');
    git('cat-file', '-e', `${sourceSha}:${component.project}`);
    git('cat-file', '-e', `${sourceSha}:${lockPath}`);
    locks.set(component.id, { path: lockPath, sha256: hashFile(lock, readFile) });
    if (component.kind === 'service') {
      const xml = readFile(project, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
      const baseImages = [...xml.matchAll(/<ContainerBaseImage\b[^>]*>([^<]*)<\/ContainerBaseImage\s*>/g)];
      const baseImage = baseImages[0]?.[1].trim();
      if (baseImages.length !== 1 || !baseImage || !/@sha256:[a-f0-9]{64}$/.test(baseImage)) {
        fail(component.project, 'service preparation requires exactly one active explicit immutable ContainerBaseImage digest in the project');
      }
      expectedArtifacts.set(`${component.id}.${component.version}.tar.gz`, {
        componentId: component.id, kind: 'image', repository: component.id.toLowerCase(), tag: component.version, baseImage,
      });
    } else {
      expectedArtifacts.set(`${path.basename(component.project, '.csproj')}.${component.version}.nupkg`, { componentId: component.id, kind: 'package' });
    }
  }

  const resolvedOutDir = path.resolve(root, outDir);
  if (existsImpl(resolvedOutDir) && listFilesRecursive(resolvedOutDir, readdir, stat).length > 0) {
    fail('outDir', `expected a fresh, empty output directory, found existing content at ${path.relative(root, resolvedOutDir)}`);
  }
  mkdirImpl(resolvedOutDir, { recursive: true });

  for (const component of components) {
    const project = path.resolve(root, component.project);
    const image = [...expectedArtifacts.values()].find((artifact) => artifact.componentId === component.id && artifact.kind === 'image');
    const baseImageArgs = image ? [`-p:ContainerBaseImage=${image.baseImage}`] : [];
    dotnet(['restore', project, '--locked-mode']);
    dotnet(['build', project, '--configuration', 'Release', '--no-restore',
      '-p:ContinuousIntegrationBuild=true', `-p:RepositoryCommit=${sourceSha}`, ...baseImageArgs]);
    const projectStem = path.basename(component.project, '.csproj');
    const expectedFile = component.kind === 'service'
      ? `${component.id}.${component.version}.tar.gz` : `${projectStem}.${component.version}.nupkg`;
    if (component.kind === 'service') {
      dotnet(['publish', project, '--configuration', 'Release', '--no-restore', '-t:PublishContainer',
        '-p:EnableSdkContainerSupport=true', '-p:ContinuousIntegrationBuild=true', `-p:RepositoryCommit=${sourceSha}`,
        ...baseImageArgs,
        `-p:ContainerRepository=${component.id.toLowerCase()}`, `-p:ContainerImageTag=${component.version}`,
        `-p:ContainerArchiveOutputPath=${path.join(resolvedOutDir, expectedFile)}`]);
    } else {
      dotnet(['pack', project, '--configuration', 'Release', '--no-build', '--no-restore', '--output', resolvedOutDir,
        `-p:RepositoryCommit=${sourceSha}`]);
    }
    if (!existsImpl(path.join(resolvedOutDir, expectedFile))) {
      fail('pack', `expected ${component.kind === 'service' ? 'image' : 'package'} artifact ${expectedFile} was not produced for ${component.id}`);
    }
  }

  const headAfter = git('rev-parse', 'HEAD');
  if (headAfter !== sourceSha) {
    fail('git', 'HEAD moved during packing; investigate before writing provenance');
  }

  const after = status();
  if (after.trim() !== '') {
    fail('git', 'working tree is no longer clean after the build (generated or modified tracked files detected); investigate before packing');
  }

  const producedFiles = listFilesRecursive(resolvedOutDir, readdir, stat).sort();
  if (producedFiles.length === 0) fail('pack', 'dotnet pack produced no artifacts');

  const artifacts = producedFiles.map((file) => ({
    path: path.relative(resolvedOutDir, file).replaceAll('\\', '/'),
    sha256: hashFile(file, readFile),
    size: stat(file).size,
    ...expectedArtifacts.get(path.relative(resolvedOutDir, file).replaceAll('\\', '/')),
  }));

  const provenance = {
    schemaVersion: 1,
    sourceSha,
    manifestSha256: createHash('sha256').update(JSON.stringify(manifest)).digest('hex'),
    stage: manifest.stage,
    createdAt: now().toISOString(),
    components: components.map(({ id, kind, version, project }) => ({ id, kind, version, project, lock: locks.get(id) })),
    artifacts,
  };

  const provenancePath = path.join(resolvedOutDir, 'provenance.json');
  const tmp = `${provenancePath}.tmp-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  try {
    writeFileImpl(tmp, JSON.stringify(provenance, null, 2) + '\n');
    renameImpl(tmp, provenancePath);
  } catch (error) {
    try {
      if (existsImpl(tmp)) unlinkSync(tmp);
    } catch (cleanupError) {
      throw new AggregateError([error, cleanupError], `${error.message}; provenance cleanup failed: ${cleanupError.message}`);
    }
    throw error;
  }

  return provenance;
}

export function packComponentsFromFile(manifestPath, options = {}) {
  const manifest = validateFile(manifestPath, options);
  const root = options.root ?? repositoryRoot;
  const relative = path.relative(root, path.resolve(root, manifestPath)).replaceAll('\\', '/');
  const source = execFileSync('git', ['show', `HEAD:${relative}`], { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  if (JSON.stringify(JSON.parse(source)) !== JSON.stringify(manifest)) fail('manifest', 'composition differs from source HEAD');
  return packComponents(manifest, options);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const args = process.argv.slice(2);
    const manifestArg = args.find((arg) => !arg.startsWith('--'));
    const outIndex = args.indexOf('--out');
    const out = outIndex >= 0 ? args[outIndex + 1] : 'artifacts/release/pack';
    if (!manifestArg || (outIndex >= 0 && !args[outIndex + 1])) {
      fail('usage', 'node scripts/release/pack.mjs <manifest.json> [--out <dir>]');
    }
    const provenance = packComponentsFromFile(manifestArg, { root: repositoryRoot, outDir: out });
    console.log(`Packed ${provenance.components.length} component(s) at source ${provenance.sourceSha} into ${out}`);
    for (const artifact of provenance.artifacts) {
      console.log(`  ${artifact.path} (${artifact.sha256})`);
    }
  } catch (error) {
    console.error(`Release pack failed: ${error.message}`);
    process.exitCode = 1;
  }
}
