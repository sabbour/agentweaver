import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { packComponents } from '../pack.mjs';
import { AGENTHOST_DOCKERFILE, agentHostArchiveMetadata } from '../agenthost-image.mjs';
import { runPublicationCommand } from '../command.mjs';
import { measureAgentHostArchiveSync } from '../measure-agenthost-image.mjs';
import { addAgentHostSource, agentHostComponent, agentHostDockerFixture } from './agenthost-fixture.mjs';

function fixture(t) {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(path.join(parent, 'agenthost-pack-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  addAgentHostSource(root);
  writeFileSync(path.join(root, '.gitignore'), 'artifacts/\n**/bin/\n**/obj/\n');
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
  git('init', '-q');
  git('config', 'core.autocrlf', 'false');
  git('add', '.');
  git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'controlled source');
  const outDir = path.join(root, 'artifacts', 'release', 'pack');
  const calls = [];
  const prepare = (options, overrides = {}) => packComponents({
    schemaVersion: 1, stage: 'draft', components: [agentHostComponent], compatibility: [],
  }, {
    root, outDir, docker: agentHostDockerFixture(root, { calls, ...options }),
    dotnet() { assert.fail('AgentHost must use only its existing Dockerfile, never the generic SDK image'); },
    ...overrides,
  });
  return { root, outDir, calls, git, prepare };
}

test('AgentHost preparation binds two archive hashes and one measured image-config identity to committed inputs', t => {
  const f = fixture(t);
  const ignored = path.join(f.root, 'services', 'agenthost', 'Agentweaver.AgentHost', 'bin');
  mkdirSync(ignored);
  writeFileSync(path.join(ignored, 'untrusted.dll'), 'ignored local output');
  let checkedContext = false;
  const provenance = f.prepare({ buildCheck(args, cwd) {
    assert.equal(readFileSync(path.join(cwd, AGENTHOST_DOCKERFILE), 'utf8'),
      readFileSync(path.join(f.root, AGENTHOST_DOCKERFILE), 'utf8'));
    assert.ok(existsSync(path.join(cwd, 'packages', 'Agentweaver.SourceControl', 'packages.lock.json')));
    assert.equal(existsSync(path.join(cwd, 'services', 'agenthost', 'Agentweaver.AgentHost', 'bin', 'untrusted.dll')), false);
    checkedContext = true;
  } });
  assert.equal(checkedContext, true);
  assert.equal(provenance.artifacts.length, 1);
  const artifact = provenance.artifacts[0];
  const record = provenance.components[0];
  assert.equal(artifact.path, 'Agentweaver.AgentHost.0.1.0.tar');
  assert.equal(artifact.repository, 'agentweaver-agenthost');
  assert.equal(record.build.dockerfile, AGENTHOST_DOCKERFILE);
  assert.deepEqual(record.build.archives.oci, artifact.ociArchive);
  assert.equal(record.build.archives.docker.sha256, artifact.sha256);
  assert.equal(record.build.archives.docker.configDigest, record.build.archives.oci.configDigest);
  assert.equal(record.build.archives.oci.configDigest, record.build.measurement.image.configDigest);
  assert.equal(record.build.imageId, artifact.imageId);
  assert.equal(record.build.measurement.sourceTree, f.git('rev-parse', 'HEAD^{tree}'));
  assert.ok(record.build.measurement.image.compressedPullBytes > 0);
  const build = f.calls.find(({ args }) => args[0] === 'buildx');
  assert.ok(build.args.includes('--load'));
  assert.ok(build.args.includes('--provenance=false'));
  assert.equal(existsSync(path.join(f.outDir, '.agenthost-context')), false);
  const native = f.calls.find(({ args }) => args[0] === 'run').args;
  assert.equal(native.at(-2), artifact.imageId);
  assert.equal(native.at(-1), '--verify-native-runtime');
  for (const argument of ['--read-only', 'none', 'ALL', 'no-new-privileges',
    '/state:rw,noexec,nosuid,nodev,uid=1654,gid=1654,mode=700',
    '/tmp:rw,noexec,nosuid,nodev,uid=1654,gid=1654,mode=700']) assert.ok(native.includes(argument));
});

test('AgentHost preparation accepts the containerd manifest-digest image ID and keeps config provenance distinct', t => {
  const f = fixture(t);
  const provenance = f.prepare({ idMode: 'manifest' });
  const artifact = provenance.artifacts[0];
  const { measurement, runtime } = provenance.components[0].build;
  assert.notEqual(measurement.image.digest, measurement.image.configDigest);
  assert.equal(artifact.imageId, measurement.image.configDigest);
  assert.equal(provenance.components[0].build.archives.docker.configDigest, measurement.image.configDigest);
  assert.ok(runtime.sdkVersion);
  const native = f.calls.find(({ args }) => args[0] === 'run').args;
  assert.equal(native.at(-2), measurement.image.digest);
  assert.equal(f.calls.some(({ args }) => args[0] === 'pull'), false);
});

test('AgentHost preparation still runs the config-digest image ID in the classic store', t => {
  const f = fixture(t);
  const { measurement } = f.prepare({ idMode: 'config' }).components[0].build;
  assert.equal(f.calls.find(({ args }) => args[0] === 'run').args.at(-2), measurement.image.configDigest);
});

test('AgentHost preparation rejects a loaded ID that is neither measured digest, before any run', t => {
  const f = fixture(t);
  assert.throws(() => f.prepare({ idMode: 'manifest', inspectChange: image => ({ ...image, Id: `sha256:${'a'.repeat(64)}` }) }),
    /loaded archive image identity/);
  assert.equal(f.calls.some(({ args }) => ['run', 'pull'].includes(args[0])), false);
});

for (const change of ['source', 'tree', 'version', 'platform', 'size', 'missing-oci', 'missing-docker']) {
  test(`AgentHost preparation rejects ${change} evidence without complete provenance`, t => {
    const f = fixture(t);
    assert.throws(() => f.prepare({ change }));
    assert.equal(existsSync(path.join(f.outDir, 'provenance.json')), false);
    assert.equal(existsSync(path.join(f.outDir, '.agenthost-context')), false);
  });
}

for (const [name, inspectChange] of [
  ['config identity', image => ({ ...image, Id: `sha256:${'e'.repeat(64)}` })],
  ['source label', image => {
    image.Config.Labels['org.opencontainers.image.revision'] = 'e'.repeat(40);
    return image;
  }],
  ['source tree', image => {
    image.Config.Labels['io.agentweaver.source-tree'] = 'e'.repeat(40);
    return image;
  }],
]) {
  test(`AgentHost preparation rejects loaded archive ${name} before native verification`, t => {
    const f = fixture(t);
    assert.throws(() => f.prepare({ inspectChange }), /loaded archive image identity/);
    assert.equal(f.calls.some(({ args }) => args[0] === 'run'), false);
    assert.equal(existsSync(path.join(f.outDir, 'provenance.json')), false);
  });
}

test('AgentHost preparation rejects a failed pinned-runtime check', t => {
  const f = fixture(t);
  assert.throws(() => f.prepare({ runtimeChange: status => ({ ...status, runtimeVersion: 'unverified' }) }),
    /pinned image-owned native runtime check failed/);
  assert.equal(existsSync(path.join(f.outDir, 'provenance.json')), false);
});

test('AgentHost preparation rejects archives that change after byte measurement', t => {
  const f = fixture(t);
  assert.throws(() => f.prepare({}, {
    measureAgentHostArchive(archive, root) {
      const measured = measureAgentHostArchiveSync(archive, root);
      writeFileSync(archive, 'changed after actual measurement');
      return measured;
    },
  }), /archive changed during measurement/);
  assert.equal(existsSync(path.join(f.outDir, 'provenance.json')), false);
});

test('AgentHost preparation requires every committed dependency lock and retains the existing recipe', t => {
  const f = fixture(t);
  const dockerfile = readFileSync(path.join(f.root, AGENTHOST_DOCKERFILE), 'utf8');
  f.git('rm', 'packages/Agentweaver.SourceControl/packages.lock.json');
  f.git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'remove input lock');
  assert.throws(() => f.prepare(), /all input lockfiles must be committed/);
  assert.equal(f.calls.length, 0);
  assert.equal(readFileSync(path.join(f.root, AGENTHOST_DOCKERFILE), 'utf8'), dockerfile);
});

test('publication command supports bounded image inspection and native verification without leaking child output', () => {
  const seen = [];
  const spawn = (bin, args, options) => {
    seen.push({ bin, args, options });
    return { status: 0, stdout: ' controlled output \n', stderr: '' };
  };
  assert.equal(runPublicationCommand('docker', ['image', 'inspect', 'source-image'], undefined, spawn), 'controlled output');
  assert.equal(runPublicationCommand('docker', ['run', 'source-image', '--verify-native-runtime'], undefined, spawn), 'controlled output');
  assert.equal(seen[1].options.timeout, 60_000);
  assert.throws(() => runPublicationCommand('docker', ['run', 'source-image', '--verify-native-runtime'], undefined,
    () => ({ error: { code: 'ETIMEDOUT' }, status: null, stderr: 'private runtime output' })),
  error => error.code === 'PROCESS_TIMEOUT' && !error.message.includes('private runtime output'));
  assert.throws(() => runPublicationCommand('docker', ['run', 'source-image', 'arbitrary-command'], undefined, spawn),
    /unsupported external operation/);
});

test('AgentHost archive hashing measures all chunks without loading the whole archive at once', t => {
  const f = fixture(t);
  mkdirSync(f.outDir, { recursive: true });
  const file = path.join(f.outDir, 'controlled-large-archive.tar');
  const bytes = Buffer.alloc(2 * 1024 * 1024 + 17, 'a');
  writeFileSync(file, bytes);
  const metadata = agentHostArchiveMetadata(file, path.basename(file), `sha256:${'a'.repeat(64)}`);
  assert.equal(metadata.size, bytes.length);
  assert.equal(metadata.sha256, createHash('sha256').update(bytes).digest('hex'));
});
