import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { runPublicationCommand } from './command.mjs';

export const diagnosticPins = Object.freeze({
  runId: 37409181340,
  sourceSha: 'f46cff3c65a97b76b78c9ea717ec8b2068d56de5',
  sourceTree: 'bf79cd6fbd1353c29571ce35d9b26ea7daa2d117',
  sourceHash: '7f011351c9cd8404c97a458994f47199b7cd8f24b1cde3f36153d7faa7e2337a',
  archiveSha256: 'd3ba283cde389523e2ccbe581c3a99ccb3e1e2c12ce971b6e7f3e5c0c55b93db',
  provenanceSha256: 'b1056b9977b10b1f01aeb4a971136984c66a662b6b43b7fea081095fe0e94082',
  configSha256: '1d74252ae41f2a1dd040fb2b0811c4399e48a3121f49519d81690e7c82968392',
  dllSha256: 'f4e7222267f96b9c640e4b83ef1d8881f79c679a9bb12442f59d35189d1fed39',
  currentDigest: 'sha256:835d5b8899f2a8956faf24d46a934ec745d91ff83363d77f22f2859c2f743969',
});

const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const target = 'ghcr.io/sabbour/agentweaver-foundation-probe:0.0.0';
const localImage = 'agentweaver-foundation-probe:0.0.0';
const safeCodes = new Set([
  'PROCESS_TIMEOUT', 'COMMAND_NOT_FOUND', 'REGISTRY_WRITE_DENIED', 'AUTHORIZATION_DENIED', 'TRANSPORT_FAILED', 'OPERATION_FAILED',
  'ARCHIVE_MEMBER_READ_FAILED', 'DIAGNOSTIC_SCOPE_INVALID', 'HELPER_SOURCE_MISMATCH', 'FROZEN_ARTIFACT_MISMATCH',
  'ARCHIVE_MANIFEST_INVALID', 'CONFIG_DIGEST_MISMATCH', 'IMAGE_SOURCE_BINDING_INVALID', 'DLL_DIGEST_MISMATCH',
  'LOADED_IMAGE_BINDING_INVALID', 'CURRENT_MANIFEST_MISMATCH',
]);
const reject = code => { throw Object.assign(new Error(`Probe diagnostic refused: ${code}`), { code }); };

export function verifyFrozenProbeArtifact(directory, {
  env = process.env, spawn = spawnSync, readFile = readFileSync, digest = hash,
} = {}) {
  const childEnv = { ...env };
  for (const key of ['GH_TOKEN', 'GITHUB_TOKEN', 'RELEASE_REGISTRY_PASSWORD']) delete childEnv[key];
  const member = (archive, name, input) => {
    const response = spawn('tar', ['-xOf', archive, name], {
      input, env: childEnv, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true,
      timeout: 120000, maxBuffer: 64 * 1024 * 1024,
    });
    if (response.error || response.status !== 0) reject('ARCHIVE_MEMBER_READ_FAILED');
    return response.stdout;
  };
  const archive = path.resolve(directory, 'Agentweaver.FoundationProbe.0.0.0.tar.gz');
  const provenanceBytes = readFile(path.resolve(directory, 'provenance.json'));
  const provenance = JSON.parse(provenanceBytes);
  if (digest(readFile(archive)) !== diagnosticPins.archiveSha256 ||
      digest(provenanceBytes) !== diagnosticPins.provenanceSha256 ||
      provenance.sourceSha !== diagnosticPins.sourceSha || provenance.artifacts?.length !== 1 ||
      provenance.artifacts[0].path !== 'Agentweaver.FoundationProbe.0.0.0.tar.gz' ||
      provenance.artifacts[0].sha256 !== diagnosticPins.archiveSha256 ||
      provenance.artifacts[0].componentId !== 'Agentweaver.FoundationProbe' ||
      provenance.artifacts[0].kind !== 'image') reject('FROZEN_ARTIFACT_MISMATCH');
  const manifests = JSON.parse(member(archive, 'manifest.json'));
  if (!Array.isArray(manifests) || manifests.length !== 1 ||
      manifests[0].Config !== `${diagnosticPins.configSha256}.json` ||
      JSON.stringify(manifests[0].RepoTags) !== JSON.stringify([localImage]) ||
      !Array.isArray(manifests[0].Layers) || manifests[0].Layers.length === 0 ||
      manifests[0].Layers.some(layer => !/^[a-f0-9]{64}\/layer\.tar$/.test(layer))) reject('ARCHIVE_MANIFEST_INVALID');
  const configBytes = member(archive, manifests[0].Config);
  if (digest(configBytes) !== diagnosticPins.configSha256) reject('CONFIG_DIGEST_MISMATCH');
  const config = JSON.parse(configBytes);
  const labels = config?.config?.Labels;
  if (config?.os !== 'linux' || config.architecture !== 'amd64' || config.config?.User !== '10001:10001' ||
      JSON.stringify(config.config.Entrypoint) !== JSON.stringify(['dotnet', 'Agentweaver.FoundationProbe.dll']) ||
      labels?.['org.opencontainers.image.revision'] !== diagnosticPins.sourceSha ||
      labels?.['io.agentweaver.source-tree'] !== diagnosticPins.sourceTree ||
      labels?.['io.agentweaver.infrastructure-source-hash'] !== diagnosticPins.sourceHash ||
      labels?.['org.opencontainers.image.version'] !== '0.0.0') reject('IMAGE_SOURCE_BINDING_INVALID');
  let layer = member(archive, manifests[0].Layers.at(-1));
  if (layer[0] === 0x1f && layer[1] === 0x8b) layer = gunzipSync(layer, { maxOutputLength: 64 * 1024 * 1024 });
  if (digest(member('-', 'app/Agentweaver.FoundationProbe.dll', layer)) !== diagnosticPins.dllSha256)
    reject('DLL_DIGEST_MISMATCH');
  return { archive, config };
}

export function verifyFrozenLoadedImage(inspected, config) {
  const actualConfig = inspected?.Config;
  if (inspected?.Id !== `sha256:${diagnosticPins.configSha256}` || inspected.Os !== 'linux' ||
      inspected.Architecture !== 'amd64' ||
      JSON.stringify(actualConfig?.Entrypoint) !== JSON.stringify(config.config.Entrypoint) ||
      actualConfig?.User !== config.config.User || actualConfig.Labels?.['org.opencontainers.image.revision'] !== diagnosticPins.sourceSha ||
      actualConfig.Labels?.['io.agentweaver.source-tree'] !== diagnosticPins.sourceTree ||
      actualConfig.Labels?.['io.agentweaver.infrastructure-source-hash'] !== diagnosticPins.sourceHash ||
      actualConfig.Labels?.['org.opencontainers.image.version'] !== '0.0.0') reject('LOADED_IMAGE_BINDING_INVALID');
}

export function runProbePublicationDiagnostic(directory, {
  env = process.env, spawn = spawnSync, readFile = readFileSync, digest = hash,
} = {}) {
  const result = {
    schemaVersion: 1, status: 'failed', originalRunId: diagnosticPins.runId,
    originalImageSourceSha: diagnosticPins.sourceSha, helperSourceSha: null,
    operations: [], archiveRebuilt: false, registryWrites: false, claimsWritten: false,
    packageWritePermissionProven: false, credentialDirectoryRemoved: true,
  };
  let authDirectory;
  const childEnv = { ...env };
  for (const key of ['GH_TOKEN', 'GITHUB_TOKEN', 'RELEASE_REGISTRY_PASSWORD']) delete childEnv[key];
  const execute = (bin, args, input) => runPublicationCommand(bin, args, input,
    (command, argv, options) => spawn(command, argv, {
      ...options, env: childEnv, timeout: 120000, maxBuffer: 32 * 1024 * 1024, windowsHide: true,
    }));
  const operation = (name, args, input) => {
    try {
      const output = execute('docker', args, input);
      result.operations.push({ operation: name, exitCode: 0, code: 'OK' });
      return output;
    } catch (error) {
      result.operations.push({ operation: name, exitCode: Number.isInteger(error.exitCode) ? error.exitCode : null,
        code: safeCodes.has(error.code) ? error.code : 'OPERATION_FAILED' });
      throw error;
    }
  };
  try {
    if (env.GITHUB_REPOSITORY !== 'sabbour/agentweaver' || env.GITHUB_REF !== 'refs/heads/v1' ||
        env.GITHUB_EVENT_NAME !== 'workflow_dispatch' || env.PUBLISH !== 'false' ||
        env.PROBE_SAMPLER_REPLACEMENT !== 'true' || env.EXPECTED_PROBE_DIGEST !== diagnosticPins.currentDigest ||
        !/^[a-f0-9]{40}$/.test(env.EXPECTED_SOURCE ?? '') ||
        env.RELEASE_REGISTRY_USER !== 'sabbour' || !env.RELEASE_REGISTRY_PASSWORD) reject('DIAGNOSTIC_SCOPE_INVALID');
    const helperSource = execute('git', ['rev-parse', 'HEAD']);
    if (helperSource !== env.EXPECTED_SOURCE || execute('git', ['status', '--porcelain'])) reject('HELPER_SOURCE_MISMATCH');
    result.helperSourceSha = helperSource;
    const { archive, config } = verifyFrozenProbeArtifact(directory, { env, spawn, readFile, digest });
    result.frozenArtifactVerified = true;
    authDirectory = mkdtempSync(path.join(tmpdir(), 'agentweaver-probe-diagnostic-'));
    result.credentialDirectoryRemoved = false;
    childEnv.DOCKER_CONFIG = authDirectory;
    operation('docker.login', ['login', 'ghcr.io', '--username', 'sabbour', '--password-stdin'], env.RELEASE_REGISTRY_PASSWORD);
    operation('docker.load', ['load', '--input', archive]);
    const inspected = JSON.parse(operation('docker.inspect', ['inspect', '--format', '{{json .}}', localImage]));
    verifyFrozenLoadedImage(inspected, config);
    result.loadedConfigDigest = inspected.Id;
    const raw = operation('docker.manifest-read', ['buildx', 'imagetools', 'inspect', target, '--raw']);
    result.currentDigest = `sha256:${digest(raw)}`;
    if (result.currentDigest !== diagnosticPins.currentDigest || JSON.parse(raw).schemaVersion !== 2) reject('CURRENT_MANIFEST_MISMATCH');
    result.status = 'completed-read-only';
    return result;
  } catch (error) {
    result.failureCode = safeCodes.has(error.code) ? error.code : 'DIAGNOSTIC_VALIDATION_FAILED';
    throw Object.assign(new Error(`Probe diagnostic failed: ${result.failureCode}`), { diagnostic: result });
  } finally {
    if (authDirectory) {
      try {
        rmSync(authDirectory, { recursive: true, force: true });
        result.credentialDirectoryRemoved = !existsSync(authDirectory);
        if (!result.credentialDirectoryRemoved) throw new Error('Owned credential directory remains');
      } catch {
        result.status = 'failed';
        result.failureCode = 'CREDENTIAL_CLEANUP_FAILED';
        throw Object.assign(new Error('Probe diagnostic failed: CREDENTIAL_CLEANUP_FAILED'), { diagnostic: result });
      }
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    console.log(JSON.stringify(runProbePublicationDiagnostic(process.argv[2])));
  } catch (error) {
    console.error(JSON.stringify(error.diagnostic ?? { status: 'failed', failureCode: 'DIAGNOSTIC_START_FAILED' }));
    process.exitCode = 1;
  }
}
