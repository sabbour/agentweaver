import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { packComponentsFromFile } from '../pack.mjs';
import { publishArtifacts } from '../publish.mjs';

function fixture(t, { service = true, pinnedBase = true, lock = true, baseImageXml } = {}) {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(path.join(parent, 'publication-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  const components = [{ id: 'Pkg', kind: 'library', version: '0.1.0', project: 'packages/Pkg/Pkg.csproj' }];
  if (service) components.push({ id: 'Svc', kind: 'service', version: '0.1.0', project: 'services/Svc/Svc.csproj' });
  const manifest = { schemaVersion: 1, stage: 'draft', components, compatibility: [] };
  mkdirSync(path.join(root, 'releases'));
  writeFileSync(path.join(root, 'releases', 'foundation.json'), JSON.stringify(manifest));
  for (const component of components) {
    const directory = path.dirname(path.join(root, component.project));
    mkdirSync(directory, { recursive: true });
    writeFileSync(path.join(root, component.project), `<Project><PropertyGroup><Version>0.1.0</Version>${
      component.kind === 'service' ? baseImageXml ?? (pinnedBase ? `<ContainerBaseImage>mcr.microsoft.com/dotnet/runtime@sha256:${'a'.repeat(64)}</ContainerBaseImage>` : '') : ''
    }</PropertyGroup></Project>`);
    if (lock) writeFileSync(path.join(directory, 'packages.lock.json'), '{"version":1,"dependencies":{}}');
  }
  writeFileSync(path.join(root, '.gitignore'), 'artifacts/\n');
  git('init', '-q');
  git('config', 'core.autocrlf', 'false');
  git('add', '.');
  git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'source');
  const calls = [];
  const outDir = path.join(root, 'artifacts', 'release', 'pack');
  const prepare = (override = {}) => packComponentsFromFile(path.join(root, 'releases', 'foundation.json'), {
    root, outDir,
    dotnet: (args) => {
      calls.push(args);
      if (args[0] === 'pack') writeFileSync(path.join(outDir, 'Pkg.0.1.0.nupkg'), 'test package bytes');
      if (args[0] === 'publish') {
        const output = args.find((arg) => arg.startsWith('-p:ContainerArchiveOutputPath=')).split('=').slice(1).join('=');
        writeFileSync(output, 'test container archive bytes');
      }
    },
    ...override,
  });
  const env = {
    RELEASE_NUGET_SOURCE: 'https://feed.example.invalid/v3/index.json', RELEASE_NUGET_API_KEY: 'test-secret',
    RELEASE_REGISTRY: 'registry.example.invalid', RELEASE_REGISTRY_USER: 'test-user',
    RELEASE_REGISTRY_PASSWORD: 'test-password', DOCKER_CONFIG: path.join(root, 'artifacts', 'docker-config'),
  };
  const sourceSha = git('rev-parse', 'HEAD');
  const publish = (options = {}) => publishArtifacts('releases/foundation.json', outDir, sourceSha, {
    root, confirmed: true, env, run: (bin, args) => {
      if (bin === 'docker' && args[0] === 'inspect') return JSON.stringify([`registry.example.invalid/svc@sha256:${'b'.repeat(64)}`]);
      return '';
    }, ...options,
  });
  return { root, outDir, prepare, publish, calls, sourceSha };
}

test('prepares locked packages and unpublished service images without a registry push or fake digest', (t) => {
  const f = fixture(t);
  const receipt = f.prepare();
  assert.deepEqual(f.calls.map(([verb]) => verb), ['restore', 'build', 'pack', 'restore', 'build', 'publish']);
  assert.ok(f.calls.filter(([verb]) => verb === 'restore').every((args) => args.includes('--locked-mode')));
  assert.ok(f.calls.find(([verb]) => verb === 'publish').includes('-p:EnableSdkContainerSupport=true'));
  const image = receipt.artifacts.find(({ kind }) => kind === 'image');
  assert.equal(image.path, 'Svc.0.1.0.tar.gz');
  assert.equal(image.repository, 'svc');
  assert.equal(image.imageDigest, undefined);
  assert.match(image.baseImage, /@sha256:[a-f0-9]{64}$/);
  assert.ok(receipt.components.every(({ lock }) => /^[a-f0-9]{64}$/.test(lock.sha256)));
  assert.equal(receipt.sourceSha, f.sourceSha);
});

for (const options of [{ lock: false }, { pinnedBase: false }]) {
  test(`rejects unsafe preparation before build: ${JSON.stringify(options)}`, (t) => {
    const f = fixture(t, options);
    assert.throws(() => f.prepare(), /packages.lock.json|immutable ContainerBaseImage/);
    assert.deepEqual(f.calls, []);
    assert.equal(existsSync(f.outDir), false);
  });
}

const immutableBase = `mcr.microsoft.com/dotnet/runtime@sha256:${'a'.repeat(64)}`;
for (const [name, baseImageXml] of [
  ['commented pin with active mutable tag', `<!-- <ContainerBaseImage>${immutableBase}</ContainerBaseImage> --><ContainerBaseImage>mcr.microsoft.com/dotnet/runtime:10.0</ContainerBaseImage>`],
  ['multiple active pins', `<ContainerBaseImage>${immutableBase}</ContainerBaseImage><ContainerBaseImage>${immutableBase}</ContainerBaseImage>`],
  ['conditional duplicate', `<ContainerBaseImage>${immutableBase}</ContainerBaseImage><ContainerBaseImage Condition="'$(Configuration)' == 'Release'">${immutableBase}</ContainerBaseImage>`],
]) {
  test(`rejects ${name} before any build or output`, (t) => {
    const f = fixture(t, { baseImageXml });
    assert.throws(() => f.prepare(), /exactly one active explicit immutable ContainerBaseImage/);
    assert.deepEqual(f.calls, []);
    assert.equal(existsSync(f.outDir), false);
  });
}

test('base-image provenance records only the single active pin, ignoring XML comments', (t) => {
  const f = fixture(t, { baseImageXml: `<!-- <ContainerBaseImage>ignored:mutable</ContainerBaseImage> --><ContainerBaseImage> ${immutableBase} </ContainerBaseImage>` });
  assert.equal(f.prepare().artifacts.find(({ kind }) => kind === 'image').baseImage, immutableBase);
});

test('failed image preparation emits no complete provenance and rejects reuse of partial output', (t) => {
  const f = fixture(t);
  assert.throws(() => f.prepare({ dotnet: (args) => {
    if (args[0] === 'pack') writeFileSync(path.join(f.outDir, 'Pkg.0.1.0.nupkg'), 'partial package');
    if (args[0] === 'publish') throw new Error('image build failed');
  } }), /image build failed/);
  assert.equal(existsSync(path.join(f.outDir, 'provenance.json')), false);
  assert.throws(() => f.prepare(), /fresh, empty/);
});

test('manual publication requires confirmation and intact exact-source artifacts before commands', (t) => {
  const f = fixture(t);
  f.prepare();
  const calls = [];
  assert.throws(() => f.publish({ confirmed: false, run: (...args) => calls.push(args) }), /explicit confirmation/);
  writeFileSync(path.join(f.outDir, 'Svc.0.1.0.tar.gz'), 'tampered');
  assert.throws(() => f.publish({ run: (...args) => calls.push(args) }), /artifact hash mismatch/);
  assert.deepEqual(calls, []);
});

test('manual publication records actual immutable registry digests and never changes draft composition', (t) => {
  const f = fixture(t);
  f.prepare();
  const before = readFileSync(path.join(f.root, 'releases', 'foundation.json'));
  const receipt = f.publish();
  assert.equal(receipt.status, 'published');
  assert.equal(receipt.sourceSha, f.sourceSha);
  assert.equal(receipt.published[1].image, `registry.example.invalid/svc@sha256:${'b'.repeat(64)}`);
  assert.deepEqual(readFileSync(path.join(f.root, 'releases', 'foundation.json')), before);
  const text = readFileSync(path.join(f.outDir, 'publication.json'), 'utf8');
  assert.doesNotMatch(text, /test-secret|test-password|test-user/);
  assert.throws(() => f.publish(), /receipt already exists/);
});

test('manual publication failure preserves partial receipts instead of claiming atomic external publication', (t) => {
  const f = fixture(t);
  f.prepare();
  assert.throws(() => f.publish({ run: (bin, args) => {
    if (bin === 'docker' && args[0] === 'push') throw new Error('mock registry failure');
    return '';
  } }), /mock registry failure/);
  const receipt = JSON.parse(readFileSync(path.join(f.outDir, 'publication.json'), 'utf8'));
  assert.equal(receipt.status, 'partial');
  assert.equal(receipt.published.length, 1);
  assert.equal(receipt.published[0].kind, 'package');
  assert.throws(() => f.publish(), /receipt already exists/);
});

test('image publication without an actual returned immutable digest is never successful', (t) => {
  const f = fixture(t);
  f.prepare();
  assert.throws(() => f.publish({ run: (bin, args) => bin === 'docker' && args[0] === 'inspect' ? '[]' : '' }), /immutable digest/);
  assert.equal(JSON.parse(readFileSync(path.join(f.outDir, 'publication.json'), 'utf8')).status, 'partial');
});
