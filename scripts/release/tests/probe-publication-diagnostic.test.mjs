import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { runPublicationCommand } from '../command.mjs';
import { diagnosticPins as pins, runProbePublicationDiagnostic } from '../probe-publication-diagnostic.mjs';

function fixture() {
  const helperSha = 'a'.repeat(40);
  const env = {
    GITHUB_REPOSITORY: 'sabbour/agentweaver', GITHUB_REF: 'refs/heads/v1', GITHUB_EVENT_NAME: 'workflow_dispatch',
    PUBLISH: 'false', PROBE_SAMPLER_REPLACEMENT: 'true', EXPECTED_PROBE_DIGEST: pins.currentDigest,
    EXPECTED_SOURCE: helperSha, RELEASE_REGISTRY_USER: 'sabbour', RELEASE_REGISTRY_PASSWORD: 'synthetic-secret',
    GH_TOKEN: 'synthetic-github-secret', GITHUB_TOKEN: 'synthetic-actions-secret',
  };
  const config = { os: 'linux', architecture: 'amd64', config: {
    User: '10001:10001', Entrypoint: ['dotnet', 'Agentweaver.FoundationProbe.dll'], Labels: {
      'org.opencontainers.image.revision': pins.sourceSha, 'org.opencontainers.image.version': '0.0.0',
      'io.agentweaver.source-tree': pins.sourceTree, 'io.agentweaver.infrastructure-source-hash': pins.sourceHash,
    },
  } };
  const provenance = Buffer.from(JSON.stringify({ sourceSha: pins.sourceSha, artifacts: [{
    path: 'Agentweaver.FoundationProbe.0.0.0.tar.gz', componentId: 'Agentweaver.FoundationProbe',
    kind: 'image', sha256: pins.archiveSha256,
  }] }));
  const archive = Buffer.from('synthetic frozen archive');
  const configBytes = Buffer.from(JSON.stringify(config));
  const dll = Buffer.from('synthetic frozen DLL');
  const raw = '{"schemaVersion":2}';
  const calls = [];
  const spawn = (bin, args, options) => {
    calls.push({ bin, args, options });
    assert.equal(options.env.GH_TOKEN, undefined);
    assert.equal(options.env.GITHUB_TOKEN, undefined);
    assert.equal(options.env.RELEASE_REGISTRY_PASSWORD, undefined);
    if (bin === 'git') return { status: 0, stdout: args[0] === 'status' ? '' : helperSha, stderr: '' };
    if (bin === 'tar') return { status: 0, stdout:
      args.at(-1) === 'manifest.json' ? Buffer.from(JSON.stringify([{
        Config: `${pins.configSha256}.json`, Layers: [`${'b'.repeat(64)}/layer.tar`],
        RepoTags: ['agentweaver-foundation-probe:0.0.0'],
      }])) : args.at(-1).endsWith('.json') ? configBytes
        : args.at(-1).endsWith('.dll') ? dll : Buffer.from('synthetic layer'), stderr: Buffer.alloc(0) };
    assert.equal(bin, 'docker');
    assert.ok(['login', 'load', 'inspect', 'buildx'].includes(args[0]));
    if (args[0] === 'login') assert.equal(options.input, env.RELEASE_REGISTRY_PASSWORD);
    return { status: 0, stdout: args[0] === 'inspect' ? JSON.stringify({
      Id: `sha256:${pins.configSha256}`, Os: 'linux', Architecture: 'amd64', Config: config.config,
    }) : args[0] === 'buildx' ? raw : 'private native output', stderr: '' };
  };
  return {
    env, calls, spawn, config, configBytes, dll,
    readFile: file => path.basename(file) === 'provenance.json' ? provenance : archive,
    digest: bytes => Buffer.from(bytes).equals(provenance) ? pins.provenanceSha256
      : Buffer.from(bytes).equals(archive) ? pins.archiveSha256
      : Buffer.from(bytes).equals(configBytes) ? pins.configSha256
      : Buffer.from(bytes).equals(dll) ? pins.dllSha256
      : Buffer.from(bytes).toString() === raw ? pins.currentDigest.slice(7) : 'c'.repeat(64),
  };
}

test('diagnostic verifies the frozen image and performs only four read-only Docker operations', () => {
  const f = fixture();
  const result = runProbePublicationDiagnostic('unused', f);
  assert.equal(result.status, 'completed-read-only');
  assert.equal(result.originalImageSourceSha, pins.sourceSha);
  assert.notEqual(result.helperSourceSha, pins.sourceSha);
  assert.equal(result.frozenArtifactVerified, true);
  assert.equal(result.currentDigest, pins.currentDigest);
  assert.deepEqual(result.operations.map(item => item.operation),
    ['docker.login', 'docker.load', 'docker.inspect', 'docker.manifest-read']);
  for (const field of ['archiveRebuilt', 'registryWrites', 'claimsWritten', 'packageWritePermissionProven']) assert.equal(result[field], false);
  assert.equal(result.credentialDirectoryRemoved, true);
  const directory = f.calls.find(call => call.bin === 'docker').options.env.DOCKER_CONFIG;
  assert.equal(existsSync(directory), false);
  assert.doesNotMatch(JSON.stringify(result), /synthetic-secret|private native output|synthetic-actions-secret/);
});

for (const [name, change] of [
  ['wrong repository', { GITHUB_REPOSITORY: 'other/repo' }],
  ['publication intent', { PUBLISH: 'true' }],
  ['wrong branch', { GITHUB_REF: 'refs/heads/dev' }],
  ['wrong target', { EXPECTED_PROBE_DIGEST: `sha256:${'e'.repeat(64)}` }],
  ['wrong helper source', { EXPECTED_SOURCE: 'e'.repeat(40) }],
]) {
  test(`diagnostic refuses ${name} before native registry operations`, () => {
    const f = fixture();
    assert.throws(() => runProbePublicationDiagnostic('unused', { ...f, env: { ...f.env, ...change } }),
      error => error.diagnostic.status === 'failed' && !error.diagnostic.registryWrites && !error.diagnostic.claimsWritten);
    assert.ok(!f.calls.some(call => call.bin === 'docker'));
  });
}

test('diagnostic refuses a substituted archive or malformed provenance before login', () => {
  for (const change of [
    { digest: () => 'e'.repeat(64) },
    { readFile: () => Buffer.from('malformed synthetic-secret JSON') },
  ]) {
    const f = fixture();
    assert.throws(() => runProbePublicationDiagnostic('unused', { ...f, ...change }),
      error => !JSON.stringify(error.diagnostic).includes('synthetic-secret'));
    assert.ok(!f.calls.some(call => call.bin === 'docker'));
  }
});

test('diagnostic checks the exact DLL hash before login and refuses a dirty helper source', () => {
  const f = fixture();
  assert.throws(() => runProbePublicationDiagnostic('unused', { ...f,
    digest: bytes => Buffer.from(bytes).equals(f.dll) ? 'e'.repeat(64) : f.digest(bytes),
  }), error => error.diagnostic.failureCode === 'DLL_DIGEST_MISMATCH');
  assert.ok(!f.calls.some(call => call.bin === 'docker'));
  assert.throws(() => runProbePublicationDiagnostic('unused', { ...f, spawn(bin, args, options) {
    return bin === 'git' && args[0] === 'status' ? { status: 0, stdout: ' M scripts/changed.mjs', stderr: '' }
      : f.spawn(bin, args, options);
  } }), error => error.diagnostic.failureCode === 'HELPER_SOURCE_MISMATCH');
  assert.ok(!f.calls.some(call => call.bin === 'docker'));
});

test('diagnostic refuses a loaded-image binding mismatch or current digest drift without writes', () => {
  for (const command of ['inspect', 'buildx']) {
    const f = fixture();
    assert.throws(() => runProbePublicationDiagnostic('unused', { ...f, spawn(bin, args, options) {
      if (bin === 'docker' && args[0] === command) return { status: 0, stderr: '', stdout:
        command === 'inspect' ? JSON.stringify({
          Id: `sha256:${pins.configSha256}`, Os: 'linux', Architecture: 'amd64', Config: { ...f.config.config, User: '0:0' },
        }) : '{"schemaVersion":2,"drift":true}' };
      return f.spawn(bin, args, options);
    } }), error => {
      assert.equal(error.diagnostic.failureCode, command === 'inspect' ? 'LOADED_IMAGE_BINDING_INVALID' : 'CURRENT_MANIFEST_MISMATCH');
      assert.equal(error.diagnostic.registryWrites, false);
      assert.equal(error.diagnostic.claimsWritten, false);
      assert.equal(error.diagnostic.credentialDirectoryRemoved, true);
      return true;
    });
  }
});

test('unexpected native exceptions cannot inject private error fields into diagnostic output', () => {
  const f = fixture();
  assert.throws(() => runProbePublicationDiagnostic('unused', { ...f, spawn(bin, args, options) {
    if (bin === 'docker') throw Object.assign(new Error('synthetic-secret'), {
      code: 'UNTRUSTED_SECRET', exitCode: 'synthetic-secret',
    });
    return f.spawn(bin, args, options);
  } }), error => {
    assert.equal(error.diagnostic.failureCode, 'DIAGNOSTIC_VALIDATION_FAILED');
    assert.doesNotMatch(JSON.stringify(error.diagnostic), /synthetic-secret|UNTRUSTED_SECRET/);
    assert.equal(error.diagnostic.operations[0].exitCode, null);
    assert.equal(error.diagnostic.credentialDirectoryRemoved, true);
    return true;
  });
});

for (const [name, stderr, error, code] of [
  ['denied', 'unauthorized synthetic-secret', undefined, 'AUTHORIZATION_DENIED'],
  ['network failure', 'connection refused https://credential:synthetic-secret@ghcr.io', undefined, 'TRANSPORT_FAILED'],
  ['timeout', '', { code: 'ETIMEDOUT', message: 'synthetic-secret' }, 'PROCESS_TIMEOUT'],
]) {
  test(`diagnostic records ${name}, stops without writes and removes its owned credential directory`, () => {
    const f = fixture();
    let directory;
    assert.throws(() => runProbePublicationDiagnostic('unused', { ...f, spawn(bin, args, options) {
      if (bin === 'docker') {
        directory = options.env.DOCKER_CONFIG;
        return { status: error ? null : 1, stdout: 'synthetic-secret', stderr, error };
      }
      return f.spawn(bin, args, options);
    } }), failure => {
      assert.equal(failure.diagnostic.failureCode, code);
      assert.deepEqual(failure.diagnostic.operations, [{ operation: 'docker.login', exitCode: error ? null : 1, code }]);
      assert.equal(failure.diagnostic.credentialDirectoryRemoved, true);
      assert.doesNotMatch(JSON.stringify(failure.diagnostic), /synthetic-secret|credential:/);
      return true;
    });
    assert.equal(existsSync(directory), false);
  });
}

test('publication command reports an allowlisted stage and code without native output, arguments or credentials', () => {
  assert.throws(() => runPublicationCommand('docker', ['push', 'synthetic-secret'], 'stdin-secret', () => ({
    status: 1, stdout: 'stdout-secret', stderr: 'permission_denied: write_package token=stderr-secret',
  })), error => {
    assert.equal(error.operation, 'docker.push');
    assert.equal(error.exitCode, 1);
    assert.equal(error.code, 'REGISTRY_WRITE_DENIED');
    assert.doesNotMatch(error.message, /synthetic-secret|stdin-secret|stdout-secret|stderr-secret/);
    return true;
  });
  assert.equal(runPublicationCommand('docker', ['buildx', 'imagetools', 'inspect', 'unused', '--raw'], undefined,
    () => ({ status: 0, stdout: 'raw bytes\n', stderr: '' })), 'raw bytes\n');
});

test('workflow diagnostic is isolated, read-only and pinned to the original artifact without a build or publisher', () => {
  const workflow = readFileSync('.github/workflows/v1-release-pack.yml', 'utf8').replaceAll('\r\n', '\n');
  const diagnostic = workflow.slice(workflow.indexOf('\n  diagnose-probe-sampler:'), workflow.indexOf('\n  publish:'));
  assert.match(workflow, /pack:\n\s+if: \$\{\{ inputs\.publish \|\| !inputs\.foundation_probe_sampler_replacement \}\}/);
  assert.match(diagnostic, /!inputs\.publish && inputs\.foundation_probe_sampler_replacement/);
  for (const permission of ['contents', 'actions', 'packages']) assert.match(diagnostic, new RegExp(`${permission}: read`));
  assert.match(diagnostic, /github-token: \$\{\{ github.token \}\}/);
  assert.match(diagnostic, /run-id: 37409181340/);
  assert.match(diagnostic, /name: v1-release-pack-f46cff3c65a97b76b78c9ea717ec8b2068d56de5-37409181340/);
  assert.doesNotMatch(diagnostic, /: write|environment:|publish\.mjs|release:pack|setup-dotnet|upload-artifact|secrets\.|--confirm-publication/);
});
