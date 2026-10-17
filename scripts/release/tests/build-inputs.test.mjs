import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import path from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';
import { gzipSync } from 'node:zlib';
import { approvedBaseImages, approvedWebLockSha256, prepareBuildInputs, readApprovedBuildInputs } from '../prepare-build-inputs.mjs';

const defaultBuilderInspection = `Name:          default
Driver:        docker
Last Activity: 2026-10-12 12:10:00 +0000 UTC

Nodes:
Name:          default
Endpoint:      default
Status:        running
BuildKit:      v0.20.2
Platforms:     linux/amd64
`;

function sourceFixture(t) {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(path.join(parent, 'build-input-source-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  for (const file of [
    'apps/web/package-lock.json', 'apps/web/Dockerfile',
    'services/agenthost/Agentweaver.AgentHost/Dockerfile',
  ]) {
    const target = path.join(root, file);
    mkdirSync(path.dirname(target), { recursive: true });
    writeFileSync(target, readFileSync(file));
  }
  const calls = [];
  const sourceSha = 'a'.repeat(40);
  const run = (bin, args, cwd) => {
    calls.push({ bin, args, cwd });
    if (bin === 'git') return args[0] === 'rev-parse' ? sourceSha : '';
    if (bin === 'npm' || args[0] === 'pull') return '';
    if (args[0] === 'context') return 'default';
    if (args[0] === 'buildx' && args[1] === 'inspect') return defaultBuilderInspection;
    if (args[0] === 'info') return JSON.stringify({
      ServerVersion: '28.0.0', DriverStatus: [['driver-type', 'io.containerd.snapshotter.v1']],
    });
    if (args[0] === 'buildx' && args[1] === 'version') return 'github.com/docker/buildx v0.31.1';
    if (args[0] === 'image') return JSON.stringify({
      Os: 'linux', Architecture: 'amd64', Id: `sha256:${'b'.repeat(64)}`,
      RepoDigests: [args.at(-1).replace(/:[^/:]+(?=@)/, '')],
    });
    throw new Error(`Unexpected command: ${bin} ${args.join(' ')}`);
  };
  return {
    root, expectedSource: sourceSha, cache: path.join(parent, 'isolated-input-cache'), run, calls,
  };
}

test('approved inputs retain three exact source pins and 467 committed dependency origins and integrities', () => {
  const inputs = readApprovedBuildInputs(process.cwd());
  assert.deepEqual(inputs.images, approvedBaseImages);
  assert.equal(inputs.packages.length, 467);
  assert.equal(inputs.packages.filter(item => item.integrity.startsWith('sha1-')).length, 377);
  assert.equal(inputs.packages.filter(item => item.integrity.startsWith('sha512-')).length, 90);
  assert.equal(createHash('sha256').update(readFileSync('apps/web/package-lock.json')).digest('hex'), approvedWebLockSha256);
});

test('preparation pulls only approved digests and fills the same-runner cache with ordinary locked npm and no scripts', t => {
  const f = sourceFixture(t);
  const result = prepareBuildInputs(f);
  assert.equal(result.sourceSha, f.expectedSource);
  assert.equal(result.approvedLockedDependencyCount, 467);
  assert.equal(result.npmCachePreparedForRunner, true);
  assert.equal('cachedDependencyCount' in result, false);
  assert.deepEqual(f.calls.filter(call => call.args[0] === 'pull').map(call => call.args), approvedBaseImages.map(image =>
    ['pull', '--platform', 'linux/amd64', image]));
  assert.deepEqual(f.calls.filter(call => call.args[0] === 'buildx' && call.args[1] === 'inspect')
    .map(call => call.args), [['buildx', 'inspect', 'default']]);
  const npm = f.calls.filter(call => call.bin === 'npm');
  assert.deepEqual(npm, [{
    bin: 'npm', cwd: path.join(f.root, 'apps', 'web'), args: [
      'ci', '--ignore-scripts', '--no-audit', '--no-fund', '--no-progress',
      '--replace-registry-host=never', '--registry=https://registry.npmjs.org', '--cache', f.cache,
    ],
  }]);
  assert.equal(f.calls.some(call => ['build', 'save', 'load', 'push', 'login'].includes(call.args[0])), false);
});

test('source, lock, image-pin and cache isolation failures cause no input download', t => {
  for (const kind of ['source', 'dirty', 'lock', 'pin', 'cache']) {
    const f = sourceFixture(t);
    const options = { ...f };
    if (kind === 'source') options.expectedSource = 'c'.repeat(40);
    if (kind === 'dirty') options.run = (bin, args, cwd) =>
      bin === 'git' && args[0] === 'status' ? ' M tracked-file' : f.run(bin, args, cwd);
    if (kind === 'lock') writeFileSync(path.join(f.root, 'apps', 'web', 'package-lock.json'), '{}');
    if (kind === 'pin') writeFileSync(path.join(f.root, 'apps', 'web', 'Dockerfile'), 'FROM unapproved:latest\n');
    if (kind === 'cache') options.cache = path.join(f.root, 'cache');
    assert.throws(() => prepareBuildInputs(options), /approved|clean|immutable|isolated/);
    assert.equal(f.calls.some(call => call.args[0] === 'pull' || call.bin === 'npm'), false, kind);
  }
});

test('missing shared store, old engine, remote context and invalid builder metadata fail before image pulls without changing the daemon', t => {
  for (const kind of ['store', 'engine', 'context', 'buildx', 'driver', 'endpoint', 'missing-driver', 'missing-endpoint', 'duplicate-endpoint']) {
    const f = sourceFixture(t);
    const run = (bin, args, cwd) => {
      if (kind === 'context' && args[0] === 'context') return 'remote';
      if (args[0] === 'info' && ['store', 'engine'].includes(kind)) return JSON.stringify({
        ServerVersion: kind === 'engine' ? '26.0.0' : '28.0.0',
        DriverStatus: kind === 'store' ? [] : [['driver-type', 'io.containerd.snapshotter.v1']],
      });
      if (args[0] === 'buildx' && kind === 'buildx' && args[1] === 'version') return 'github.com/docker/buildx v0.12.1';
      if (args[0] === 'buildx' && args[1] === 'inspect') {
        if (kind === 'driver') return defaultBuilderInspection.replace('Driver:        docker', 'Driver:        docker-container');
        if (kind === 'endpoint') return defaultBuilderInspection.replace('Endpoint:      default', 'Endpoint:      tcp://builder.example');
        if (kind === 'missing-driver') return defaultBuilderInspection.replace('Driver:        docker\n', '');
        if (kind === 'missing-endpoint') return defaultBuilderInspection.replace('Endpoint:      default\n', '');
        if (kind === 'duplicate-endpoint') return `${defaultBuilderInspection}Endpoint:      default\n`;
      }
      return f.run(bin, args, cwd);
    };
    assert.throws(() => prepareBuildInputs({ ...f, run }), /context|containerd|shared image store|Buildx 0.13|inspection output/);
    assert.equal(f.calls.some(call => call.args[0] === 'pull' || call.bin === 'npm'), false, kind);
    assert.equal(f.calls.some(call => ['sudo', 'systemctl'].includes(call.bin)), false, kind);
  }
});

test('wrong pulled digest or platform blocks npm cache fill, and command failures propagate', t => {
  for (const kind of ['digest', 'platform', 'pull']) {
    const f = sourceFixture(t);
    const run = (bin, args, cwd) => {
      if (kind === 'pull' && args[0] === 'pull') throw new Error('controlled pull failure');
      if (args[0] === 'image') return JSON.stringify({
        Os: 'linux', Architecture: kind === 'platform' ? 'arm64' : 'amd64',
        Id: `sha256:${'b'.repeat(64)}`,
        RepoDigests: kind === 'platform' ? [args.at(-1).replace(/:[^/:]+(?=@)/, '')] : [],
      });
      return f.run(bin, args, cwd);
    };
    assert.throws(() => prepareBuildInputs({ ...f, run }), /immutable reference|controlled pull failure/);
    assert.equal(f.calls.some(call => call.bin === 'npm'), false, kind);
  }
  const f = sourceFixture(t);
  assert.throws(() => prepareBuildInputs({ ...f, run: (bin, args, cwd) => {
    if (bin === 'npm') throw new Error('controlled integrity failure');
    return f.run(bin, args, cwd);
  } }), /controlled integrity failure/);
});

test('preparation rechecks source and exact lock bytes after npm succeeds', t => {
  for (const kind of ['head', 'lock']) {
    const f = sourceFixture(t);
    let npmRan = false;
    const run = (bin, args, cwd) => {
      if (bin === 'npm') {
        npmRan = true;
        if (kind === 'lock') writeFileSync(path.join(f.root, 'apps', 'web', 'package-lock.json'), '{}');
      }
      if (npmRan && kind === 'head' && bin === 'git' && args[0] === 'rev-parse') return 'c'.repeat(40);
      return f.run(bin, args, cwd);
    };
    assert.throws(() => prepareBuildInputs({ ...f, run }), /approved|clean/);
  }
});

test('product image job prepares approved inputs with the shared Docker engine before packing', () => {
  const workflow = readFileSync('.github/workflows/v1-release-pack.yml', 'utf8').replaceAll('\r\n', '\n');
  const images = workflow.slice(workflow.indexOf('\n  images:'), workflow.indexOf('\n  publish:'));
  assert.equal((images.match(/uses: docker\/setup-docker-action@2bf61fb9464cc67f0cbdeabed6aa0380accd1c70/g) ?? []).length, 1);
  assert.match(images, /- name: Set up product Docker engine\n\s+uses: docker\/setup-docker-action@2bf61fb9464cc67f0cbdeabed6aa0380accd1c70\n\s+with:\n\s+version: 'v29\.9\.0'\n\s+set-host: true\n\s+daemon-config:[\s\S]*?"containerd-snapshotter": true/);
  assert.doesNotMatch(images, /docker context update default|setup-buildx-action/);
  assert.match(images, /Prepare approved product build inputs[\s\S]*?EXPECTED_SOURCE: \$\{\{ github\.sha \}\}[\s\S]*?DOCKER_CONTEXT: default/);
  assert.match(images, /npm_config_cache: \$\{\{ runner.temp \}\}\/agentweaver-release-npm-cache/);
  assert.ok(images.indexOf('Set up product Docker engine') < images.indexOf('Prepare approved product build inputs'));
  assert.ok(images.indexOf('Prepare approved product build inputs') < images.indexOf('Build product Docker images'));
  assert.doesNotMatch(images, /\bsudo\b|systemctl|daemon\.json|buildx-stable|docker buildx install/);
});

function packageTarball(name) {
  const bytes = Buffer.from(JSON.stringify({
    name, version: '1.0.0',
    scripts: { postinstall: 'node -e "require(\'node:fs\').writeFileSync(\'script-ran\', \'yes\')"' },
  }));
  const header = Buffer.alloc(512);
  header.write('package/package.json');
  header.write('0000644\0', 100);
  header.write('0000000\0', 108);
  header.write('0000000\0', 116);
  header.write(`${bytes.length.toString(8).padStart(11, '0')}\0`, 124);
  header.write('00000000000\0', 136);
  header.fill(32, 148, 156);
  header.write('0', 156);
  header.write('ustar\0', 257);
  header.write('00', 263);
  const checksum = header.reduce((sum, byte) => sum + byte, 0);
  header.write(`${checksum.toString(8).padStart(6, '0')}\0 `, 148);
  return gzipSync(Buffer.concat([
    header, bytes, Buffer.alloc((512 - bytes.length % 512) % 512), Buffer.alloc(1024),
  ]));
}

test('standard locked npm install supplies its unchanged offline install for SHA1 and SHA512 without scripts', async t => {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(path.join(parent, 'build-input-npm-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const tarballs = new Map([
    ['/sha1.tgz', packageTarball('cache-sha1')],
    ['/sha512.tgz', packageTarball('cache-sha512')],
  ]);
  const requests = [];
  const server = createServer((request, response) => {
    requests.push(request.url);
    const bytes = tarballs.get(request.url);
    response.writeHead(bytes ? 200 : 404, { 'content-type': 'application/octet-stream' });
    response.end(bytes);
  });
  const closeServer = () => {
    if (!server.listening) return Promise.resolve();
    const closed = new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
    server.closeAllConnections();
    return closed;
  };
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(closeServer);
  const origin = `http://127.0.0.1:${server.address().port}`;
  const packages = { '': { name: 'cache-fixture', version: '1.0.0', dependencies: {
    'cache-sha1': '1.0.0', 'cache-sha512': '1.0.0',
  } } };
  for (const algorithm of ['sha1', 'sha512']) {
    const name = `cache-${algorithm}`;
    packages[`node_modules/${name}`] = {
      version: '1.0.0', resolved: `${origin}/${algorithm}.tgz`,
      integrity: `${algorithm}-${createHash(algorithm).update(tarballs.get(`/${algorithm}.tgz`)).digest('base64')}`,
      hasInstallScript: true,
    };
  }
  writeFileSync(path.join(root, 'package.json'), JSON.stringify(packages['']));
  const lockPath = path.join(root, 'package-lock.json');
  writeFileSync(lockPath, JSON.stringify({ name: 'cache-fixture', version: '1.0.0', lockfileVersion: 3, packages }));
  const lockBefore = readFileSync(lockPath);
  const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';
  const install = offline => promisify(execFile)(npm, [
    'ci', '--ignore-scripts', '--no-audit', '--no-fund', '--no-progress',
    '--replace-registry-host=never', '--registry', origin,
    '--cache', path.join(root, 'cache'), ...(offline ? ['--offline'] : []),
  ], {
    cwd: root, shell: process.platform === 'win32',
    env: { ...process.env, npm_config_update_notifier: 'false', npm_config_fetch_retries: '0' },
    timeout: 60_000,
  });
  await install(false);
  assert.deepEqual(requests.sort(), ['/sha1.tgz', '/sha512.tgz']);
  assert.deepEqual(readFileSync(lockPath), lockBefore);
  for (const name of ['cache-sha1', 'cache-sha512']) {
    assert.equal(existsSync(path.join(root, 'node_modules', name, 'script-ran')), false);
  }
  await closeServer();
  await install(true);
  assert.equal(requests.length, 2);
  assert.deepEqual(readFileSync(lockPath), lockBefore);
  for (const name of ['cache-sha1', 'cache-sha512']) {
    assert.equal(JSON.parse(readFileSync(path.join(root, 'node_modules', name, 'package.json'))).version, '1.0.0');
    assert.equal(existsSync(path.join(root, 'node_modules', name, 'script-ran')), false);
  }
});
