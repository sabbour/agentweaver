/**
 * Builds manifest components and records source, version, and artifact hashes.
 * It prepares local or workflow artifacts only; publication is a separate action.
 */
import { createHash } from 'node:crypto';
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  validateFile,
  WEB_HOST_LOCK_PATH,
  WEB_HOST_PROJECT_PATH,
  WEB_LOCK_PATH,
  WEB_PROJECT_PATH,
} from './validate.mjs';
import { resolveProbeImageSource } from '../azure/build-foundation-probe-image.mjs';
import {
  AGENTHOST_ID, AGENTHOST_PROJECT, AGENTHOST_DOCKERFILE,
  readAgentHostSource, agentHostArchiveMetadata, verifyAgentHostArchives, verifyAgentHostMeasurement, verifyLoadedAgentHostImage,
} from './agenthost-image.mjs';
import { measureAgentHostArchiveSync } from './measure-agenthost-image.mjs';
import { readDockerArchiveConfig } from './docker-archive.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const sha = /^[a-f0-9]{40}$/;
const INTEGRATION_SERVICE_IDS = Object.freeze([
  'Agentweaver.AgentHost',
  'Agentweaver.Identity.Broker',
  'Agentweaver.EventsAndSessions',
  'Agentweaver.Gateway',
  'Agentweaver.Mcp',
  'Agentweaver.Web',
  'Agentweaver.Orchestrator',
  'Agentweaver.Environment',
  'Agentweaver.Knowledge',
  'Agentweaver.Projects.Config',
].sort());

/** Returns the fixed container repository name for a component that produces an image. */
export function componentImageRepository(componentId) {
  if (componentId === 'Agentweaver.FoundationProbe') return 'agentweaver-foundation-probe';
  if (componentId === 'Agentweaver.Web') return 'agentweaver-web';
  if (componentId === AGENTHOST_ID) return 'agentweaver-agenthost';
  return componentId.toLowerCase();
}

/** Builds a registry tag unique to the component version and exact source commit. */
export function componentImageTag(version, sourceSha) {
  return `${version.replaceAll('+', '_')}-${sourceSha}`;
}

/** Selects service image components included in images-only integration releases. */
export function selectIntegrationImageComponents(manifest) {
  const components = manifest.components.filter(component =>
    component.kind === 'service' && component.id !== 'Agentweaver.FoundationProbe');
  const ids = components.map(component => component.id).sort();
  if (ids.length !== INTEGRATION_SERVICE_IDS.length ||
      ids.some((id, index) => id !== INTEGRATION_SERVICE_IDS[index])) {
    fail('selection', 'integration images-only preparation requires the exact ten product services and excludes FoundationProbe');
  }
  return components;
}

function fail(location, message) {
  throw new Error(`${location}: ${message}`);
}

function verifyIntegrationDockerConfig(archive, imageReference, sourceSha, sourceTree, version) {
  let image;
  try {
    image = readDockerArchiveConfig(archive, imageReference);
  } catch (error) {
    fail('docker', `cannot verify prepared image config from ${archive}: ${error.message}`);
  }
  const labels = image.config?.config?.Labels;
  if (!/^sha256:[a-f0-9]{64}$/.test(image.configDigest) ||
      image.config.os !== 'linux' || image.config.architecture !== 'amd64' ||
      labels?.['org.opencontainers.image.revision'] !== sourceSha ||
      labels?.['io.agentweaver.source-tree'] !== sourceTree ||
      labels?.['org.opencontainers.image.version'] !== version) {
    fail('docker', `prepared image ${imageReference} does not match the exact source, tree, version, and linux/amd64 platform`);
  }
  return {
    imageConfigDigest: image.configDigest,
    labels: {
      'org.opencontainers.image.revision': labels['org.opencontainers.image.revision'],
      'io.agentweaver.source-tree': labels['io.agentweaver.source-tree'],
      'org.opencontainers.image.version': labels['org.opencontainers.image.version'],
    },
  };
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

function defaultNpm(env) {
  return (args, cwd) => {
    const bin = process.platform === 'win32' ? 'npm.cmd' : 'npm';
    const result = spawnSync(bin, args, {
      cwd, stdio: 'inherit', env, shell: process.platform === 'win32',
    });
    if (result.error) fail('npm', `failed to launch "${bin} ${args.join(' ')}": ${result.error.message}`);
    if (result.status !== 0) fail('npm', `"${bin} ${args.join(' ')}" exited with code ${result.status}`);
  };
}

function defaultDocker(root) {
  return (args, cwd = root) => {
    const result = spawnSync(process.env.DOCKER_HOST_PATH || 'docker', args, {
      cwd, stdio: ['ignore', 'pipe', 'inherit'],
      ...(args[0] === 'run' ? { timeout: 60_000 } : {}),
    });
    if (result.error) fail('docker', `failed to launch "docker ${args.join(' ')}": ${result.error.message}`);
    if (result.status !== 0) fail('docker', `"docker ${args.join(' ')}" exited with code ${result.status}`);
    return result.stdout.toString('utf8').trim();
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

function listRegularWebFiles(dir, readdir, location = 'apps/web/dist') {
  const out = [];
  for (const entry of readdir(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) out.push(...listRegularWebFiles(full, readdir, location));
    else if (entry.isFile()) out.push(full);
    else fail(location, `unsupported build output entry ${path.relative(dir, full)}`);
  }
  return out;
}

function parseWebDockerfile(text) {
  const logical = text.replaceAll('\r\n', '\n').split('\n')
    .reduce((lines, line) => {
      if (lines.length && lines.at(-1).endsWith('\\')) {
        lines[lines.length - 1] = `${lines.at(-1).slice(0, -1)} ${line.trim()}`;
      } else {
        lines.push(line.trim());
      }
      return lines;
    }, []);
  const images = [];
  const stages = new Set();
  for (const line of logical) {
    if (!line || line.startsWith('#')) continue;
    const match = /^FROM\s+(\S+)(?:\s+AS\s+(\S+))?$/i.exec(line);
    if (!match) {
      if (/^FROM\b/i.test(line)) fail('apps/web/Dockerfile', `unsupported or mutable FROM instruction "${line}"`);
      const copy = /^COPY\s+(.+)$/i.exec(line);
      if (copy) {
        const from = /(?:^|\s)--from=([^\s]+)/i.exec(copy[1])?.[1];
        if (from && !stages.has(from.toLowerCase()) && !/^\d+$/.test(from)) {
          fail('apps/web/Dockerfile', `COPY --from=${from} must refer to a declared pinned FROM stage`);
        }
      }
      if (/^RUN\b/i.test(line)) {
        for (const [, from] of line.matchAll(/(?:^|[, ])from=([^\s,]+)/gi)) {
          if (!stages.has(from.toLowerCase()) && !/^\d+$/.test(from)) {
            fail('apps/web/Dockerfile', `RUN mount from=${from} must refer to a declared pinned FROM stage`);
          }
        }
      }
      if (/^ADD\b/i.test(line) && /\b(?:https?:\/\/|git@|git:\/\/)/i.test(line)) {
        fail('apps/web/Dockerfile', 'remote ADD sources are not permitted in a source-bound web image');
      }
      continue;
    }
    const reference = match[1];
    const at = reference.lastIndexOf('@sha256:');
    if (at < 1 || !/^sha256:[a-f0-9]{64}$/.test(reference.slice(at + 1)) ||
        reference.slice(0, at).includes('$')) {
      fail('apps/web/Dockerfile', `every FROM image must be pinned by an explicit immutable digest: ${reference}`);
    }
    const repository = reference.slice(0, at);
    if (repository.split('/').at(-1).includes(':')) {
      fail('apps/web/Dockerfile', `FROM image must not include a mutable tag: ${reference}`);
    }
    images.push({ reference, repositoryDigest: reference });
    if (match[2]) stages.add(match[2].toLowerCase());
  }
  if (images.length === 0) fail('apps/web/Dockerfile', 'expected at least one pinned FROM image');
  return images;
}

function createWebDockerContext(root, sourceFiles, distDir, publishDir, contextDir, {
  readFile, readdir, stat, writeFile, mkdir,
}) {
  for (const source of sourceFiles) {
    const sourcePath = path.resolve(root, source.path);
    const relative = path.relative(path.resolve(root, 'apps', 'web'), sourcePath);
    const destination = path.join(contextDir, relative);
    mkdir(path.dirname(destination), { recursive: true });
    writeFile(destination, readFile(sourcePath));
  }
  for (const file of listFilesRecursive(distDir, readdir, stat)) {
    const relative = path.relative(distDir, file);
    const destination = path.join(contextDir, 'wwwroot', relative);
    mkdir(path.dirname(destination), { recursive: true });
    writeFile(destination, readFile(file));
  }
  for (const file of listFilesRecursive(publishDir, readdir, stat)) {
    const relative = path.relative(publishDir, file);
    const destination = path.join(contextDir, 'publish', relative);
    mkdir(path.dirname(destination), { recursive: true });
    writeFile(destination, readFile(file));
  }
}

function sourceTreeMetadata(root, sourceSha, git, readFile, stat) {
  const output = git('ls-tree', '-r', '--name-only', sourceSha, '--', 'apps/web');
  const paths = output.split(/\r?\n/).filter(Boolean).sort();
  if (![WEB_PROJECT_PATH, WEB_LOCK_PATH, WEB_HOST_PROJECT_PATH, WEB_HOST_LOCK_PATH, 'apps/web/Dockerfile']
    .every((file) => paths.includes(file))) {
    fail('apps/web', 'package.json, both committed lockfiles, the ASP.NET host project, and Dockerfile must all exist in source HEAD');
  }
  if (paths.some((file) => file.split('/').includes('dist') || file.split('/').includes('node_modules'))) {
    fail('apps/web', 'dist and node_modules must not be checked into the release source tree');
  }
  const files = paths.map((file) => {
    const full = path.resolve(root, file);
    if (!existsSync(full) || !lstatSync(full).isFile() || !stat(full).isFile()) {
      fail(file, 'committed web source file must be a regular file in the working tree');
    }
    return { path: file, sha256: hashFile(full, readFile), size: stat(full).size };
  });
  const treeSha256 = createHash('sha256').update(JSON.stringify(files.map(({ path: file, sha256 }) => [file, sha256]))).digest('hex');
  return { treeSha256, files };
}

function verifyPinnedBaseImages(images, docker, webDir) {
  const resolved = [];
  for (const image of images) {
    const { reference } = image;
    let output;
    try {
      output = docker(['image', 'inspect', '--format={{json .}}', reference], webDir);
    } catch (error) {
      fail('docker', `pinned FROM image ${reference} is not available locally; refusing registry access: ${error.message}`);
    }
    let info;
    try {
      info = JSON.parse(output);
    } catch {
      fail('docker', `cannot inspect local pinned FROM image ${reference}`);
    }
    const digest = reference.slice(reference.lastIndexOf('@'));
    const expected = `${reference.slice(0, reference.lastIndexOf('@')).replace(/:[^/:]+$/, '')}${digest}`;
    if (!/^sha256:[a-f0-9]{64}$/.test(info?.Id ?? '') ||
        !Array.isArray(info.RepoDigests) || !info.RepoDigests.includes(expected)) {
      fail('docker', `locally available FROM image does not have the required immutable RepoDigest ${expected}`);
    }
    resolved.push({ ...image, imageId: info.Id });
  }
  return resolved;
}

/** Removes Vite-prefixed variables so local build settings cannot leak into release output. */
export function cleanBuildEnvironment(environment) {
  return Object.fromEntries(Object.entries(environment).filter(([name]) => !/^VITE_/i.test(name)));
}

function rejectLocalBuildInputs(webDir, readdir) {
  const files = readdir(webDir, { withFileTypes: true });
  const localEnv = files.filter(({ name }) =>
    name === '.env.local' || /^\.env\..*\.local$/i.test(name)).map(({ name }) => name);
  if (localEnv.length) fail('apps/web', `local Vite environment files are not source-bound build inputs: ${localEnv.join(', ')}`);
  if (existsSync(path.join(webDir, 'dist'))) {
    fail('apps/web/dist', 'stale or prebuilt dist output must be removed before source packing');
  }
}

function hashFile(file, readFile) {
  return createHash('sha256').update(readFile(file)).digest('hex');
}

/**
 * Restores, builds, and packs every packable manifest component into `outDir`, then writes an
 * atomic `provenance.json` receipt once every package or local image has been produced successfully.
 * Fails closed (no provenance written) on a dirty working tree, stale output, or any failing build.
 * Web images are built only from a locked npm install, a clean staged context, and locally present
 * immutable base images. Never pushes or publishes any artifact anywhere.
 */
/**
 * Builds each manifest component from a clean, exact source and records artifact hashes and provenance.
 * It returns the receipt and output paths; restore, build, archive, or validation failures throw.
 */
export function packComponents(manifest, {
  root = repositoryRoot,
  git = defaultGit(root),
  status = () => defaultStatus(root),
  dotnet = defaultDotnet(root),
  npm,
  docker,
  measureAgentHostArchive = measureAgentHostArchiveSync,
  environment = process.env,
  readdirSync: readdir = readdirSync,
  readFileSync: readFile = readFileSync,
  writeFileSync: writeFileImpl = writeFileSync,
  renameSync: renameImpl = renameSync,
  mkdirSync: mkdirImpl = mkdirSync,
  existsSync: existsImpl = existsSync,
  statSync: stat = statSync,
  now = () => new Date(),
  outDir,
  packagesOnly = false,
  imagesOnly = false,
  foundationProbeOnly = false,
  platformOnly = false,
  integrationSourceSha,
} = {}) {
  if (typeof outDir !== 'string' || outDir.trim() === '') fail('outDir', 'expected an output directory path');
  const integrationImagesOnly = integrationSourceSha !== undefined;
  if (integrationImagesOnly && !sha.test(integrationSourceSha)) {
    fail('selection', 'integration image preparation requires an exact 40-character source SHA');
  }
  if (integrationImagesOnly && (packagesOnly || imagesOnly || foundationProbeOnly || platformOnly)) {
    fail('selection', 'integration images-only preparation cannot combine with another selection');
  }
  if (packagesOnly && (imagesOnly || foundationProbeOnly)) {
    fail('selection', 'package-only preparation cannot combine with image-only or Foundation Probe-only preparation');
  }
  if (platformOnly && (packagesOnly || foundationProbeOnly))
    fail('selection', 'platform-only preparation cannot combine with package-only or Foundation Probe-only preparation');
  const compositionComponents = integrationImagesOnly
    ? selectIntegrationImageComponents(manifest)
    : platformOnly
    ? manifest.components.filter(component => component.id !== 'Agentweaver.FoundationProbe')
    : foundationProbeOnly
    ? manifest.components.filter(component => component.id === 'Agentweaver.FoundationProbe' && component.kind === 'service')
    : packagesOnly
    ? manifest.components.filter((component) => component.kind !== 'service')
    : manifest.components;
  // The release workflow builds package and image outputs independently; platform-only image packs omit the acceptance Probe.
  const components = imagesOnly
    ? compositionComponents.filter((component) => component.kind === 'service')
    : compositionComponents;
  if (components.length === 0) fail('manifest.components', 'no components to prepare');
  if (integrationImagesOnly && (manifest.stage !== 'draft' || manifest.evidence !== undefined)) {
    fail('manifest', 'integration image preparation requires a tracked draft manifest with no deployment evidence');
  }

  const before = status();
  if (before.trim() !== '') fail('git', 'working tree must be clean before packing: commit or stash pending changes first');

  const sourceSha = git('rev-parse', 'HEAD');
  if (!sha.test(sourceSha)) fail('git', 'cannot resolve a full 40-character commit SHA for HEAD');
  if (integrationImagesOnly && integrationSourceSha !== sourceSha) {
    fail('git', 'integration source SHA must match the exact clean local HEAD');
  }
  if (manifest.stage === 'release' && manifest.evidence.sourceSha !== sourceSha) fail('manifest.evidence', 'released composition evidence must match preparation HEAD');
  // Shared product images carry the exact commit tree in both release and integration packs.
  const sourceTree = git('rev-parse', `${sourceSha}^{tree}`);
  if (!sha.test(sourceTree)) {
    fail('git', 'cannot resolve the exact 40-character source tree for product images');
  }
  const integrationSourceTree = integrationImagesOnly ? sourceTree : undefined;
  const productImageLabels = (version) => ({
    'org.opencontainers.image.revision': sourceSha,
    'io.agentweaver.source-tree': sourceTree,
    'org.opencontainers.image.version': version,
  });
  const probeSource = components.some(component => component.id === 'Agentweaver.FoundationProbe')
    ? resolveProbeImageSource({ repoRoot: root, readFile }) : undefined;
  if (probeSource && probeSource.sourceSha !== sourceSha) fail('git', 'Probe image source changed before preparation');
  const buildEnvironment = cleanBuildEnvironment(environment);
  const runNpm = npm ?? defaultNpm(buildEnvironment);
  const runDotnet = dotnet ?? defaultDotnet(root);
  const runDocker = docker ?? defaultDocker(root);
  const expectedArtifacts = new Map();
  const locks = new Map();
  const webSources = new Map();
  const webBuilds = new Map();
  const agentHostSources = new Map();
  const agentHostBuilds = new Map();
  const auxiliaryArchives = new Set();
  for (const component of components) {
    if (component.project === WEB_PROJECT_PATH) {
      if (component.id !== 'Agentweaver.Web' || component.kind !== 'service') {
        fail(component.project, 'only Agentweaver.Web may be packed from an npm project');
      }
      const lock = path.resolve(root, WEB_LOCK_PATH);
      if (!existsImpl(lock)) fail(component.project, 'committed npm package-lock.json is required before preparation');
      const hostLock = path.resolve(root, WEB_HOST_LOCK_PATH);
      if (!existsImpl(hostLock)) fail(component.project, 'committed ASP.NET host packages.lock.json is required before preparation');
      for (const file of [
        WEB_PROJECT_PATH, WEB_LOCK_PATH, WEB_HOST_PROJECT_PATH, WEB_HOST_LOCK_PATH, 'apps/web/Dockerfile',
      ]) {
        try {
          git('cat-file', '-e', `${sourceSha}:${file}`);
        } catch (error) {
          fail(file, `must be committed in source HEAD before web preparation: ${error.message}`);
        }
      }
      let webPackage;
      let webLock;
      try {
        webPackage = JSON.parse(readFile(path.resolve(root, WEB_PROJECT_PATH), 'utf8'));
        webLock = JSON.parse(readFile(lock, 'utf8'));
      } catch (error) {
        fail(WEB_PROJECT_PATH, `cannot read npm project mirrors: ${error.message}`);
      }
      if (webPackage?.version !== component.version ||
          webLock?.version !== component.version ||
          webLock?.packages?.['']?.version !== component.version) {
        fail(WEB_PROJECT_PATH, `package.json, package-lock.json, and package-lock.json packages[""] must all mirror ${component.version}`);
      }
      locks.set(component.id, {
        path: WEB_LOCK_PATH,
        sha256: hashFile(lock, readFile),
      });
      const source = sourceTreeMetadata(root, sourceSha, git, readFile, stat);
      const dockerfilePath = path.join(root, 'apps', 'web', 'Dockerfile');
      const dockerfile = readFile(dockerfilePath, 'utf8');
      const baseImages = parseWebDockerfile(dockerfile);
      const artifactName = `${component.id}.${component.version}.tar`;
      const tag = integrationImagesOnly ? sourceSha : `${component.version.replaceAll('+', '_')}-${sourceSha}`;
      if (tag.length > 128) fail(component.id, 'version-derived Docker tag exceeds the 128-character limit');
      const imageReference = `${componentImageRepository(component.id)}:${tag}`;
      webSources.set(component.id, {
        treeSha256: source.treeSha256,
        files: source.files,
        packageJsonSha256: source.files.find(({ path: file }) => file === WEB_PROJECT_PATH).sha256,
        packageLockSha256: source.files.find(({ path: file }) => file === WEB_LOCK_PATH).sha256,
        hostProjectSha256: source.files.find(({ path: file }) => file === WEB_HOST_PROJECT_PATH).sha256,
        hostLockSha256: source.files.find(({ path: file }) => file === WEB_HOST_LOCK_PATH).sha256,
        dockerfileSha256: source.files.find(({ path: file }) => file === 'apps/web/Dockerfile').sha256,
      });
      expectedArtifacts.set(artifactName, {
        componentId: component.id,
        kind: 'image',
        repository: componentImageRepository(component.id),
        tag,
        imageReference,
        platform: 'linux/amd64',
        baseImages,
        ...(integrationImagesOnly ? { integrationLabels: productImageLabels(component.version) } : {}),
      });
      continue;
    }
    const project = path.resolve(root, component.project);
    const lock = path.join(path.dirname(project), 'packages.lock.json');
    if (!existsImpl(lock)) fail(component.project, 'checked-in packages.lock.json is required before preparation');
    const lockPath = path.relative(root, lock).replaceAll('\\', '/');
    git('cat-file', '-e', `${sourceSha}:${component.project}`);
    git('cat-file', '-e', `${sourceSha}:${lockPath}`);
    locks.set(component.id, { path: lockPath, sha256: hashFile(lock, readFile) });
    if (component.id === AGENTHOST_ID || component.project === AGENTHOST_PROJECT) {
      if (component.id !== AGENTHOST_ID || component.project !== AGENTHOST_PROJECT || component.kind !== 'service')
        fail(component.project, 'only the AgentHost service may use its native Dockerfile preparation');
      const source = readAgentHostSource(root, sourceSha, git);
      const tag = integrationImagesOnly ? sourceSha : `${component.version.replaceAll('+', '_')}-${sourceSha}`;
      if (tag.length > 128) fail(component.id, 'version-derived Docker tag exceeds the 128-character limit');
      agentHostSources.set(component.id, source);
      auxiliaryArchives.add(`${component.id}.${component.version}.oci.tar`);
      expectedArtifacts.set(`${component.id}.${component.version}.tar`, {
        componentId: component.id, kind: 'image', repository: componentImageRepository(component.id),
        tag, imageReference: `${componentImageRepository(component.id)}:${tag}`, platform: 'linux/amd64',
        ...(integrationImagesOnly ? { integrationLabels: productImageLabels(component.version) } : {}),
      });
      if (integrationImagesOnly && source.sourceTree !== integrationSourceTree) {
        fail(component.id, 'AgentHost source tree differs from the exact integration source tree');
      }
      continue;
    }
    if (component.kind === 'service') {
      const xml = readFile(project, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
      const baseImages = [...xml.matchAll(/<ContainerBaseImage\b[^>]*>([^<]*)<\/ContainerBaseImage\s*>/g)];
      const baseImage = baseImages[0]?.[1].trim();
      if (baseImages.length !== 1 || !baseImage || !/@sha256:[a-f0-9]{64}$/.test(baseImage)) {
        fail(component.project, 'service preparation requires exactly one active explicit immutable ContainerBaseImage digest in the project');
      }
      expectedArtifacts.set(`${component.id}.${component.version}.tar.gz`, {
        componentId: component.id, kind: 'image', repository: componentImageRepository(component.id),
        tag: integrationImagesOnly ? sourceSha : component.version, baseImage,
        ...(integrationImagesOnly ? {
          imageReference: `${componentImageRepository(component.id)}:${sourceSha}`,
          integrationLabels: productImageLabels(component.version),
        } : {}),
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
    if (component.id === AGENTHOST_ID) {
      const source = agentHostSources.get(component.id);
      const contextDir = path.join(resolvedOutDir, '.agenthost-context');
      const dockerPath = `${component.id}.${component.version}.tar`;
      const ociPath = `${component.id}.${component.version}.oci.tar`;
      const artifact = expectedArtifacts.get(dockerPath);
      if (status().trim() !== '' || git('rev-parse', 'HEAD') !== sourceSha ||
          JSON.stringify(readAgentHostSource(root, sourceSha, git)) !== JSON.stringify(source))
        fail('git', 'AgentHost inputs changed before image preparation');
      try {
        verifyPinnedBaseImages(source.baseImages, runDocker, root);
        for (const file of source.files) {
          const destination = path.join(contextDir, file.path);
          mkdirImpl(path.dirname(destination), { recursive: true });
          writeFileImpl(destination, readFile(path.resolve(root, file.path)));
        }
        // Keep AgentHost's OCI labels aligned with the source pins used for SDK container images.
        runDocker([
          'buildx', 'build', '--pull=false', '--no-cache', '--platform', 'linux/amd64',
          '--file', AGENTHOST_DOCKERFILE, '--tag', artifact.imageReference, '--load',
          '--output', `type=oci,dest=${path.join(resolvedOutDir, ociPath)}`,
          '--provenance=false', '--sbom=false',
          '--label', `org.opencontainers.image.revision=${sourceSha}`,
          '--label', `io.agentweaver.source-tree=${source.sourceTree}`,
          '--label', `org.opencontainers.image.version=${component.version}`, '.',
        ], contextDir);
        const ociArchive = agentHostArchiveMetadata(path.join(resolvedOutDir, ociPath), ociPath);
        const measured = measureAgentHostArchive(path.join(resolvedOutDir, ociPath), root);
        verifyAgentHostMeasurement(measured, source, component.version);
        runDocker(['save', '--output', path.join(resolvedOutDir, dockerPath), artifact.imageReference]);
        if (integrationImagesOnly) {
          const prepared = verifyIntegrationDockerConfig(path.join(resolvedOutDir, dockerPath),
            artifact.imageReference, sourceSha, integrationSourceTree, component.version);
          if (prepared.imageConfigDigest !== measured.image.configDigest) {
            fail(component.id, 'Docker archive config digest differs from the measured OCI config');
          }
          artifact.imageConfigDigest = prepared.imageConfigDigest;
          artifact.labels = prepared.labels;
        }
        const archives = {
          docker: agentHostArchiveMetadata(path.join(resolvedOutDir, dockerPath), dockerPath, measured.image.configDigest),
          oci: { ...ociArchive, configDigest: measured.image.configDigest },
        };
        runDocker(['load', '--input', path.join(resolvedOutDir, dockerPath)]);
        const { runtime } = verifyLoadedAgentHostImage(artifact.imageReference, measured, source, component.version, runDocker);
        verifyAgentHostArchives(archives, resolvedOutDir);
        artifact.imageId = measured.image.configDigest;
        artifact.ociArchive = archives.oci;
        agentHostBuilds.set(component.id, {
          tool: 'docker-buildx', dockerfile: AGENTHOST_DOCKERFILE, imageReference: artifact.imageReference,
          imageId: measured.image.configDigest, archives, measurement: measured, runtime,
        });
      } finally {
        rmSync(contextDir, { recursive: true, force: true });
      }
      continue;
    }
    if (component.project === WEB_PROJECT_PATH) {
      const webDir = path.resolve(root, 'apps', 'web');
      const distDir = path.join(webDir, 'dist');
      const hostPublishDir = path.join(resolvedOutDir, '.web-host-publish');
      const contextDir = path.join(resolvedOutDir, '.web-context');
      rejectLocalBuildInputs(webDir, readdir);
      let buildOutput;
      try {
        runNpm(['ci', '--offline', '--no-audit', '--no-fund', '--no-progress'], webDir, buildEnvironment);
        runNpm(['run', 'build'], webDir, buildEnvironment);
        if (!existsImpl(distDir) || !lstatSync(distDir).isDirectory()) fail('apps/web/dist', 'npm run build did not produce a fresh dist directory');
        runDotnet(['restore', path.resolve(root, WEB_HOST_PROJECT_PATH), '--locked-mode']);
        runDotnet([
          'publish', path.resolve(root, WEB_HOST_PROJECT_PATH),
          '--configuration', 'Release', '--no-restore', '--output', hostPublishDir,
          '-p:UseAppHost=false', '-p:ContinuousIntegrationBuild=true', `-p:RepositoryCommit=${sourceSha}`,
        ]);
        if (!existsImpl(hostPublishDir) || !lstatSync(hostPublishDir).isDirectory()) {
          fail(WEB_HOST_PROJECT_PATH, 'dotnet publish did not produce the ASP.NET host output directory');
        }
        if (status().trim() !== '') fail('git', 'web build left tracked or untracked output outside ignored build artifacts');
        const webFiles = listRegularWebFiles(distDir, readdir).sort()
          .map((file) => ({
            path: path.relative(distDir, file).replaceAll('\\', '/'),
            sha256: hashFile(file, readFile),
            size: stat(file).size,
          }));
        if (webFiles.length === 0) fail('apps/web/dist', 'npm run build produced no files');
        const hostFiles = listRegularWebFiles(hostPublishDir, readdir, WEB_HOST_PROJECT_PATH).sort()
          .map((file) => ({
            path: path.relative(hostPublishDir, file).replaceAll('\\', '/'),
            sha256: hashFile(file, readFile),
            size: stat(file).size,
          }));
        if (hostFiles.length === 0) fail(WEB_HOST_PROJECT_PATH, 'dotnet publish produced no files');
        const hostBuildOutput = {
          files: hostFiles,
          sha256: createHash('sha256').update(JSON.stringify(hostFiles)).digest('hex'),
        };
        const files = [
          ...webFiles.map((file) => ({ ...file, path: `wwwroot/${file.path}` })),
          ...hostFiles.map((file) => ({ ...file, path: `publish/${file.path}` })),
        ].sort((left, right) => left.path.localeCompare(right.path));
        buildOutput = {
          files,
          sha256: createHash('sha256').update(JSON.stringify(files)).digest('hex'),
        };

        const artifact = [...expectedArtifacts.values()].find(({ componentId }) => componentId === component.id);
        const webDockerfile = readFile(path.join(webDir, 'Dockerfile'), 'utf8');
        const baseImages = verifyPinnedBaseImages(parseWebDockerfile(webDockerfile), runDocker, webDir);
        createWebDockerContext(root, webSources.get(component.id).files, distDir, hostPublishDir, contextDir, {
          readFile, readdir, stat, writeFile: writeFileImpl, mkdir: mkdirImpl,
        });
        runDocker([
          'build', '--pull=false', '--network=none', '--no-cache', '--platform=linux/amd64',
          '--tag', artifact.imageReference,
          '--build-arg', `IMAGE_TAG=${component.version}`,
          '--build-arg', `GIT_SHA=${sourceSha}`,
          ...Object.entries(productImageLabels(component.version))
            .flatMap(([name, value]) => ['--label', `${name}=${value}`]),
          '--file', 'Dockerfile', '.',
        ], contextDir);
        let imageInfo;
        try {
          imageInfo = JSON.parse(runDocker(['image', 'inspect', '--format={{json .}}', artifact.imageReference], contextDir));
        } catch (error) {
          fail('docker', `cannot inspect locally built web image: ${error.message}`);
        }
        if (!imageInfo || !/^sha256:[a-f0-9]{64}$/.test(imageInfo.Id ?? '') ||
            imageInfo.Os !== 'linux' || imageInfo.Architecture !== 'amd64' ||
            imageInfo.Config?.Labels?.['org.opencontainers.image.revision'] !== sourceSha ||
            imageInfo.Config?.Labels?.['io.agentweaver.source-tree'] !== sourceTree ||
            imageInfo.Config?.Labels?.['org.opencontainers.image.version'] !== component.version) {
          fail('docker', 'built web image is missing its exact source, tree, or component-version labels');
        }
        const artifactName = `${component.id}.${component.version}.tar`;
        const artifactPath = path.join(resolvedOutDir, artifactName);
        runDocker(['save', '--output', artifactPath, artifact.imageReference], contextDir);
        const preparedConfig = integrationImagesOnly
          ? verifyIntegrationDockerConfig(artifactPath, artifact.imageReference, sourceSha, integrationSourceTree, component.version)
          : undefined;
        if (preparedConfig && JSON.stringify(preparedConfig.labels) !== JSON.stringify({
          'org.opencontainers.image.revision': imageInfo.Config.Labels['org.opencontainers.image.revision'],
          'io.agentweaver.source-tree': imageInfo.Config.Labels['io.agentweaver.source-tree'],
          'org.opencontainers.image.version': imageInfo.Config.Labels['org.opencontainers.image.version'],
        })) {
          fail(component.id, 'saved web image config differs from the inspected built image labels');
        }
        if (preparedConfig) artifact.imageConfigDigest = preparedConfig.imageConfigDigest;
        artifact.imageId = imageInfo.Id;
        artifact.labels = {
          'org.opencontainers.image.revision': imageInfo.Config.Labels['org.opencontainers.image.revision'],
          'io.agentweaver.source-tree': imageInfo.Config.Labels['io.agentweaver.source-tree'],
          'org.opencontainers.image.version': imageInfo.Config.Labels['org.opencontainers.image.version'],
        };
        artifact.buildOutput = buildOutput;
        artifact.baseImages = baseImages;
        webBuilds.set(component.id, {
          tool: 'npm+dotnet',
          install: ['npm', 'ci', '--offline', '--no-audit', '--no-fund', '--no-progress'],
          build: ['npm', 'run', 'build'],
          dotnet: {
            project: WEB_HOST_PROJECT_PATH,
            lock: { path: WEB_HOST_LOCK_PATH, sha256: webSources.get(component.id).hostLockSha256 },
            restoreMode: 'locked',
            publishConfiguration: 'Release',
            useAppHost: false,
            output: hostBuildOutput,
          },
          environment: 'ambient VITE_* removed; local .env files rejected',
          output: buildOutput,
          dockerfile: 'apps/web/Dockerfile',
          dockerBuildArguments: { IMAGE_TAG: component.version, GIT_SHA: sourceSha },
          imageId: imageInfo.Id,
          imageReference: artifact.imageReference,
          platform: artifact.platform,
          baseImages,
        });
      } finally {
        rmSync(distDir, { recursive: true, force: true });
        rmSync(hostPublishDir, { recursive: true, force: true });
        rmSync(contextDir, { recursive: true, force: true });
      }
      if (!existsImpl(path.join(resolvedOutDir, `${component.id}.${component.version}.tar`))) {
        fail('pack', `expected web image archive for ${component.id} was not produced`);
      }
      continue;
    }
    const project = path.resolve(root, component.project);
    const image = [...expectedArtifacts.values()].find((artifact) => artifact.componentId === component.id && artifact.kind === 'image');
    const baseImageArgs = image ? [`-p:ContainerBaseImage=${image.baseImage}`] : [];
    const probeSourceArgs = component.id === 'Agentweaver.FoundationProbe' ? [
      `-p:ProbeSourceSha=${probeSource.sourceSha}`,
      `-p:ProbeSourceTree=${probeSource.sourceTree}`,
      `-p:ProbeInfrastructureHash=${probeSource.sourceHash}`,
    ] : [];
    // Product release and integration images share the exact source and version labels.
    const sourceLabelArgs = component.kind === 'service'
      && component.id !== 'Agentweaver.FoundationProbe' ? [
      `-p:AgentweaverImageSourceSha=${sourceSha}`,
      `-p:AgentweaverImageSourceTree=${sourceTree}`,
      `-p:AgentweaverImageVersion=${component.version}`,
    ] : [];
    dotnet(['restore', project, '--locked-mode']);
    dotnet(['build', project, '--configuration', 'Release', '--no-restore',
      '-p:ContinuousIntegrationBuild=true', `-p:RepositoryCommit=${sourceSha}`, ...baseImageArgs, ...probeSourceArgs]);
    const projectStem = path.basename(component.project, '.csproj');
    const expectedFile = component.kind === 'service'
      ? `${component.id}.${component.version}.tar.gz` : `${projectStem}.${component.version}.nupkg`;
    if (component.kind === 'service') {
      dotnet(['publish', project, '--configuration', 'Release', '--no-restore', '-t:PublishContainer',
        '-p:EnableSdkContainerSupport=true', '-p:ContinuousIntegrationBuild=true', `-p:RepositoryCommit=${sourceSha}`,
        ...baseImageArgs, ...probeSourceArgs, ...sourceLabelArgs,
        `-p:ContainerRepository=${image.repository}`, `-p:ContainerImageTag=${integrationImagesOnly ? sourceSha : component.version}`,
        `-p:ContainerArchiveOutputPath=${path.join(resolvedOutDir, expectedFile)}`]);
    } else {
      dotnet(['pack', project, '--configuration', 'Release', '--no-build', '--no-restore', '--output', resolvedOutDir,
        `-p:RepositoryCommit=${sourceSha}`]);
    }
    if (!existsImpl(path.join(resolvedOutDir, expectedFile))) {
      fail('pack', `expected ${component.kind === 'service' ? 'image' : 'package'} artifact ${expectedFile} was not produced for ${component.id}`);
    }
    if (integrationImagesOnly && component.kind === 'service') {
      const artifactPath = path.join(resolvedOutDir, expectedFile);
      const imageReference = image.imageReference;
      runDocker(['load', '--input', artifactPath]);
      const prepared = verifyIntegrationDockerConfig(artifactPath, imageReference,
        sourceSha, integrationSourceTree, component.version);
      image.imageConfigDigest = prepared.imageConfigDigest;
      image.labels = prepared.labels;
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
  if (agentHostBuilds.has(AGENTHOST_ID)) verifyAgentHostArchives(agentHostBuilds.get(AGENTHOST_ID).archives, resolvedOutDir);

  const artifacts = producedFiles.filter(file =>
    !auxiliaryArchives.has(path.relative(resolvedOutDir, file).replaceAll('\\', '/'))).map(file => {
    const relativePath = path.relative(resolvedOutDir, file).replaceAll('\\', '/');
    const expected = expectedArtifacts.get(relativePath);
    const dockerArchive = expected?.componentId === AGENTHOST_ID ? agentHostBuilds.get(AGENTHOST_ID).archives.docker : undefined;
    return {
      path: relativePath, sha256: dockerArchive?.sha256 ?? hashFile(file, readFile),
      size: dockerArchive?.size ?? stat(file).size, ...expected,
    };
  });

  const provenance = {
    schemaVersion: 1,
    sourceSha,
    ...(integrationImagesOnly ? {
      integration: { mode: 'images-only', sourceTree: integrationSourceTree, imageTag: sourceSha },
    } : {}),
    manifestSha256: createHash('sha256').update(JSON.stringify(manifest)).digest('hex'),
    stage: manifest.stage,
    createdAt: now().toISOString(),
    components: components.map(({ id, kind, version, project }) => ({
      id, kind, version, project, lock: locks.get(id),
      ...(webSources.has(id) ? { source: webSources.get(id), build: webBuilds.get(id) } : {}),
      ...(agentHostSources.has(id) ? { source: agentHostSources.get(id), build: agentHostBuilds.get(id) } : {}),
    })),
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

/** Loads and validates a release manifest, then delegates artifact preparation to packComponents(). */
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
    const outIndex = args.indexOf('--out');
    const out = outIndex >= 0 ? args[outIndex + 1] : 'artifacts/release/pack';
    const integrationIndex = args.indexOf('--integration-source-sha');
    const integrationSourceSha = integrationIndex >= 0 ? args[integrationIndex + 1] : undefined;
    if (integrationIndex >= 0 &&
        (args.lastIndexOf('--integration-source-sha') !== integrationIndex || !sha.test(integrationSourceSha ?? ''))) {
      fail('selection', '--integration-source-sha requires one exact 40-character source SHA');
    }
    const excludedIndices = new Set([
      ...(integrationIndex >= 0 ? [integrationIndex + 1] : []),
      ...(outIndex >= 0 ? [outIndex + 1] : []),
    ]);
    const manifestArg = args.find((arg, index) => !arg.startsWith('--') && !excludedIndices.has(index));
    const packagesOnly = args.includes('--packages-only');
    const imagesOnly = args.includes('--images-only');
    const foundationProbeOnly = args.includes('--foundation-probe-only');
    const platformOnly = args.includes('--platform-only');
    if (!manifestArg || (outIndex >= 0 && !args[outIndex + 1])) {
      fail('usage', 'node scripts/release/pack.mjs <manifest.json> [--packages-only | --images-only [--platform-only] | --foundation-probe-only | --platform-only | --integration-source-sha <sha>] [--out <dir>]');
    }
    const provenance = packComponentsFromFile(manifestArg, {
      root: repositoryRoot, outDir: out, packagesOnly, imagesOnly, foundationProbeOnly, platformOnly, integrationSourceSha,
    });
    console.log(`Packed ${provenance.components.length} component(s) at source ${provenance.sourceSha} into ${out}`);
    for (const artifact of provenance.artifacts) {
      console.log(`  ${artifact.path} (${artifact.sha256})`);
    }
  } catch (error) {
    console.error(`Release pack failed: ${error.message}`);
    process.exitCode = 1;
  }
}
