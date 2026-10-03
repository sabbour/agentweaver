#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const dockerfile = 'tools/Agentweaver.FoundationProbe/Dockerfile';
const receiptPath = join(root, 'artifacts', 'images', 'foundation-probe.json');
const sourceShaPattern = /^[0-9a-f]{40}$/;
const digestPattern = /^sha256:[0-9a-f]{64}$/;
const sourceHashPattern = /^[0-9a-f]{64}$/;
const numericUserPattern = /^[1-9]\d*:[1-9]\d*$/;

function runProcess(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, encoding: 'utf8', windowsHide: true });
  if (result.error) throw new Error(`${command} could not be started.`);
  if (result.status !== 0) throw new Error(`${command} exited with code ${result.status ?? 1}.`);
  return result.stdout ?? '';
}

export function buildImageReceipt({ sourceSha, sourceTree, sourceHash, imageReference, inspect }) {
  if (!sourceShaPattern.test(sourceSha ?? '') || !sourceShaPattern.test(sourceTree ?? '') ||
      !sourceHashPattern.test(sourceHash ?? '') || !imageReference || !inspect ||
      !digestPattern.test(inspect.Id ?? '')) {
    throw new Error('Image source or local image metadata is invalid.');
  }
  const labels = inspect.Config?.Labels;
  if (labels?.['org.opencontainers.image.revision'] !== sourceSha ||
      labels?.['io.agentweaver.source-tree'] !== sourceTree ||
      labels?.['io.agentweaver.infrastructure-source-hash'] !== sourceHash) {
    throw new Error('Built image labels do not match the clean Git source.');
  }
  const configUser = inspect.Config?.User;
  if (!numericUserPattern.test(configUser ?? '')) {
    throw new Error('Built image must run as a numeric non-root UID and GID.');
  }
  const repositoryDigests = inspect.RepoDigests;
  if (!Array.isArray(repositoryDigests) ||
      repositoryDigests.some(value => typeof value !== 'string' || !/^.+@sha256:[0-9a-f]{64}$/.test(value))) {
    throw new Error('Docker did not return valid repository manifest digests.');
  }

  return {
    kind: 'foundation-probe-image',
    schemaVersion: 1,
    sourceSha,
    sourceTree,
    sourceHash,
    image: {
      localReference: imageReference,
      localConfigDigest: inspect.Id,
      configUser,
      repositoryDigests,
    },
  };
}

export function verifyPublishedImageReceipt(receipt, imageReference, inspect) {
  if (typeof imageReference === 'string' && imageReference.includes('@')) {
    throw new Error('Use a registry image reference without a caller-supplied digest.');
  }
  if (receipt?.kind !== 'foundation-probe-image' || receipt.schemaVersion !== 1 ||
      !sourceShaPattern.test(receipt.sourceSha ?? '') || !sourceShaPattern.test(receipt.sourceTree ?? '') ||
      !sourceHashPattern.test(receipt.sourceHash ?? '') ||
      !digestPattern.test(receipt.image?.localConfigDigest ?? '') ||
      !numericUserPattern.test(receipt.image?.configUser ?? '') ||
      typeof imageReference !== 'string' || !imageReference || imageReference.includes('@') ||
      inspect?.Id !== receipt.image.localConfigDigest ||
      inspect?.Config?.User !== receipt.image.configUser) {
    throw new Error('Published image does not match the source-bound local image receipt.');
  }
  const labels = inspect.Config?.Labels;
  if (labels?.['org.opencontainers.image.revision'] !== receipt.sourceSha ||
      labels?.['io.agentweaver.source-tree'] !== receipt.sourceTree ||
      labels?.['io.agentweaver.infrastructure-source-hash'] !== receipt.sourceHash) {
    throw new Error('Published image labels do not match the source-bound receipt.');
  }
  const repositoryDigests = inspect.RepoDigests;
  const repository = imageReference.slice(0, imageReference.lastIndexOf(':') > imageReference.lastIndexOf('/')
    ? imageReference.lastIndexOf(':') : imageReference.length);
  if (!Array.isArray(repositoryDigests) ||
      repositoryDigests.some(value => typeof value !== 'string' || !/^.+@sha256:[0-9a-f]{64}$/.test(value)) ||
      !repositoryDigests.some(value => value.startsWith(`${repository}@`))) {
    throw new Error('Docker did not return a matching registry manifest digest.');
  }

  return {
    ...receipt,
    image: {
      ...receipt.image,
      publishedReference: imageReference,
      repositoryDigests,
    },
  };
}

export function buildFoundationProbeImage({
  repoRoot = root,
  run = runProcess,
  readFile = readFileSync,
  writeReceipt = writeFileSync,
} = {}) {
  const dirty = run('git', ['status', '--porcelain', '--untracked-files=normal'], repoRoot);
  if (dirty.trim()) throw new Error('The image receipt requires a clean Git working tree.');

  const sourceSha = run('git', ['rev-parse', 'HEAD'], repoRoot).trim();
  const sourceTree = run('git', ['rev-parse', 'HEAD^{tree}'], repoRoot).trim();
  if (!sourceShaPattern.test(sourceSha) || !sourceShaPattern.test(sourceTree)) {
    throw new Error('Git HEAD and tree must resolve to full lowercase SHA-1 values.');
  }
  const ignored = run('git', ['ls-files', '--others', '--ignored', '--exclude-standard', '--', 'infra/bicep'], repoRoot);
  if (ignored.trim()) throw new Error('Ignored or untracked infrastructure inputs are not an exact source.');
  const files = run('git', ['ls-files', '--', 'infra/bicep'], repoRoot)
    .trim().split(/\r?\n/).filter(Boolean).sort();
  if (files.length === 0) throw new Error('Tracked infrastructure inputs are missing.');
  const hash = createHash('sha256');
  for (const file of files) {
    const content = readFile(join(repoRoot, file)).toString().replaceAll('\r\n', '\n');
    const committed = run('git', ['show', `${sourceSha}:${file}`], repoRoot).replaceAll('\r\n', '\n');
    if (content !== committed) throw new Error('Infrastructure inputs do not match the clean Git source.');
    hash.update(file).update('\0').update(content).update('\0');
  }
  const sourceHash = hash.digest('hex');

  const imageReference = `agentweaver-foundation-probe:source-${sourceSha}`;
  run('docker', [
    'build',
    '--platform', 'linux/amd64',
    '--file', dockerfile,
    '--tag', imageReference,
    '--label', `org.opencontainers.image.revision=${sourceSha}`,
    '--label', `io.agentweaver.source-tree=${sourceTree}`,
    '--label', `io.agentweaver.infrastructure-source-hash=${sourceHash}`,
    '--build-arg', `PROBE_SOURCE_SHA=${sourceSha}`,
    '--build-arg', `PROBE_SOURCE_TREE=${sourceTree}`,
    '--build-arg', `PROBE_INFRASTRUCTURE_HASH=${sourceHash}`,
    '.',
  ], repoRoot);
  const inspected = JSON.parse(run('docker', ['image', 'inspect', imageReference], repoRoot));
  if (!Array.isArray(inspected) || inspected.length !== 1) {
    throw new Error('Docker image inspection did not return exactly one local image.');
  }

  const receipt = buildImageReceipt({
    sourceSha,
    sourceTree,
    sourceHash,
    imageReference,
    inspect: inspected[0],
  });
  mkdirSync(dirname(receiptPath), { recursive: true });
  writeReceipt(receiptPath, `${JSON.stringify(receipt, null, 2)}\n`, 'utf8');
  return { receiptPath, receipt };
}

export function verifyPublishedFoundationProbeImage(
  imageReference,
  { repoRoot = root, run = runProcess, readReceiptPath = receiptPath,
    readFile = path => JSON.parse(readFileSync(path, 'utf8')), writeReceipt = writeFileSync } = {}) {
  if (typeof imageReference !== 'string' || !imageReference || imageReference.includes('@')) {
    throw new Error('Use a registry image reference without a caller-supplied digest.');
  }
  const receipt = readFile(readReceiptPath);
  run('docker', ['pull', imageReference], repoRoot);
  const inspected = JSON.parse(run('docker', ['image', 'inspect', imageReference], repoRoot));
  if (!Array.isArray(inspected) || inspected.length !== 1) {
    throw new Error('Docker image inspection did not return exactly one published image.');
  }
  const verified = verifyPublishedImageReceipt(receipt, imageReference, inspected[0]);
  mkdirSync(dirname(receiptPath), { recursive: true });
  writeReceipt(receiptPath, `${JSON.stringify(verified, null, 2)}\n`, 'utf8');
  return { receiptPath, receipt: verified };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    const result = process.argv[2] === '--verify-published'
      ? verifyPublishedFoundationProbeImage(process.argv[3])
      : process.argv.length === 2
        ? buildFoundationProbeImage()
        : (() => { throw new Error('Usage: build-foundation-probe-image.mjs [--verify-published <repository:tag>]'); })();
    const { receiptPath: output } = result;
    console.log(`Wrote source-bound image receipt: ${output}`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
