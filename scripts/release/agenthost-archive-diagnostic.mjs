import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { agentHostArchiveMetadata } from './agenthost-image.mjs';
import { runPublicationCommand } from './command.mjs';

export const archivePins = Object.freeze({
  runId: 38286624230,
  artifactId: 11763641424,
  archive: 'Agentweaver.AgentHost.1.0.0.tar',
  archiveSha256: 'd1af964c658f4d02f0d7aca009f3d74c6f128fdec791d635f202c3739c04b054',
  imageSourceSha: '6b036e11ff211f96cfddcd837be9b5bce75ddb34',
  sourceTree: 'a6becebfb45d1be2eb523025c610e6017e83994e',
  version: '1.0.0',
  configDigest: 'sha256:6737390f3e233187e53dec6985de12aea81e29e2d7b21ea217c2a0e181d1c6c1',
  manifestDigest: 'sha256:1cbc99febfe0cfe587e81a363534bff12fd4c1b885c7508e59d86c16e781a7dc',
  user: '1654:1654',
  entrypoint: ['dotnet', 'Agentweaver.AgentHost.dll'],
});

// Only these inspect fields are ever read. Nothing else from the inspect output is reported.
export const allowedFields = Object.freeze([
  'Id', 'Os', 'Architecture', 'Config.User', 'Config.Entrypoint',
  'Config.Labels.org.opencontainers.image.revision', 'Config.Labels.io.agentweaver.source-tree',
  'Config.Labels.org.opencontainers.image.version',
]);

const sha256 = text => createHash('sha256').update(text).digest('hex');
const canonical = value => sha256(JSON.stringify(value ?? null));
const failureCodes = new Set([
  'DIAGNOSTIC_SCOPE_INVALID', 'HELPER_SOURCE_MISMATCH', 'ARCHIVE_HASH_MISMATCH', 'ARCHIVE_UNREADABLE',
  'PROCESS_TIMEOUT', 'COMMAND_NOT_FOUND', 'OPERATION_FAILED', 'INSPECT_OUTPUT_INVALID',
  'REGISTRY_WRITE_DENIED', 'AUTHORIZATION_DENIED', 'TRANSPORT_FAILED',
]);
const reject = code => { throw Object.assign(new Error(`AgentHost archive diagnostic refused: ${code}`), { code }); };

export function describeInspect(image) {
  const labels = image?.Config?.Labels;
  const values = {
    'Id': image?.Id, 'Os': image?.Os, 'Architecture': image?.Architecture,
    'Config.User': image?.Config?.User, 'Config.Entrypoint': image?.Config?.Entrypoint,
    'Config.Labels.org.opencontainers.image.revision': labels?.['org.opencontainers.image.revision'],
    'Config.Labels.io.agentweaver.source-tree': labels?.['io.agentweaver.source-tree'],
    'Config.Labels.org.opencontainers.image.version': labels?.['org.opencontainers.image.version'],
  };
  const present = Object.fromEntries(allowedFields.map(name => [name, values[name] !== undefined]));
  const digests = Object.fromEntries(allowedFields.map(name => [name, canonical(values[name])]));
  const comparisons = {
    idEqualsConfigDigest: values.Id === archivePins.configDigest,
    idEqualsManifestDigest: values.Id === archivePins.manifestDigest,
    idIsSha256Digest: typeof values.Id === 'string' && /^sha256:[a-f0-9]{64}$/.test(values.Id),
    osIsLinux: values.Os === 'linux',
    architectureIsAmd64: values.Architecture === 'amd64',
    userMatches: values['Config.User'] === archivePins.user,
    entrypointMatches: JSON.stringify(values['Config.Entrypoint']) === JSON.stringify(archivePins.entrypoint),
    revisionMatches: values['Config.Labels.org.opencontainers.image.revision'] === archivePins.imageSourceSha,
    treeMatches: values['Config.Labels.io.agentweaver.source-tree'] === archivePins.sourceTree,
    versionMatches: values['Config.Labels.org.opencontainers.image.version'] === archivePins.version,
  };
  // The image ID is a public digest, so it is shown only when it has the digest shape.
  const imageId = comparisons.idIsSha256Digest ? values.Id : null;
  return { present, comparisons, digests, imageId };
}

export function runAgentHostArchiveDiagnostic(directory, { env = process.env, spawn = spawnSync,
  expectedArchiveSha256 = archivePins.archiveSha256 } = {}) {
  const result = {
    schemaVersion: 1, status: 'failed', originalRunId: archivePins.runId,
    originalImageSourceSha: archivePins.imageSourceSha, helperSourceSha: null,
    archiveHashVerifiedBeforeLoad: false, operations: [],
    archiveRebuilt: false, registryAccess: false, runtimeExecuted: false,
  };
  const childEnv = { ...env };
  for (const key of ['GH_TOKEN', 'GITHUB_TOKEN', 'RELEASE_REGISTRY_PASSWORD']) delete childEnv[key];
  const execute = (bin, args) => runPublicationCommand(bin, args, undefined,
    (command, argv, options) => spawn(command, argv, {
      ...options, env: childEnv, timeout: 300000, maxBuffer: 32 * 1024 * 1024, windowsHide: true,
    }));
  const operation = (name, bin, args) => {
    try {
      const output = execute(bin, args);
      result.operations.push({ operation: name, exitCode: 0, code: 'OK' });
      return output;
    } catch (error) {
      result.operations.push({ operation: name, exitCode: Number.isInteger(error.exitCode) ? error.exitCode : null,
        code: failureCodes.has(error.code) ? error.code : 'OPERATION_FAILED' });
      throw error;
    }
  };
  try {
    if (env.GITHUB_REPOSITORY !== 'sabbour/agentweaver' || env.GITHUB_REF !== 'refs/heads/v1' ||
        env.GITHUB_EVENT_NAME !== 'workflow_dispatch' || env.GITHUB_RUN_ATTEMPT !== '1' ||
        !/^[a-f0-9]{40}$/.test(env.EXPECTED_SOURCE ?? '') ||
        env.EXPECTED_SOURCE === archivePins.imageSourceSha) reject('DIAGNOSTIC_SCOPE_INVALID');
    const helperSource = operation('git.source-read', 'git', ['rev-parse', 'HEAD']);
    if (helperSource !== env.EXPECTED_SOURCE || operation('git.source-read', 'git', ['status', '--porcelain']))
      reject('HELPER_SOURCE_MISMATCH');
    result.helperSourceSha = helperSource;
    let archivePath = path.resolve(directory, archivePins.archive);
    if (!existsSync(archivePath)) {
      const nested = existsSync(directory) ? readdirSync(directory, { withFileTypes: true })
        .filter(entry => entry.isDirectory()).map(entry => path.resolve(directory, entry.name, archivePins.archive))
        .filter(candidate => existsSync(candidate)) : [];
      if (nested.length === 1) [archivePath] = nested;
    }
    let metadata;
    try {
      metadata = agentHostArchiveMetadata(archivePath, archivePins.archive);
    } catch { reject('ARCHIVE_UNREADABLE'); }
    if (metadata.sha256 !== expectedArchiveSha256) reject('ARCHIVE_HASH_MISMATCH');
    result.archiveHashVerifiedBeforeLoad = true;
    result.archiveSizeBytes = metadata.size;
    operation('docker.load', 'docker', ['load', '--input', archivePath]);
    const reference = `agentweaver-agenthost:${archivePins.version}-${archivePins.imageSourceSha}`;
    let inspected;
    const raw = operation('docker.image-inspect', 'docker', ['image', 'inspect', '--format', '{{json .}}', reference]);
    try {
      inspected = JSON.parse(raw);
    } catch { reject('INSPECT_OUTPUT_INVALID'); }
    if (!inspected || typeof inspected !== 'object' || Array.isArray(inspected) || typeof inspected.Id !== 'string' ||
        (inspected.Config !== undefined && (inspected.Config === null || typeof inspected.Config !== 'object')))
      reject('INSPECT_OUTPUT_INVALID');
    Object.assign(result, describeInspect(inspected));
    result.allExpectedFieldsMatch = Object.entries(result.comparisons)
      .every(([name, value]) => name === 'idEqualsManifestDigest' || value);
    result.status = 'completed-read-only';
    return result;
  } catch (error) {
    result.failureCode = failureCodes.has(error.code) ? error.code : 'OPERATION_FAILED';
    throw Object.assign(new Error(`AgentHost archive diagnostic failed: ${result.failureCode}`), { diagnostic: result });
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    console.log(JSON.stringify(runAgentHostArchiveDiagnostic(process.argv[2])));
  } catch (error) {
    console.error(JSON.stringify(error.diagnostic ?? { status: 'failed', failureCode: 'DIAGNOSTIC_START_FAILED' }));
    process.exitCode = 1;
  }
}
