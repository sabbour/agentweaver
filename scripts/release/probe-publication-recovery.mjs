import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { runPublicationCommand } from './command.mjs';
import { diagnosticPins as pins, verifyFrozenProbeArtifact, verifyFrozenLoadedImage } from './probe-publication-diagnostic.mjs';

export const recoveryClaimNamespace = 'agentweaver-publication/foundation-probe-0.0.0-sampler-recovery-attempt-1';
const helperNamespace = sha => `agentweaver-publication/foundation-probe-0.0.0-sampler-recovery-helper/${sha}`;
const repository = 'ghcr.io/sabbour/agentweaver-foundation-probe';
const target = `${repository}:0.0.0`;
const localImage = 'agentweaver-foundation-probe:0.0.0';
const initialDigest = 'sha256:452be7e284ee6c33814fcedcf1d7c98f98384d09ea7239ad851f9cb316727c9a';
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const safeCodes = new Set([
  'PROCESS_TIMEOUT', 'COMMAND_NOT_FOUND', 'REGISTRY_WRITE_DENIED', 'AUTHORIZATION_DENIED', 'TRANSPORT_FAILED', 'OPERATION_FAILED',
  'ARCHIVE_MEMBER_READ_FAILED', 'FROZEN_ARTIFACT_MISMATCH', 'ARCHIVE_MANIFEST_INVALID', 'CONFIG_DIGEST_MISMATCH',
  'IMAGE_SOURCE_BINDING_INVALID', 'DLL_DIGEST_MISMATCH', 'LOADED_IMAGE_BINDING_INVALID',
  'RECOVERY_SCOPE_INVALID', 'HELPER_SOURCE_MISMATCH', 'RECEIPT_ALREADY_EXISTS', 'RECOVERY_ALREADY_CLAIMED',
  'MANIFEST_INVALID', 'CURRENT_MANIFEST_MISMATCH', 'INITIAL_INDEX_MISMATCH', 'TAG_RESPONSE_MISMATCH', 'REF_RESPONSE_MISMATCH',
  'PUBLISHED_DIGEST_MISSING', 'PUBLISHED_MANIFEST_MISMATCH', 'RETAINED_MANIFEST_MISMATCH', 'CREDENTIAL_CLEANUP_FAILED',
]);
const reject = code => { throw Object.assign(new Error(`Probe recovery refused: ${code}`), { code }); };
const safeFailure = (error, operation) => ({
  operation,
  exitCode: Number.isInteger(error?.exitCode) ? error.exitCode : null,
  code: safeCodes.has(error?.code) ? error.code : 'RECOVERY_VALIDATION_FAILED',
});

export function publishFrozenProbeRecovery(directory, {
  env = process.env, spawn = spawnSync, readFile = readFileSync, digest = hash, writeReceipt = writeFileSync,
} = {}) {
  const receipt = {
    schemaVersion: 1, status: 'partial', helperSourceSha: null, imageSourceSha: pins.sourceSha,
    originalRunId: pins.runId, originalSourceTree: pins.sourceTree, infrastructureSourceHash: pins.sourceHash,
    archiveSha256: pins.archiveSha256, provenanceSha256: pins.provenanceSha256, configSha256: pins.configSha256,
    dllSha256: pins.dllSha256, target, expectedCurrentDigest: pins.currentDigest,
    recoveryNamespace: recoveryClaimNamespace, claims: [], operations: [], published: [],
    archiveRebuilt: false, credentialDirectoryRemoved: true, originalClaimsModified: false,
  };
  let authDirectory;
  let receiptPath;
  let attempted = false;
  let failure;
  let stage = 'recovery.preflight';
  const execute = (operation, bin, args, input) => {
    stage = operation;
    const childEnv = { ...env };
    delete childEnv.GITHUB_TOKEN;
    delete childEnv.RELEASE_REGISTRY_PASSWORD;
    if (bin !== 'gh') delete childEnv.GH_TOKEN;
    if (authDirectory) childEnv.DOCKER_CONFIG = authDirectory;
    try {
      const output = runPublicationCommand(bin, args, input, (command, argv, options) => spawn(command, argv, {
        ...options, env: childEnv, timeout: 120000, maxBuffer: 32 * 1024 * 1024, windowsHide: true,
      }));
      receipt.operations.push({ operation, exitCode: 0, code: 'OK' });
      return output;
    } catch (error) {
      receipt.operations.push(safeFailure(error, operation));
      throw error;
    }
  };
  const api = (operation, method, endpoint, body) => {
    const args = ['api', '--method', method, `repos/sabbour/agentweaver/${endpoint}`];
    if (body) args.push('--input', '-');
    return JSON.parse(execute(operation, 'gh', args, body ? JSON.stringify(body) : undefined));
  };
  const descriptor = reference => {
    const raw = execute('docker.manifest-read', 'docker', ['buildx', 'imagetools', 'inspect', reference, '--raw']);
    const manifest = JSON.parse(raw);
    if (manifest.schemaVersion !== 2) reject('MANIFEST_INVALID');
    return { schemaVersion: manifest.schemaVersion, mediaType: manifest.mediaType, digest: `sha256:${digest(raw)}`,
      ...(manifest.manifests === undefined ? {} : {
        manifests: Array.isArray(manifest.manifests) ? manifest.manifests.map(item => ({ digest: item?.digest })) : null,
      }) };
  };
  const record = (namespace, name, value) => {
    const tag = `${namespace}/${name}`;
    const message = JSON.stringify(value);
    const object = api(`github.${name}-create`, 'POST', 'git/tags',
      { tag, message, object: receipt.helperSourceSha, type: 'commit' });
    if (!/^[a-f0-9]{40}$/.test(object?.sha ?? '') || object.tag !== tag || object.message !== message ||
        object.object?.sha !== receipt.helperSourceSha || object.object?.type !== 'commit') reject('TAG_RESPONSE_MISMATCH');
    const ref = `refs/tags/${tag}`;
    const created = api(`github.${name}-create`, 'POST', 'git/refs', { ref, sha: object.sha });
    if (created?.ref !== ref || created.object?.type !== 'tag' || created.object?.sha !== object.sha)
      reject('REF_RESPONSE_MISMATCH');
    return object.sha;
  };
  try {
    if (env.GITHUB_REPOSITORY !== 'sabbour/agentweaver' || env.GITHUB_REF !== 'refs/heads/v1' ||
        env.GITHUB_EVENT_NAME !== 'workflow_dispatch' || env.GITHUB_RUN_ATTEMPT !== '1' ||
        !/^[1-9][0-9]*$/.test(env.GITHUB_RUN_ID ?? '') || env.PUBLISH !== 'true' ||
        env.PROBE_SAMPLER_REPLACEMENT !== 'true' || env.EXPECTED_PROBE_DIGEST !== pins.currentDigest ||
        !/^[a-f0-9]{40}$/.test(env.EXPECTED_SOURCE ?? '') ||
        env.RELEASE_REGISTRY !== 'ghcr.io/sabbour' || env.RELEASE_REGISTRY_USER !== 'sabbour' ||
        !env.GH_TOKEN || env.GH_TOKEN !== env.RELEASE_REGISTRY_PASSWORD) reject('RECOVERY_SCOPE_INVALID');
    const helper = execute('git.source-read', 'git', ['rev-parse', 'HEAD']);
    if (helper === pins.sourceSha || helper !== env.EXPECTED_SOURCE ||
        execute('git.source-read', 'git', ['rev-parse', 'refs/remotes/origin/v1']) !== helper ||
        execute('git.source-read', 'git', ['status', '--porcelain'])) reject('HELPER_SOURCE_MISMATCH');
    receipt.helperSourceSha = helper;
    receipt.runId = env.GITHUB_RUN_ID;
    const output = path.resolve(directory, 'publication.json');
    if (existsSync(output)) reject('RECEIPT_ALREADY_EXISTS');
    stage = 'recovery.artifact-verify';
    const { archive, config } = verifyFrozenProbeArtifact(directory, { env, spawn, readFile, digest });
    receipt.frozenArtifactVerified = true;
    for (const namespace of [recoveryClaimNamespace, helperNamespace(helper)]) {
      const prior = api('github.claim-read', 'GET', `git/matching-refs/tags/${namespace}/`);
      if (!Array.isArray(prior) || prior.length !== 0) reject('RECOVERY_ALREADY_CLAIMED');
      receipt.claims.push({ namespace, attempted: false, acknowledged: false });
    }
    const previous = descriptor(target);
    if (previous.digest !== pins.currentDigest ||
        !['application/vnd.oci.image.manifest.v1+json', 'application/vnd.docker.distribution.manifest.v2+json'].includes(previous.mediaType) ||
        previous.manifests !== undefined) reject('CURRENT_MANIFEST_MISMATCH');
    const initial = descriptor(`${repository}@${initialDigest}`);
    if (initial.digest !== initialDigest || initial.mediaType !== 'application/vnd.oci.image.index.v1+json' ||
        !Array.isArray(initial.manifests) || initial.manifests.length === 0 ||
        initial.manifests.some(item => !/^sha256:[a-f0-9]{64}$/.test(item?.digest ?? ''))) reject('INITIAL_INDEX_MISMATCH');
    receipt.previousManifest = previous;
    receipt.preservedInitialIndex = initial;
    receiptPath = output;
    stage = 'recovery.credential-create';
    authDirectory = mkdtempSync(path.join(tmpdir(), 'agentweaver-probe-recovery-'));
    receipt.credentialDirectoryRemoved = false;
    for (const claim of receipt.claims) {
      attempted = true;
      claim.attempted = true;
      claim.sha = record(claim.namespace, 'claim', { ...receipt, status: 'claimed' });
      claim.acknowledged = true;
    }
    execute('docker.login', 'docker', ['login', 'ghcr.io', '--username', 'sabbour', '--password-stdin'], env.RELEASE_REGISTRY_PASSWORD);
    execute('docker.load', 'docker', ['load', '--input', archive]);
    const loaded = JSON.parse(execute('docker.inspect', 'docker', ['inspect', '--format', '{{json .}}', localImage]));
    verifyFrozenLoadedImage(loaded, config);
    receipt.loadedConfigDigest = loaded.Id;
    execute('docker.tag', 'docker', ['tag', localImage, target]);
    if (descriptor(target).digest !== pins.currentDigest) reject('CURRENT_MANIFEST_MISMATCH');
    receipt.pushAttempted = true;
    execute('docker.push', 'docker', ['push', target]);
    receipt.pushAcknowledged = true;
    const digests = JSON.parse(execute('docker.inspect', 'docker', ['inspect', '--format', '{{json .RepoDigests}}', target]));
    const published = Array.isArray(digests) && digests.find(value =>
      typeof value === 'string' && value.startsWith(`${repository}@sha256:`) && /@sha256:[a-f0-9]{64}$/.test(value));
    if (!published) reject('PUBLISHED_DIGEST_MISSING');
    receipt.newDigest = published.slice(published.lastIndexOf('@') + 1);
    receipt.published.push({ id: 'Agentweaver.FoundationProbe', version: '0.0.0', image: published,
      archiveSha256: pins.archiveSha256, imageSourceSha: pins.sourceSha });
    const current = descriptor(target);
    if (current.digest === pins.currentDigest || current.digest !== receipt.newDigest) reject('PUBLISHED_MANIFEST_MISMATCH');
    for (const retained of [previous.digest, initialDigest, ...initial.manifests.map(item => item.digest)]) {
      if (descriptor(`${repository}@${retained}`).digest !== retained) reject('RETAINED_MANIFEST_MISMATCH');
    }
    receipt.originalIndexAndPlatformsRetained = true;
    receipt.status = 'published';
  } catch (error) {
    failure = safeFailure(error, stage);
    receipt.failure = failure;
  } finally {
    if (authDirectory) {
      try {
        rmSync(authDirectory, { recursive: true, force: true });
        receipt.credentialDirectoryRemoved = !existsSync(authDirectory);
        if (!receipt.credentialDirectoryRemoved) reject('CREDENTIAL_CLEANUP_FAILED');
      } catch {
        receipt.cleanupFailure = { operation: 'recovery.credential-cleanup', exitCode: null, code: 'CREDENTIAL_CLEANUP_FAILED' };
        failure ??= receipt.cleanupFailure;
        receipt.status = 'partial';
      }
    }
    if (attempted) {
      for (const claim of receipt.claims.filter(item => item.acknowledged)) {
        try {
          claim.resultSha = record(claim.namespace, 'result', receipt);
        } catch (error) {
          const issue = safeFailure(error, 'github.result-create');
          (receipt.persistenceFailures ??= []).push(issue);
          failure ??= issue;
          receipt.status = 'partial';
        }
      }
    }
    if (receiptPath) {
      try {
        writeReceipt(receiptPath, JSON.stringify(receipt, null, 2) + '\n', { flag: 'wx' });
      } catch {
        const issue = { operation: 'recovery.receipt-write', exitCode: null, code: 'RECEIPT_PERSISTENCE_FAILED' };
        (receipt.persistenceFailures ??= []).push(issue);
        failure ??= issue;
        receipt.status = 'partial';
      }
    }
  }
  if (failure) throw Object.assign(new Error(`Probe recovery stopped: ${failure.code}`), { recovery: receipt });
  return receipt;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    console.log(JSON.stringify(publishFrozenProbeRecovery(process.argv[2])));
  } catch (error) {
    console.error(JSON.stringify(error.recovery ?? { status: 'partial', failure: { code: 'RECOVERY_START_FAILED' } }));
    process.exitCode = 1;
  }
}
