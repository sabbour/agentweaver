/**
 * Verifies AgentHost build provenance, archive bytes, measurements, and loaded-image metadata.
 * Release packing and publication use these checks to ensure the same source-bound image is carried through.
 */
import { createHash } from 'node:crypto';
import { closeSync, fstatSync, lstatSync, openSync, readFileSync, readSync } from 'node:fs';
import path from 'node:path';
import { measureAgentHostArchiveSync } from './measure-agenthost-image.mjs';

/** Manifest identifier for the AgentHost component. */
export const AGENTHOST_ID = 'Agentweaver.AgentHost';
/** Project file included in the AgentHost source and archive verification boundary. */
export const AGENTHOST_PROJECT = 'services/agenthost/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj';
/** Dockerfile used to build the AgentHost container image. */
export const AGENTHOST_DOCKERFILE = 'services/agenthost/Agentweaver.AgentHost/Dockerfile';
const runtimePath = 'services/agenthost/Agentweaver.AgentHost/runtime-manifest.json';
/** Source directories copied into the AgentHost Docker build context; image reuse fingerprints them too. */
export const AGENTHOST_INPUT_DIRECTORIES = Object.freeze([
  'packages/Agentweaver.Abstractions', 'packages/Agentweaver.Identity',
  'packages/Agentweaver.AgentRuntime', 'packages/Agentweaver.Providers',
  'packages/Agentweaver.SourceControl', 'packages/Agentweaver.Telemetry',
  'services/agenthost/Agentweaver.AgentHost',
]);
const inputDirectories = AGENTHOST_INPUT_DIRECTORIES;
const reject = message => { throw new Error(`AgentHost image: ${message}`); };
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const equal = (left, right) => JSON.stringify(left) === JSON.stringify(right);

/** Resolves the AgentHost source tree at an exact commit and hashes its approved input files. */
export function readAgentHostSource(root, sourceSha, git) {
  const sourceTree = git('rev-parse', `${sourceSha}^{tree}`);
  if (!/^[a-f0-9]{40}$/.test(sourceTree)) reject('an exact source tree is required');
  const paths = git('ls-tree', '-r', '--name-only', sourceSha, '--',
    'Directory.Build.props', '.dockerignore', ...inputDirectories).split(/\r?\n/).filter(Boolean).sort();
  const required = ['Directory.Build.props', '.dockerignore', AGENTHOST_DOCKERFILE, runtimePath,
    ...inputDirectories.flatMap(directory => [
      `${directory}/${path.posix.basename(directory)}.csproj`, `${directory}/packages.lock.json`,
    ])];
  if (required.some(file => !paths.includes(file))) reject('the Dockerfile, runtime pins, projects, and all input lockfiles must be committed');
  const files = paths.map(file => {
    const full = path.resolve(root, file);
    if (!full.startsWith(path.resolve(root) + path.sep) || !lstatSync(full).isFile())
      reject(`source input must be a regular file: ${file}`);
    const bytes = readFileSync(full);
    return { path: file, sha256: hash(bytes), size: bytes.length };
  });
  const dockerfile = readFileSync(path.resolve(root, AGENTHOST_DOCKERFILE), 'utf8');
  const from = dockerfile.split(/\r?\n/).filter(line => /^FROM\b/i.test(line.trim()));
  const baseImages = from.map(line => {
    const match = /^FROM\s+(?:--platform=(?:\$BUILDPLATFORM|linux\/amd64)\s+)?(\S+)(?:\s+AS\s+\S+)?\s*$/i.exec(line.trim());
    if (!match || !/@sha256:[a-f0-9]{64}$/.test(match[1]) || match[1].includes('$'))
      reject('every Dockerfile FROM image must have an explicit immutable digest');
    return { reference: match[1], repositoryDigest: match[1] };
  });
  if (baseImages.length === 0) reject('the existing Dockerfile must have pinned base images');
  const runtime = JSON.parse(readFileSync(path.resolve(root, runtimePath), 'utf8'));
  if (typeof runtime.sdkVersion !== 'string' || typeof runtime.runtimeVersion !== 'string' ||
      !/^[a-f0-9]{128}$/.test(runtime.archiveSha512 ?? '') ||
      !dockerfile.includes(`copilot-linux-x64-${runtime.runtimeVersion}.tgz`) ||
      !dockerfile.includes(runtime.archiveSha512))
    reject('the Dockerfile must retain the checked-in native archive version and checksum');
  return { sourceSha, sourceTree, files, baseImages, runtime };
}

/** Reads stable archive metadata and content hashes while rejecting non-regular archive files. */
export function agentHostArchiveMetadata(file, relativePath, configDigest) {
  if (!lstatSync(file).isFile()) reject(`archive must be a regular file: ${relativePath}`);
  const descriptor = openSync(file, 'r');
  try {
    const expectedSize = fstatSync(descriptor).size;
    if (!Number.isSafeInteger(expectedSize) || expectedSize <= 0) reject(`archive has an invalid size: ${relativePath}`);
    const digest = createHash('sha256');
    const buffer = Buffer.alloc(1024 * 1024);
    let size = 0;
    let count;
    while ((count = readSync(descriptor, buffer, 0, buffer.length, null)) > 0) {
      size += count;
      digest.update(buffer.subarray(0, count));
    }
    if (size !== expectedSize) reject(`archive size changed while hashing: ${relativePath}`);
    return { path: relativePath, sha256: digest.digest('hex'), size, configDigest };
  } finally {
    closeSync(descriptor);
  }
}

/** Re-reads the measured archives and rejects any byte or metadata change after measurement. */
export function verifyAgentHostArchives(archives, directory) {
  for (const archive of [archives.docker, archives.oci]) {
    if (!equal(archive, agentHostArchiveMetadata(path.join(directory, archive.path), archive.path, archive.configDigest)))
      reject('an archive changed during measurement or image verification');
  }
}

/** Checks that the measurement receipt matches the exact source, component version, and image platform. */
export function verifyAgentHostMeasurement(measured, source, version) {
  if (measured?.kind !== 'agenthost-image' || measured.schemaVersion !== 1 ||
      measured.sourceSha !== source.sourceSha || measured.sourceTree !== source.sourceTree ||
      measured.image?.platform !== 'linux/amd64' || measured.image.version !== version ||
      !/^sha256:[a-f0-9]{64}$/.test(measured.image.digest ?? '') ||
      !/^sha256:[a-f0-9]{64}$/.test(measured.image.configDigest ?? '') ||
      !Number.isSafeInteger(measured.image.compressedPullBytes) || measured.image.compressedPullBytes <= 0)
    reject('measured OCI bytes, size, source/tree, version, or image identity do not match');
}

/** Verifies that runtime-reported AgentHost versions and layout match the built runtime manifest. */
export function verifyAgentHostRuntimeStatus(status, runtime) {
  if (status?.sdkVersion !== runtime.sdkVersion || status.runtimeVersion !== runtime.runtimeVersion ||
      status.ProtocolVersion !== 3 || status.nativeDistribution !== '/app/native' ||
      status.privatePackageCacheAbsent !== true)
    reject('the pinned image-owned native runtime check failed');
  return {
    sdkVersion: status.sdkVersion, runtimeVersion: status.runtimeVersion, ProtocolVersion: 3,
    nativeDistribution: '/app/native', privatePackageCacheAbsent: true,
  };
}

// Docker reports its image ID as the OCI config digest (classic store) or as the OCI platform
// manifest digest (containerd store). Both are proven by the measured archive bytes, because the
// measured manifest names the measured config. Any other ID is refused.
/** Accepts only a loaded image ID already recorded as the measured config or manifest digest. */
export function verifiedLoadedImageId(image, measured) {
  const id = image?.Id;
  if (id === measured.image.configDigest) return { id, kind: 'config' };
  if (id === measured.image.digest) return { id, kind: 'manifest' };
  return null;
}

/** Inspects a locally loaded image and verifies its labels, digest, source, and runtime contract. */
export function verifyLoadedAgentHostImage(imageReference, measured, source, version, docker) {
  verifyAgentHostMeasurement(measured, source, version);
  const image = JSON.parse(docker(['image', 'inspect', '--format', '{{json .}}', imageReference]));
  const labels = image?.Config?.Labels;
  const loaded = verifiedLoadedImageId(image, measured);
  if (!loaded || image.Os !== 'linux' || image.Architecture !== 'amd64' ||
      image.Config?.User !== '1654:1654' ||
      !equal(image.Config?.Entrypoint, ['dotnet', 'Agentweaver.AgentHost.dll']) ||
      labels?.['org.opencontainers.image.revision'] !== source.sourceSha ||
      labels?.['io.agentweaver.source-tree'] !== source.sourceTree ||
      labels?.['org.opencontainers.image.version'] !== version)
    reject('loaded archive image identity, source/tree labels, version, or execution contract do not match');
  const status = JSON.parse(docker([
    'run', '--rm', '--pull=never', '--platform', 'linux/amd64', '--network', 'none', '--read-only',
    '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
    '--tmpfs', '/state:rw,noexec,nosuid,nodev,uid=1654,gid=1654,mode=700',
    '--tmpfs', '/tmp:rw,noexec,nosuid,nodev,uid=1654,gid=1654,mode=700',
    loaded.id, '--verify-native-runtime',
  ]));
  return { runtime: verifyAgentHostRuntimeStatus(status, source.runtime), loadedImageId: loaded.id, loadedImageIdKind: loaded.kind };
}

/**
 * Validates an AgentHost artifact record against its measured archives and source provenance.
 * Returns verified artifact metadata or rejects missing, altered, or inconsistent evidence.
 */
export function validateAgentHostArtifact(component, artifact, record, root, directory, sourceSha, git,
  measure = measureAgentHostArchiveSync, integrationImagesOnly = false) {
  const source = readAgentHostSource(root, sourceSha, git);
  const repository = 'agentweaver-agenthost';
  const tag = integrationImagesOnly ? sourceSha : `${component.version.replaceAll('+', '_')}-${sourceSha}`;
  const imageReference = `${repository}:${tag}`;
  const dockerPath = `${AGENTHOST_ID}.${component.version}.tar`;
  const ociPath = `${AGENTHOST_ID}.${component.version}.oci.tar`;
  const fail = () => reject('prepared archive provenance does not match both exact archives and their image-config identity');
  if (component.id !== AGENTHOST_ID || component.project !== AGENTHOST_PROJECT || component.kind !== 'service' ||
      artifact.repository !== repository || artifact.tag !== tag || artifact.imageReference !== imageReference ||
      artifact.path !== dockerPath || artifact.platform !== 'linux/amd64' ||
      (integrationImagesOnly && !equal(artifact.integrationLabels, {
        'org.opencontainers.image.revision': sourceSha,
        'io.agentweaver.source-tree': source.sourceTree,
        'org.opencontainers.image.version': component.version,
      })) ||
      !equal(record?.source, source)) fail();
  const archives = {
    docker: agentHostArchiveMetadata(path.join(directory, dockerPath), dockerPath, artifact.imageId),
    oci: agentHostArchiveMetadata(path.join(directory, ociPath), ociPath, artifact.imageId),
  };
  if (!equal(record.build?.archives, archives) ||
      !equal(artifact.ociArchive, archives.oci) || artifact.sha256 !== archives.docker.sha256 ||
      artifact.size !== archives.docker.size ||
      record.build?.tool !== 'docker-buildx' || record.build.dockerfile !== AGENTHOST_DOCKERFILE ||
      record.build.imageReference !== imageReference ||
      record.build.imageId !== artifact.imageId ||
      !equal(record.lock, {
        path: `${path.posix.dirname(AGENTHOST_PROJECT)}/packages.lock.json`,
        sha256: source.files.find(file => file.path === `${path.posix.dirname(AGENTHOST_PROJECT)}/packages.lock.json`).sha256,
      })) fail();
  const measured = measure(path.join(directory, ociPath), root);
  verifyAgentHostMeasurement(measured, source, component.version);
  if (!equal(record.build.measurement, measured) || artifact.imageId !== measured.image.configDigest) fail();
  verifyAgentHostArchives(archives, directory);
  verifyAgentHostRuntimeStatus(record.build?.runtime, source.runtime);
  return { imageReference, measured, source, archives };
}
