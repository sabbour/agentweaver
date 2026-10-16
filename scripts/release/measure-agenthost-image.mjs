#!/usr/bin/env node
import { execFileSync, spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const digestPattern = /^sha256:[a-f0-9]{64}$/;
const sourcePattern = /^[a-f0-9]{40}$/;
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const hash = bytes => `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
const blobPath = digest => `blobs/sha256/${digest.slice(7)}`;

export async function measureAgentHostImage(indexBytes, readEntry, source) {
  if (!sourcePattern.test(source?.sha ?? '') || !sourcePattern.test(source?.tree ?? ''))
    throw new Error('An exact clean Git source SHA and tree are required.');
  const index = JSON.parse(indexBytes);
  if (index.schemaVersion !== 2 || index.manifests?.length !== 1)
    throw new Error('The OCI archive must contain one exact platform manifest, not a multi-platform tag.');
  const descriptor = index.manifests[0];
  if (!digestPattern.test(descriptor.digest ?? '') || !Number.isSafeInteger(descriptor.size) || descriptor.size <= 0)
    throw new Error('The OCI manifest descriptor is invalid.');
  const manifestBytes = await readEntry(blobPath(descriptor.digest), true);
  if (manifestBytes.length !== descriptor.size || hash(manifestBytes) !== descriptor.digest)
    throw new Error('The OCI platform manifest bytes do not match their descriptor.');
  const manifest = JSON.parse(manifestBytes);
  if (manifest.schemaVersion !== 2 ||
      manifest.mediaType !== 'application/vnd.oci.image.manifest.v1+json' ||
      !Array.isArray(manifest.layers) || manifest.layers.length === 0 ||
      manifest.config?.mediaType !== 'application/vnd.oci.image.config.v1+json' ||
      manifest.layers.some(layer => layer.mediaType !== 'application/vnd.oci.image.layer.v1.tar+gzip'))
    throw new Error('An OCI image with gzip-compressed layers and an explicit config is required.');
  const blobs = new Map();
  let compressedPullBytes = manifestBytes.length;
  for (const blob of [manifest.config, ...manifest.layers]) {
    if (!digestPattern.test(blob.digest ?? '') || !Number.isSafeInteger(blob.size) || blob.size <= 0 ||
        blobs.has(blob.digest) && blobs.get(blob.digest) !== blob.size)
      throw new Error('An OCI blob descriptor is invalid or conflicting.');
    if (blobs.has(blob.digest)) continue;
    const measured = await readEntry(blobPath(blob.digest), blob === manifest.config);
    const measuredSize = Buffer.isBuffer(measured) ? measured.length : measured.size;
    const measuredDigest = Buffer.isBuffer(measured) ? hash(measured) : measured.digest;
    if (measuredSize !== blob.size || measuredDigest !== blob.digest)
      throw new Error('An OCI blob does not match its exact compressed size and digest.');
    if (blob !== manifest.config && !(Buffer.isBuffer(measured)
      ? measured[0] === 0x1f && measured[1] === 0x8b && measured[2] === 8 : measured.gzip))
      throw new Error('The declared compressed OCI layer is not a gzip blob.');
    blobs.set(blob.digest, blob.size);
    compressedPullBytes += blob.size;
    if (!Number.isSafeInteger(compressedPullBytes))
      throw new Error('The measured OCI pull-byte total exceeds the exact integer range.');
  }
  const config = JSON.parse(await readEntry(blobPath(manifest.config.digest), true));
  if (config.os !== 'linux' || config.architecture !== 'amd64' || config.config?.User !== '1654:1654' ||
      config.config?.Labels?.['org.opencontainers.image.revision'] !== source.sha ||
      config.config?.Labels?.['io.agentweaver.source-tree'] !== source.tree ||
      JSON.stringify(config.config?.Entrypoint) !== JSON.stringify(['dotnet', 'Agentweaver.AgentHost.dll']))
    throw new Error('The image platform, non-root owner, executable, or clean-source labels do not match.');
  return {
    kind: 'agenthost-image', schemaVersion: 1, sourceSha: source.sha, sourceTree: source.tree,
    image: {
      digest: descriptor.digest, configDigest: manifest.config.digest, platform: 'linux/amd64', compressedPullBytes,
      ...(config.config.Labels['org.opencontainers.image.version'] !== undefined
        ? { version: config.config.Labels['org.opencontainers.image.version'] } : {}),
    },
    measurement: 'platform-manifest plus unique config and gzip-layer blob bytes',
  };
}

async function readTarEntry(archive, entry, retain) {
  const process = spawn('tar', ['-xOf', archive, entry], { windowsHide: true });
  const digest = createHash('sha256');
  const content = [];
  let size = 0;
  let header = Buffer.alloc(0);
  let errors = '';
  process.stderr.setEncoding('utf8');
  process.stderr.on('data', text => { errors += text; });
  process.stdout.on('data', bytes => {
    size += bytes.length;
    digest.update(bytes);
    if (header.length < 3) header = Buffer.concat([header, bytes]).subarray(0, 3);
    if (retain) {
      if (size > 2 * 1024 * 1024) process.kill();
      else content.push(bytes);
    }
  });
  await new Promise((resolve, reject) => {
    process.once('error', reject);
    process.once('close', code => code === 0 && (!retain || size <= 2 * 1024 * 1024)
      ? resolve() : reject(new Error(`Cannot read the bounded OCI archive entry ${entry}: ${errors.trim()}`)));
  });
  return retain ? Buffer.concat(content) : {
    size, digest: `sha256:${digest.digest('hex')}`, gzip: header[0] === 0x1f && header[1] === 0x8b && header[2] === 8,
  };
}

export function measureAgentHostArchiveSync(archive, repoRoot, sourceSha) {
  if (sourceSha !== undefined && !sourcePattern.test(sourceSha))
    throw new Error('An explicit AgentHost measurement source must be an exact commit SHA.');
  return JSON.parse(execFileSync(process.execPath, [
    fileURLToPath(import.meta.url), '--stdout', resolve(archive), resolve(repoRoot),
    ...(sourceSha === undefined ? [] : [sourceSha]),
  ], { encoding: 'utf8', maxBuffer: 2 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] }));
}

function git(args, repoRoot = root) {
  const result = spawnSync('git', args, { cwd: repoRoot, encoding: 'utf8', windowsHide: true });
  if (result.error || result.status !== 0) throw new Error('Cannot read the exact Git image source.');
  return result.stdout.trim();
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    const stdout = process.argv[2] === '--stdout';
    const explicitSource = stdout && process.argv.length === 6 ? process.argv[5] : undefined;
    if (process.argv.length !== (stdout ? (explicitSource === undefined ? 5 : 6) : 4) ||
        (explicitSource !== undefined && !sourcePattern.test(explicitSource)))
      throw new Error('Usage: measure-agenthost-image.mjs <gzip-OCI-archive> <receipt.json> | --stdout <gzip-OCI-archive> <source-root> [<source-sha>]');
    const repoRoot = stdout ? resolve(process.argv[4]) : root;
    const inputs = [
      'Directory.Build.props', '.dockerignore',
      'packages/Agentweaver.Abstractions', 'packages/Agentweaver.Identity',
      'packages/Agentweaver.AgentRuntime', 'packages/Agentweaver.Providers',
      'packages/Agentweaver.SourceControl', 'packages/Agentweaver.Telemetry',
      'services/agenthost/Agentweaver.AgentHost',
    ];
    if (git(['status', '--porcelain', '--untracked-files=normal', ...(stdout ? [] : ['--', ...inputs])], repoRoot))
      throw new Error('A clean Git working tree is required before writing an image receipt.');
    const sourceRevision = explicitSource ?? 'HEAD';
    const source = {
      sha: git(['rev-parse', '--verify', `${sourceRevision}^{commit}`], repoRoot),
      tree: git(['rev-parse', `${sourceRevision}^{tree}`], repoRoot),
    };
    const archive = resolve(process.argv[stdout ? 3 : 2]);
    const receipt = await measureAgentHostImage(
      await readTarEntry(archive, 'index.json', true), (entry, retain) => readTarEntry(archive, entry, retain), source);
    if (stdout) {
      console.log(JSON.stringify(receipt));
    } else {
      const destination = resolve(process.argv[3]);
      mkdirSync(dirname(destination), { recursive: true });
      writeFileSync(destination, `${JSON.stringify(receipt, null, 2)}\n`, { flag: 'wx' });
      console.log(`Measured ${receipt.image.compressedPullBytes} compressed OCI pull bytes: ${receipt.image.digest}`);
    }
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
