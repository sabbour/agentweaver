import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const runtime = 'mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4';
const engine = process.env.AGENTWEAVER_CONTAINER_ENGINE ?? 'docker';
const explicitlyRequested = process.env.AGENTWEAVER_CONTAINER_ENGINE !== undefined;
const enabled = process.platform === 'linux' || explicitlyRequested || process.env.GITHUB_ACTIONS === 'true';
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

function run(command, args, { timeout = 60_000, allowFailure = false } = {}) {
  const result = spawnSync(command, args, { cwd: root, encoding: 'utf8', timeout, windowsHide: true });
  if (result.error) throw result.error;
  if (!allowFailure && result.status !== 0)
    throw new Error(`${command} failed (${result.status}): ${result.stderr.trim()}`);
  return result;
}

function request(outputs, maximumTotalBytes = 1024) {
  return {
    operationId: '5a2daa59-cda2-4594-9b4b-92d0c261b5d9',
    immutableHash: `sha256:${'a'.repeat(64)}`,
    requestFingerprint: 'b'.repeat(64),
    checkpoint: {
      projectId: 'project', runId: 'run', sessionId: 'session', checkpointId: 'checkpoint',
      workPlanId: 'plan', stepId: 'step', checkpointRevision: 1, decisionStateVersion: 1,
      executionFence: 1, acceptedSelectionHash: 'A'.repeat(64),
    },
    workspaceMountPath: '/workspace',
    maximumTotalBytes,
    outputs,
  };
}

const obligation = (name, relativePath, maximumBytes = 128) =>
  ({ name, relativePath, required: true, maximumBytes });

test('actual Linux collector reads bounded regular files and rejects unsafe output paths', {
  skip: enabled ? false : 'requires a cached Linux runtime; set AGENTWEAVER_CONTAINER_ENGINE=podman or run on Linux Docker',
}, async t => {
  assert.ok(['docker', 'podman'].includes(engine), 'select only Docker or Podman');
  run(engine, ['version']);
  const inspected = JSON.parse(run(engine, ['image', 'inspect', runtime]).stdout)[0];
  assert.equal(inspected.Os, 'linux');
  assert.ok(['amd64', 'arm64'].includes(inspected.Architecture));
  if (process.env.GITHUB_ACTIONS === 'true') assert.equal(inspected.Architecture, 'amd64');
  const platform = `linux/${inspected.Architecture}`;
  const suffix = `agentweaver-collector-${randomUUID()}`;
  const volume = `${suffix}-workspace`;
  const directory = path.join(root, 'artifacts', 'collector-tests', suffix);
  const publish = path.join(directory, 'publish');
  const proof = {
    kind: 'local-buildtest-collector', sourceSha: run('git', ['rev-parse', 'HEAD']).stdout.trim(),
    sourceTree: run('git', ['rev-parse', 'HEAD^{tree}']).stdout.trim(),
    sourceClean: run('git', ['status', '--porcelain', '--untracked-files=normal']).stdout.trim() === '',
    collectorBlob: run('git', ['hash-object', 'services/agenthost/Agentweaver.AgentHost/BuildTestOutputCollectorProgram.cs']).stdout.trim(),
    runtimeImageId: inspected.Id, platform, engine, cases: [],
    boundary: 'Managed collector only; no native SDK, model call, command Pod, Kubernetes, or deployment.',
  };
  mkdirSync(directory, { recursive: true });
  let createdVolume = false;
  const containers = new Set();
  t.after(() => {
    try {
      const remaining = run(engine, ['ps', '--all', '--format', '{{.Names}}']).stdout.trim().split(/\r?\n/);
      for (const name of remaining.filter(name => containers.has(name)))
        run(engine, ['rm', '--force', name]);
      if (createdVolume) run(engine, ['volume', 'rm', volume]);
      proof.cleanup = { volume, removed: createdVolume };
    } finally {
      writeFileSync(path.join(directory, 'receipt.json'), `${JSON.stringify(proof, null, 2)}\n`);
    }
  });
  run('dotnet', [
    'publish', 'services/agenthost/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj',
    '--configuration', 'Release', '--no-restore', '--no-self-contained',
    '-p:UseAppHost=false', '--output', publish, '--verbosity', 'quiet',
  ], { timeout: 600_000 });
  run(engine, ['volume', 'create', volume]);
  createdVolume = true;
  containers.add(`${suffix}-setup`);
  run(engine, [
    'run', '--rm', '--name', `${suffix}-setup`, '--pull=never', '--platform', platform,
    '--network=none', '--read-only', '--mount', `type=volume,source=${volume},destination=/workspace`,
    runtime, '/bin/sh', '-c',
    'set -eu; mkdir /workspace/out; printf output > /workspace/out/result.bin; printf second > /workspace/out/second.bin; : > /workspace/out/empty.bin; '
      + 'ln -s result.bin /workspace/out/link.bin; ln -s out /workspace/linked-dir; mkfifo /workspace/out/pipe; '
      + 'chmod 755 /workspace /workspace/out; chmod 644 /workspace/out/result.bin /workspace/out/second.bin /workspace/out/empty.bin',
  ]);

  function collect(name, value, { uid = 'collector-test-uid', encoded } = {}) {
    containers.add(`${suffix}-${name}`);
    const result = run(engine, [
      'run', '--rm', '--name', `${suffix}-${name}`, '--pull=never', '--platform', platform,
      '--network=none', '--read-only', '--user', '1654:1654',
      '--cap-drop=ALL', '--security-opt=no-new-privileges',
      '--mount', `type=bind,source=${publish},destination=/app,ro=true`,
      '--mount', `type=volume,source=${volume},destination=/workspace,ro=true`,
      '--env', `AGENTWEAVER_COLLECTOR_POD_UID=${uid}`, '--workdir', '/app',
      runtime, 'dotnet', '/app/Agentweaver.AgentHost.dll', '--build-test-output-collector-v1',
      encoded ?? Buffer.from(JSON.stringify(value)).toString('base64url'),
    ], { allowFailure: true, timeout: 120_000 });
    proof.cases.push({
      name, exitCode: result.status, stderr: result.stderr.trim(),
      receipt: result.stdout.trim() ? JSON.parse(result.stdout) : null,
    });
    return result;
  }

  await t.test('regular and empty files produce exact hashes and bound provenance', () => {
    const value = request([obligation('result', 'out/result.bin'), obligation('empty', 'out/empty.bin')]);
    const result = collect('regular', value);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stderr, '');
    const receipt = JSON.parse(result.stdout);
    assert.equal(receipt.operationId, value.operationId);
    assert.equal(receipt.immutableHash, value.immutableHash);
    assert.equal(receipt.requestFingerprint, value.requestFingerprint);
    assert.deepEqual(receipt.checkpoint, value.checkpoint);
    assert.equal(receipt.collectorPodUid, 'collector-test-uid');
    assert.equal(receipt.collectorContainerName, 'buildtest-collector');
    assert.deepEqual(receipt.outputs.map(output => [output.exists, output.capturedBytes, output.capturedSha256]), [
      [true, 6, sha256('output')], [true, 0, sha256('')],
    ]);
    assert.equal(receipt.manifestSha256, 'a791769c4fffd2ef9a249202025154fa76b9e39617fd7e421d78861ba7f8dc24');
    assert.ok(!result.stdout.includes('"output"'), 'file content is not returned');
  });
  await t.test('missing files and missing parent paths remain absent evidence', () => {
    const result = collect('missing', request([
      obligation('missing', 'out/missing.bin'), obligation('missing-parent', 'missing-parent/result.bin'),
    ]));
    assert.equal(result.status, 0, result.stderr);
    for (const output of JSON.parse(result.stdout).outputs) {
      assert.equal(output.required, true);
      assert.equal(output.exists, false);
      assert.equal(output.capturedBytes, 0);
      assert.equal(output.capturedSha256, null);
    }
  });
  for (const [name, relativePath] of [
    ['symlink', 'out/link.bin'], ['symlink-parent', 'linked-dir/result.bin'],
    ['fifo', 'out/pipe'], ['directory', 'out'],
  ]) {
    await t.test(`${name} fails without a success receipt`, () => {
      const result = collect(name, request([obligation(name, relativePath)]));
      assert.equal(result.status, 4);
      assert.equal(result.stdout, '');
      assert.equal(result.stderr.trim(), 'collector_read_failed');
    });
  }
  await t.test('per-file and combined byte limits reject excess output', () => {
    for (const [name, value] of [
      ['file-limit', request([obligation('result', 'out/result.bin', 5)])],
      ['total-limit', request([
        obligation('result', 'out/result.bin', 6), obligation('second', 'out/second.bin', 6),
      ], 6)],
    ]) {
      const result = collect(name, value);
      assert.equal(result.status, 4);
      assert.equal(result.stdout, '');
      assert.equal(result.stderr.trim(), 'collector_read_failed');
    }
  });
  await t.test('malformed requests and absent Pod identity fail before a receipt', () => {
    for (const [name, options] of [
      ['malformed', { encoded: '%' }], ['missing-identity', { uid: '' }],
    ]) {
      const result = collect(name, request([obligation('result', 'out/result.bin')]), options);
      assert.equal(result.status, 2);
      assert.equal(result.stdout, '');
      assert.equal(result.stderr.trim(), 'collector_request_invalid');
    }
  });
});
