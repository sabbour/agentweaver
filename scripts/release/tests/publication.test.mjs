import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { packComponentsFromFile } from '../pack.mjs';
import { publishArtifacts } from '../publish.mjs';
import { resolveProbeImageSource } from '../../azure/build-foundation-probe-image.mjs';

function fixture(t, { service = true, pinnedBase = true, lock = true, baseImageXml,
  foundationProbe = false, probeVersion = '0.0.1' } = {}) {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(path.join(parent, 'publication-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  const components = [{ id: 'Pkg', kind: 'library', version: '0.1.0', project: 'packages/Pkg/Pkg.csproj' }];
  if (service) components.push({ id: 'Svc', kind: 'service', version: '0.1.0', project: 'services/Svc/Svc.csproj' });
  if (foundationProbe) components.push({
    id: 'Agentweaver.FoundationProbe', kind: 'service', version: probeVersion,
    project: 'tools/Agentweaver.FoundationProbe/Agentweaver.FoundationProbe.csproj',
  });
  const manifest = { schemaVersion: 1, stage: 'draft', components, compatibility: [] };
  mkdirSync(path.join(root, 'releases'));
  writeFileSync(path.join(root, 'releases', 'foundation.json'), JSON.stringify(manifest));
  for (const component of components) {
    const directory = path.dirname(path.join(root, component.project));
    mkdirSync(directory, { recursive: true });
    writeFileSync(path.join(root, component.project), `<Project><PropertyGroup><Version>${component.version}</Version>${
      component.kind === 'service' ? baseImageXml ?? (pinnedBase ? `<ContainerBaseImage>mcr.microsoft.com/dotnet/runtime@sha256:${'a'.repeat(64)}</ContainerBaseImage>` : '') : ''
    }</PropertyGroup></Project>`);
    if (lock) writeFileSync(path.join(directory, 'packages.lock.json'), '{"version":1,"dependencies":{}}');
  }
  if (foundationProbe) {
    mkdirSync(path.join(root, 'infra', 'bicep'), { recursive: true });
    writeFileSync(path.join(root, 'infra', 'bicep', 'main.bicep'), "targetScope = 'resourceGroup'\n");
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
    GITHUB_REPOSITORY: 'test/release', GH_TOKEN: 'test-github-secret',
    RELEASE_NUGET_SOURCE: 'https://feed.example.invalid/v3/index.json', RELEASE_NUGET_API_KEY: 'test-secret',
    RELEASE_REGISTRY: 'registry.example.invalid', RELEASE_REGISTRY_USER: 'test-user',
    RELEASE_REGISTRY_PASSWORD: 'test-password', DOCKER_CONFIG: path.join(root, 'artifacts', 'docker-config'),
  };
  const sourceSha = git('rev-parse', 'HEAD');
  const tags = new Map();
  const refs = new Map();
  const remoteCalls = [];
  const externalCalls = [];
  const github = (args, input) => {
    remoteCalls.push({ args, input });
    const method = args[args.indexOf('--method') + 1];
    const endpoint = args[3];
    const body = input ? JSON.parse(input) : undefined;
    if (method === 'GET' && endpoint.includes('/git/matching-refs/')) {
      const prefix = `refs/${endpoint.split('/git/matching-refs/')[1]}`;
      return JSON.stringify([...refs.values()].filter(({ ref }) => ref.startsWith(prefix)));
    }
    if (method === 'POST' && endpoint.endsWith('/git/tags')) {
      const sha = createHash('sha1').update(input).digest('hex');
      const tag = { sha, tag: body.tag, message: body.message, object: { type: body.type, sha: body.object } };
      tags.set(sha, tag);
      return JSON.stringify(tag);
    }
    if (method === 'POST' && endpoint.endsWith('/git/refs')) {
      if (refs.has(body.ref)) throw new Error('ref already exists (atomic race lost)');
      const ref = { ref: body.ref, object: { type: 'tag', sha: body.sha } };
      refs.set(body.ref, ref);
      return JSON.stringify(ref);
    }
    throw new Error(`unexpected injected GitHub request: ${method} ${endpoint}`);
  };
  const publish = (options = {}) => {
    const external = options.run ?? ((bin, args) => {
      if (bin === 'docker' && args[0] === 'inspect') {
        const repository = args.at(-1).replace(/:[^/:]+$/, '');
        return JSON.stringify([`${repository}@sha256:${'b'.repeat(64)}`]);
      }
      return '';
    });
    return publishArtifacts('releases/foundation.json', outDir, sourceSha, {
      root, confirmed: true, env, ...options,
      run: (bin, args, input) => {
        if (bin === 'gh') return (options.github ?? github)(args, input);
        externalCalls.push({ bin, args, input });
        return external(bin, args, input);
      },
    });
  };
  return { root, outDir, prepare, publish, calls, sourceSha, github, tags, refs, remoteCalls, externalCalls, env };
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

test('a Debug-only pin is forced into the actual Release build and container publication', (t) => {
  const f = fixture(t, { baseImageXml: `<ContainerBaseImage Condition="'$(Configuration)' == 'Debug'">${immutableBase}</ContainerBaseImage>` });
  const image = f.prepare().artifacts.find(({ kind }) => kind === 'image');
  assert.equal(image.baseImage, immutableBase);
  const serviceCommands = f.calls.filter((args) => ['build', 'publish'].includes(args[0]) && args[1].endsWith('Svc.csproj'));
  assert.equal(serviceCommands.length, 2);
  for (const args of serviceCommands) {
    assert.ok(args.includes('Release'));
    assert.equal(args.filter((arg) => arg.startsWith('-p:ContainerBaseImage=')).length, 1);
    assert.ok(args.includes(`-p:ContainerBaseImage=${image.baseImage}`));
  }
});

test('pack upload and download use the same attempt-independent source/run identity', () => {
  const workflow = readFileSync('.github/workflows/v1-release-pack.yml', 'utf8');
  assert.equal((workflow.match(/name: v1-release-pack-\$\{\{ github.sha \}\}-\$\{\{ github.run_id \}\}/g) ?? []).length, 3);
  assert.doesNotMatch(workflow, /name: v1-release-pack-.*github\.run_attempt/);
  assert.match(workflow, /name: v1-release-pack-[^\n]+\n\s+overwrite: true/);
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

test('packages-only publication claims and pushes only the exact NuGet selection', (t) => {
  const f = fixture(t);
  f.prepare({ packagesOnly: true });
  const env = { ...f.env };
  delete env.RELEASE_REGISTRY;
  delete env.RELEASE_REGISTRY_USER;
  delete env.RELEASE_REGISTRY_PASSWORD;
  delete env.DOCKER_CONFIG;

  const receipt = f.publish({ packagesOnly: true, env });
  assert.equal(receipt.status, 'published');
  assert.deepEqual(receipt.planned.map(({ id, kind }) => ({ id, kind })), [{ id: 'Pkg', kind: 'package' }]);
  assert.deepEqual(receipt.published.map(({ id, kind }) => ({ id, kind })), [{ id: 'Pkg', kind: 'package' }]);
  assert.ok(!f.externalCalls.some(({ bin }) => bin === 'docker'));
  assert.ok(f.externalCalls.some(({ bin, args }) => bin === 'dotnet' && args[0] === 'nuget'));
});

test('packages-only publication rejects a full composition pack before any external command', (t) => {
  const f = fixture(t);
  f.prepare();
  assert.throws(() => f.publish({ packagesOnly: true }),
    /provenance component selection does not match the package-only manifest selection/);
  assert.deepEqual(f.remoteCalls, []);
  assert.deepEqual(f.externalCalls, []);
});

test('Probe-only preparation and publication preserve the existing hyphenated image and exclude other components', t => {
  const f = fixture(t, { foundationProbe: true });
  const prepared = f.prepare({ foundationProbeOnly: true });
  assert.deepEqual(prepared.components.map(component => component.id), ['Agentweaver.FoundationProbe']);
  assert.equal(prepared.artifacts[0].repository, 'agentweaver-foundation-probe');
  assert.deepEqual(f.calls.map(args => args[0]), ['restore', 'build', 'publish']);
  assert.ok(f.calls.every(args => !args[1].endsWith('Svc.csproj') && !args[1].endsWith('Pkg.csproj')));
  const env = { ...f.env, RELEASE_REGISTRY: 'ghcr.io/sabbour' };
  delete env.RELEASE_NUGET_SOURCE;
  delete env.RELEASE_NUGET_API_KEY;
  const receipt = f.publish({ foundationProbeOnly: true, env });
  assert.deepEqual(receipt.planned.map(record => record.destination),
    ['ghcr.io/sabbour/agentweaver-foundation-probe:0.0.1']);
  assert.deepEqual(receipt.published.map(record => record.id), ['Agentweaver.FoundationProbe']);
  assert.equal(receipt.published[0].image, `ghcr.io/sabbour/agentweaver-foundation-probe@sha256:${'b'.repeat(64)}`);
  assert.ok(!f.externalCalls.some(call => call.bin === 'dotnet'));
  assert.deepEqual(f.externalCalls.find(call => call.bin === 'docker' && call.args[0] === 'tag').args,
    ['tag', 'agentweaver-foundation-probe:0.0.1', 'ghcr.io/sabbour/agentweaver-foundation-probe:0.0.1']);
  assert.ok(!JSON.stringify(receipt).includes('agentweaver.foundationprobe'));
});

test('Probe-only publication refuses a full preparation and contradictory selectors before any external effects', t => {
  const f = fixture(t, { foundationProbe: true });
  assert.throws(() => f.prepare({ foundationProbeOnly: true, packagesOnly: true }), /mutually exclusive/);
  f.prepare();
  assert.throws(() => f.publish({ foundationProbeOnly: true }), /Foundation Probe-only manifest selection/);
  assert.throws(() => f.publish({ foundationProbeOnly: true, packagesOnly: true }), /mutually exclusive/);
  assert.deepEqual(f.remoteCalls, []);
  assert.deepEqual(f.externalCalls, []);
});

const oldProbeDigest = 'sha256:452be7e284ee6c33814fcedcf1d7c98f98384d09ea7239ad851f9cb316727c9a';
const oldProbePlatform = 'sha256:517fb4f4d1af150d703e94b324fce6b1f22d5af8bf93b840ef4743b3883b7358';
const oldProbeAttestation = 'sha256:01c4362f57d59be166e15d1963ee285ca7cd46de2d53a053255e0c1a24986e5f';
const probeTarget = 'ghcr.io/sabbour/agentweaver-foundation-probe:0.0.0';
const replacementNamespace = 'agentweaver-publication/initial-foundation-probe-0.0.0-replacement';

const currentProbeDigest = 'sha256:835d5b8899f2a8956faf24d46a934ec745d91ff83363d77f22f2859c2f743969';
const samplerReplacementNamespace = 'agentweaver-publication/foundation-probe-0.0.0-sampler-replacement';

function initialProbeFixture(t, { sampler = false } = {}) {
  const f = fixture(t, { foundationProbe: true, probeVersion: '0.0.0' });
  execFileSync('git', ['update-ref', 'refs/remotes/origin/v1', f.sourceSha], { cwd: f.root });
  f.prepare({ foundationProbeOnly: true });
  const env = { ...f.env, GITHUB_REPOSITORY: 'sabbour/agentweaver', RELEASE_REGISTRY: 'ghcr.io/sabbour' };
  const source = resolveProbeImageSource({ repoRoot: f.root });
  const options = { foundationProbeOnly: true, env, ...(sampler
    ? { confirmFoundationProbeSamplerReplacement: f.sourceSha, expectedFoundationProbeSamplerDigest: currentProbeDigest }
    : { confirmFoundationProbeInitialReplacement: f.sourceSha }) };
  let pushed = false;
  let targetReads = 0;
  const oldIndex = sampler ? {
    schemaVersion: 2, mediaType: 'application/vnd.docker.distribution.manifest.v2+json',
    digest: currentProbeDigest,
  } : {
    schemaVersion: 2, mediaType: 'application/vnd.oci.image.index.v1+json', digest: oldProbeDigest,
    manifests: [{ digest: oldProbePlatform }, { digest: oldProbeAttestation }],
  };
  const native = (bin, args) => {
    if (bin !== 'docker') return '';
    if (args[0] === 'push') pushed = true;
    if (args[0] === 'buildx') {
      const reference = args[3];
      if (sampler && reference === `ghcr.io/sabbour/agentweaver-foundation-probe@${oldProbeDigest}`)
        return JSON.stringify({
          schemaVersion: 2, mediaType: 'application/vnd.oci.image.index.v1+json', digest: oldProbeDigest,
          manifests: [{ digest: oldProbePlatform }, { digest: oldProbeAttestation }],
        });
      if (reference === probeTarget) {
        targetReads++;
        return JSON.stringify(pushed ? { schemaVersion: 2, digest: `sha256:${'b'.repeat(64)}` } : oldIndex);
      }
      return JSON.stringify({ schemaVersion: 2, digest: reference.split('@')[1] });
    }
    if (args[0] === 'inspect' && args.includes('{{json .Config}}')) return JSON.stringify({
      User: '10001:10001', Entrypoint: ['dotnet', 'Agentweaver.FoundationProbe.dll'],
      Labels: {
        'org.opencontainers.image.revision': source.sourceSha,
        'io.agentweaver.source-tree': source.sourceTree,
        'io.agentweaver.infrastructure-source-hash': source.sourceHash,
        'org.opencontainers.image.version': '0.0.0',
      },
    });
    if (args[0] === 'inspect') return JSON.stringify([
      `ghcr.io/sabbour/agentweaver-foundation-probe@sha256:${'b'.repeat(64)}`,
    ]);
    return '';
  };
  return { f, options, native, get targetReads() { return targetReads; } };
}

test('sampler replacement consumes its distinct permanent claim and preserves consumed initial history', t => {
  const { f, options, native } = initialProbeFixture(t, { sampler: true });
  const historical = new Map(['claim', 'result'].map(name => {
    const ref = `refs/tags/${replacementNamespace}/${name}`;
    return [ref, { ref, object: { type: 'tag', sha: 'd'.repeat(40) } }];
  }));
  for (const [ref, record] of historical) f.refs.set(ref, record);
  const receipt = f.publish({ ...options, run: native });
  assert.equal(receipt.status, 'published');
  assert.equal(receipt.initialProbeReplacement, undefined);
  assert.equal(receipt.samplerProbeReplacement.expectedCurrentDigest, currentProbeDigest);
  assert.equal(receipt.samplerProbeReplacement.previousIndex.digest, currentProbeDigest);
  assert.equal(receipt.samplerProbeReplacement.newDigest, `sha256:${'b'.repeat(64)}`);
  assert.equal(receipt.samplerProbeReplacement.originalIndexAndPlatformsRetained, true);
  assert.deepEqual(receipt.published.map(item => item.id), ['Agentweaver.FoundationProbe']);
  for (const [ref, record] of historical) assert.equal(f.refs.get(ref), record);
  for (const name of ['claim', 'result']) assert.ok(f.refs.has(`refs/tags/${samplerReplacementNamespace}/${name}`));
  assert.equal(f.externalCalls.filter(call => call.args[0] === 'push').length, 1);
  assert.ok(f.externalCalls.some(call => call.args[3] === `ghcr.io/sabbour/agentweaver-foundation-probe@${currentProbeDigest}`));
  for (const digest of [oldProbeDigest, oldProbePlatform, oldProbeAttestation])
    assert.ok(f.externalCalls.some(call => call.args[3] === `ghcr.io/sabbour/agentweaver-foundation-probe@${digest}`));
  assert.ok(!f.externalCalls.some(call => call.bin === 'dotnet'));
});

test('sampler replacement rejects absent/wrong intent, arbitrary digest, source, composition, and target without writes', t => {
  const { f, options } = initialProbeFixture(t, { sampler: true });
  for (const change of [
    { confirmed: false },
    { confirmFoundationProbeSamplerReplacement: undefined },
    { confirmFoundationProbeSamplerReplacement: 'e'.repeat(40) },
    { expectedFoundationProbeSamplerDigest: undefined },
    { expectedFoundationProbeSamplerDigest: oldProbeDigest },
    { expectedFoundationProbeSamplerDigest: `sha256:${'f'.repeat(64)}` },
    { confirmFoundationProbeInitialReplacement: f.sourceSha },
    { foundationProbeOnly: false },
    { packagesOnly: true },
    { env: { ...options.env, GITHUB_REPOSITORY: 'other/repo' } },
    { env: { ...options.env, RELEASE_REGISTRY: 'ghcr.io/other' } },
  ]) {
    assert.throws(() => f.publish({ ...options, ...change }),
      /confirmation|replacement|Probe 0.0.0|mutually exclusive/);
    assert.deepEqual(f.remoteCalls, []);
    assert.deepEqual(f.externalCalls, []);
  }
  const future = fixture(t, { foundationProbe: true });
  future.prepare({ foundationProbeOnly: true });
  assert.throws(() => future.publish({ ...options, confirmFoundationProbeSamplerReplacement: future.sourceSha }),
    /approved Probe 0.0.0/);
  assert.deepEqual(future.remoteCalls, []);
  assert.deepEqual(future.externalCalls, []);
  const otherCommit = execFileSync('git', ['-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
    'commit-tree', 'HEAD^{tree}', '-m', 'different admitted source'], { cwd: f.root, encoding: 'utf8' }).trim();
  execFileSync('git', ['update-ref', 'refs/remotes/origin/v1', otherCommit], { cwd: f.root });
  assert.throws(() => f.publish(options), /exact admitted origin\/v1 source/);
  assert.deepEqual(f.remoteCalls, []);
  assert.deepEqual(f.externalCalls, []);
});

test('sampler permanent claim blocks another source before any image or claim write', t => {
  const { f, options, native } = initialProbeFixture(t, { sampler: true });
  const ref = `refs/tags/${samplerReplacementNamespace}/claim`;
  f.refs.set(ref, { ref, object: { type: 'tag', sha: 'e'.repeat(40) } });
  assert.throws(() => f.publish({ ...options, run: native }), /already claimed/);
  assert.deepEqual(f.externalCalls, []);
  assert.ok(!f.remoteCalls.some(call => call.args.includes('POST')));
});

test('sampler replacement checks the exact current tag before claims and immediately before its sole push', t => {
  for (const concurrent of [false, true]) {
    const { f, options, native } = initialProbeFixture(t, { sampler: true });
    let reads = 0;
    assert.throws(() => f.publish({ ...options, run(bin, args) {
      if (bin === 'docker' && args[0] === 'buildx' && args[3] === probeTarget &&
          ++reads === (concurrent ? 2 : 1))
        return JSON.stringify({ schemaVersion: 2, mediaType: 'application/vnd.docker.distribution.manifest.v2+json',
          digest: `sha256:${'c'.repeat(64)}` });
      return native(bin, args);
    } }), /drifted|changed before push/);
    assert.ok(!f.externalCalls.some(call => call.args[0] === 'push'));
    if (!concurrent) assert.ok(!f.remoteCalls.some(call => call.args.includes('POST')));
  }
});

test('sampler post-push verification failure preserves irreversible digest and blocks a second push', t => {
  const { f, options, native } = initialProbeFixture(t, { sampler: true });
  assert.throws(() => f.publish({ ...options, run(bin, args) {
    if (bin === 'docker' && args[0] === 'buildx' &&
        args[3] === `ghcr.io/sabbour/agentweaver-foundation-probe@${currentProbeDigest}`)
      throw new Error('Native previous manifest read failed');
    return native(bin, args);
  } }), /Native previous manifest read failed/);
  const receipt = JSON.parse(readFileSync(path.join(f.outDir, 'publication.json'), 'utf8'));
  assert.equal(receipt.status, 'partial');
  assert.equal(receipt.samplerProbeReplacement.newDigest, `sha256:${'b'.repeat(64)}`);
  assert.equal(receipt.published[0].image, `ghcr.io/sabbour/agentweaver-foundation-probe@sha256:${'b'.repeat(64)}`);
  assert.equal(receipt.samplerProbeReplacement.originalIndexAndPlatformsRetained, undefined);
  assert.equal(f.externalCalls.filter(call => call.args[0] === 'push').length, 1);
  assert.ok(f.refs.has(`refs/tags/${samplerReplacementNamespace}/claim`));
  assert.throws(() => f.publish(options), /receipt already exists/);
});

test('sampler replacement rejects an unavailable preserved initial index before any claim or push', t => {
  const { f, options, native } = initialProbeFixture(t, { sampler: true });
  assert.throws(() => f.publish({ ...options, run(bin, args) {
    if (bin === 'docker' && args[0] === 'buildx' &&
        args[3] === `ghcr.io/sabbour/agentweaver-foundation-probe@${oldProbeDigest}`)
      return JSON.stringify({ schemaVersion: 2, digest: oldProbeDigest });
    return native(bin, args);
  } }), /preserved initial Probe index is missing/);
  assert.ok(!f.remoteCalls.some(call => call.args.includes('POST')));
  assert.ok(!f.externalCalls.some(call => call.args[0] === 'push'));
});

test('existing Linux workflow defaults off and isolates the approved sampler route from ordinary protected publication', () => {
  const workflow = readFileSync('.github/workflows/v1-release-pack.yml', 'utf8');
  assert.match(workflow, /foundation_probe_sampler_replacement:[\s\S]*?default: false/);
  assert.match(workflow, /expected_probe_digest:[\s\S]*?default: ''/);
  assert.match(workflow, /args\+=\(--foundation-probe-only\); fi/);
  assert.match(workflow, /npm run release:pack -- "\$\{args\[@\]\}"/);
  assert.match(workflow, /--confirm-foundation-probe-sampler-replacement "\$GITHUB_SHA" --expected-foundation-probe-sampler-digest "\$EXPECTED_PROBE_DIGEST"/);
  assert.match(workflow, /if: \$\{\{ inputs\.publish && inputs\.foundation_probe_sampler_replacement \}\}/);
  assert.ok(workflow.includes(currentProbeDigest));
  assert.equal((workflow.match(/fetch-depth: 0/g) ?? []).length, 4);
  const ordinary = workflow.slice(workflow.indexOf('\n  publish:'), workflow.indexOf('\n  publish-probe-sampler:'));
  const sampler = workflow.slice(workflow.indexOf('\n  publish-probe-sampler:'));
  assert.doesNotMatch(ordinary, /packages: write/);
  assert.match(ordinary, /environment: v1-publication/);
  assert.match(ordinary, /inputs\.publish && !inputs\.foundation_probe_sampler_replacement/);
  assert.match(ordinary, /RELEASE_REGISTRY_PASSWORD: \$\{\{ secrets\.RELEASE_REGISTRY_PASSWORD \}\}/);
  assert.match(sampler, /packages: write/);
  assert.match(sampler, /RELEASE_REGISTRY: ghcr\.io\/sabbour/);
  assert.match(sampler, /RELEASE_REGISTRY_USER: \$\{\{ github\.repository_owner \}\}/);
  assert.match(sampler, /RELEASE_REGISTRY_PASSWORD: \$\{\{ github\.token \}\}/);
  assert.doesNotMatch(sampler, /\benvironment:/);
  assert.doesNotMatch(sampler, /secrets\.|RELEASE_NUGET|console\.log|echo.*TOKEN/);
});

test('confirmed initial replacement updates only the approved tag and preserves old index/platforms and history', t => {
  const { f, options, native } = initialProbeFixture(t);
  const historicalRef = 'refs/tags/agentweaver-publication/old-source/claim';
  const historical = { ref: historicalRef, object: { type: 'tag', sha: 'd'.repeat(40) } };
  f.refs.set(historicalRef, historical);
  const receipt = f.publish({ ...options, run: native });
  assert.equal(receipt.status, 'published');
  assert.deepEqual(receipt.planned.map(record => [record.id, record.version, record.destination]),
    [['Agentweaver.FoundationProbe', '0.0.0', probeTarget]]);
  assert.equal(receipt.initialProbeReplacement.previousIndex.digest, oldProbeDigest);
  assert.equal(receipt.initialProbeReplacement.newDigest, `sha256:${'b'.repeat(64)}`);
  assert.equal(receipt.initialProbeReplacement.userConfirmedBaselineOverride, true);
  assert.equal(receipt.initialProbeReplacement.originalIndexAndPlatformsRetained, true);
  assert.equal(f.refs.get(historicalRef), historical);
  assert.ok(f.refs.has(`refs/tags/${replacementNamespace}/claim`));
  assert.ok(f.refs.has(`refs/tags/${replacementNamespace}/result`));
  assert.deepEqual(f.externalCalls.filter(call => call.bin === 'docker' && call.args[0] === 'push')
    .map(call => call.args), [['push', probeTarget]]);
  assert.ok(!f.externalCalls.some(call => call.bin === 'dotnet'));
  for (const digest of [oldProbeDigest, oldProbePlatform, oldProbeAttestation]) {
    assert.ok(f.externalCalls.some(call => call.args[0] === 'buildx' &&
      call.args[3] === `ghcr.io/sabbour/agentweaver-foundation-probe@${digest}`));
  }
});

test('initial replacement rejects absent confirmation, wrong source/repository/component/version without writes', t => {
  const { f, options } = initialProbeFixture(t);
  for (const change of [
    { confirmFoundationProbeInitialReplacement: undefined },
    { confirmFoundationProbeInitialReplacement: 'e'.repeat(40) },
    { foundationProbeOnly: false },
    { env: { ...options.env, RELEASE_REGISTRY: 'ghcr.io/different' } },
  ]) {
    assert.throws(() => f.publish({ ...options, ...change }), /initial Probe|initial Probe tag/);
    assert.deepEqual(f.remoteCalls, []);
    assert.deepEqual(f.externalCalls, []);
  }
  const otherCommit = execFileSync('git', ['-c', 'user.name=Test', '-c', 'user.email=test@example.invalid',
    'commit-tree', 'HEAD^{tree}', '-m', 'different admitted source'], { cwd: f.root, encoding: 'utf8' }).trim();
  execFileSync('git', ['update-ref', 'refs/remotes/origin/v1', otherCommit], { cwd: f.root });
  assert.throws(() => f.publish(options), /exact admitted origin\/v1 source/);
  assert.deepEqual(f.remoteCalls, []);
  assert.deepEqual(f.externalCalls, []);
  const future = fixture(t, { foundationProbe: true });
  future.prepare({ foundationProbeOnly: true });
  assert.throws(() => future.publish({ ...options, confirmFoundationProbeInitialReplacement: future.sourceSha }),
    /approved Probe 0.0.0/);
  assert.deepEqual(future.remoteCalls, []);
  assert.deepEqual(future.externalCalls, []);
});

test('initial replacement refuses both preflight and concurrent native tag drift before push', t => {
  for (const concurrent of [false, true]) {
    const { f, options, native } = initialProbeFixture(t);
    let reads = 0;
    assert.throws(() => f.publish({ ...options, run(bin, args) {
      if (bin === 'docker' && args[0] === 'buildx' && args[3] === probeTarget &&
          ++reads === (concurrent ? 2 : 1)) {
        return JSON.stringify({
          schemaVersion: 2, mediaType: 'application/vnd.oci.image.index.v1+json',
          digest: `sha256:${'c'.repeat(64)}`, manifests: [{ digest: oldProbePlatform }],
        });
      }
      return native(bin, args);
    } }), /drifted|changed before push/);
    assert.ok(!f.externalCalls.some(call => call.args[0] === 'push'));
    if (concurrent) {
      const receipt = JSON.parse(readFileSync(path.join(f.outDir, 'publication.json'), 'utf8'));
      assert.equal(receipt.status, 'partial');
      assert.equal(receipt.initialProbeReplacement.previousIndex.digest, oldProbeDigest);
    }
  }
});

test('a permanent initial replacement claim blocks a second source attempt before image effects', t => {
  const { f, options, native } = initialProbeFixture(t);
  const ref = `refs/tags/${replacementNamespace}/claim`;
  f.refs.set(ref, { ref, object: { type: 'tag', sha: 'e'.repeat(40) } });
  assert.throws(() => f.publish({ ...options, run: native }), /already claimed/);
  assert.deepEqual(f.externalCalls, []);
  assert.ok(!f.remoteCalls.some(call => call.args.includes('POST')));
});

test('initial replacement refuses a relabeled or wrong-user prepared image before pushing', t => {
  for (const field of ['source', 'user', 'version', 'entrypoint']) {
    const { f, options, native } = initialProbeFixture(t);
    assert.throws(() => f.publish({ ...options, run(bin, args) {
      const output = native(bin, args);
      if (bin !== 'docker' || !args.includes('{{json .Config}}')) return output;
      const config = JSON.parse(output);
      if (field === 'source') config.Labels['org.opencontainers.image.revision'] = 'a'.repeat(40);
      if (field === 'user') config.User = '0:0';
      if (field === 'version') config.Labels['org.opencontainers.image.version'] = '0.0.1';
      if (field === 'entrypoint') config.Entrypoint = ['sh'];
      return JSON.stringify(config);
    } }), /corrected source, binary baseline, and image contract/);
    assert.ok(!f.externalCalls.some(call => call.args[0] === 'push'));
    assert.equal(JSON.parse(readFileSync(path.join(f.outDir, 'publication.json'), 'utf8')).status, 'partial');
  }
});

test('old-platform retention failure records the actual new digest without claiming complete replacement', t => {
  const { f, options, native } = initialProbeFixture(t);
  assert.throws(() => f.publish({ ...options, run(bin, args) {
    if (bin === 'docker' && args[0] === 'buildx' &&
        args[3] === `ghcr.io/sabbour/agentweaver-foundation-probe@${oldProbePlatform}`) {
      throw new Error('Native old platform read failed');
    }
    return native(bin, args);
  } }), /Native old platform read failed/);
  const receipt = JSON.parse(readFileSync(path.join(f.outDir, 'publication.json'), 'utf8'));
  assert.equal(receipt.status, 'partial');
  assert.equal(receipt.initialProbeReplacement.previousIndex.digest, oldProbeDigest);
  assert.equal(receipt.initialProbeReplacement.newDigest, `sha256:${'b'.repeat(64)}`);
  assert.equal(receipt.initialProbeReplacement.originalIndexAndPlatformsRetained, undefined);
});

test('native single Docker V2 descriptors bind schema to exact raw bytes after push', t => {
  const { f, options, native } = initialProbeFixture(t);
  const raw = JSON.stringify({ schemaVersion: 2, mediaType: 'application/vnd.docker.distribution.manifest.v2+json',
    config: { digest: `sha256:${'a'.repeat(64)}`, size: 10 }, layers: [] });
  const digest = `sha256:${createHash('sha256').update(raw).digest('hex')}`;
  const receipt = f.publish({ ...options, run(bin, args) {
    if (bin === 'docker' && args.includes('--raw')) return raw;
    const output = native(bin, args);
    if (bin === 'docker' && args[0] === 'buildx' && args[3] === probeTarget &&
        JSON.parse(output).digest === `sha256:${'b'.repeat(64)}`) {
      return JSON.stringify({ mediaType: JSON.parse(raw).mediaType, digest, size: Buffer.byteLength(raw) });
    }
    if (bin === 'docker' && args[0] === 'inspect' && !args.includes('{{json .Config}}')) {
      return JSON.stringify([`ghcr.io/sabbour/agentweaver-foundation-probe@${digest}`]);
    }
    return output;
  } });
  assert.equal(receipt.status, 'published');
  assert.equal(receipt.initialProbeReplacement.newDigest, digest);
  assert.equal(receipt.initialProbeReplacement.originalIndexAndPlatformsRetained, true);
  assert.equal(f.externalCalls.filter(call => call.args[0] === 'push').length, 1);
});

test('post-push raw digest, size, schema, and media-type failures retain the actual irreversible digest', t => {
  for (const failure of ['digest', 'size', 'schema', 'media-type', 'read']) {
    const { f, options, native } = initialProbeFixture(t);
    const raw = JSON.stringify({ schemaVersion: failure === 'schema' ? 1 : 2,
      mediaType: 'application/vnd.docker.distribution.manifest.v2+json', layers: [] });
    const digest = `sha256:${createHash('sha256').update(raw).digest('hex')}`;
    assert.throws(() => f.publish({ ...options, run(bin, args) {
      if (bin === 'docker' && args.includes('--raw')) {
        if (failure === 'read') throw new Error('Native post-push raw read failed');
        return failure === 'digest' ? `${raw}\n` : raw;
      }
      const output = native(bin, args);
      if (bin === 'docker' && args[0] === 'buildx' && args[3] === probeTarget &&
          JSON.parse(output).digest === `sha256:${'b'.repeat(64)}`) {
        return JSON.stringify({ mediaType: failure === 'media-type' ? 'foreign' : JSON.parse(raw).mediaType,
          digest, size: Buffer.byteLength(raw) + (failure === 'size' ? 1 : 0) });
      }
      if (bin === 'docker' && args[0] === 'inspect' && !args.includes('{{json .Config}}')) {
        return JSON.stringify([`ghcr.io/sabbour/agentweaver-foundation-probe@${digest}`]);
      }
      return output;
    } }), /raw manifest|Native post-push raw read failed/);
    const receipt = JSON.parse(readFileSync(path.join(f.outDir, 'publication.json'), 'utf8'));
    assert.equal(receipt.status, 'partial');
    assert.equal(receipt.initialProbeReplacement.newDigest, digest);
    assert.equal(receipt.initialProbeReplacement.originalIndexAndPlatformsRetained, undefined);
    assert.equal(f.externalCalls.filter(call => call.args[0] === 'push').length, 1);
    assert.ok(f.refs.has(`refs/tags/${replacementNamespace}/claim`));
    assert.throws(() => f.publish(options), /receipt already exists/);
  }
});

test('manual publication records actual immutable registry digests and never changes draft composition', (t) => {
  const f = fixture(t);
  f.prepare();
  const before = readFileSync(path.join(f.root, 'releases', 'foundation.json'));
  const receipt = f.publish();
  assert.equal(receipt.status, 'published');
  assert.equal(receipt.sourceSha, f.sourceSha);
  assert.equal(receipt.published[1].image, `registry.example.invalid/svc@sha256:${'b'.repeat(64)}`);
  assert.equal(receipt.planned[1].destination, 'registry.example.invalid/svc:0.1.0');
  assert.equal(f.externalCalls.find(({ bin, args }) => bin === 'docker' && args[0] === 'login').args[1], 'registry.example.invalid');
  assert.deepEqual(readFileSync(path.join(f.root, 'releases', 'foundation.json')), before);
  const text = readFileSync(path.join(f.outDir, 'publication.json'), 'utf8');
  assert.doesNotMatch(text, /test-secret|test-password|test-user|test-github-secret/);
  const claim = [...f.tags.values()].find(({ tag }) => tag.endsWith('/claim'));
  const result = [...f.tags.values()].find(({ tag }) => tag.endsWith('/result'));
  const claimRecord = JSON.parse(claim.message);
  assert.equal(claimRecord.status, 'claimed');
  assert.equal(claimRecord.sourceSha, f.sourceSha);
  assert.equal(claimRecord.provenanceSha256, receipt.provenanceSha256);
  assert.deepEqual(claimRecord.planned, receipt.planned);
  assert.equal(receipt.claimSha, claim.sha);
  assert.deepEqual(JSON.parse(result.message), receipt);
  assert.doesNotMatch(JSON.stringify([...f.tags.values()]), /test-secret|test-password|test-user|test-github-secret/);
  assert.throws(() => f.publish(), /receipt already exists/);
});

test('GHCR namespace scopes image paths while Docker login uses only the registry host', (t) => {
  const f = fixture(t);
  f.prepare();
  const registry = 'ghcr.io/sabbour';
  const receipt = f.publish({ env: { ...f.env, RELEASE_REGISTRY: registry } });
  assert.equal(receipt.status, 'published');
  assert.equal(receipt.planned[1].destination, `${registry}/svc:0.1.0`);
  assert.equal(receipt.published[1].image, `${registry}/svc@sha256:${'b'.repeat(64)}`);
  assert.equal(f.externalCalls.find(({ bin, args }) => bin === 'docker' && args[0] === 'login').args[1], 'ghcr.io');
  assert.ok(f.externalCalls.some(({ bin, args }) => bin === 'docker' && args[0] === 'push' && args[1] === `${registry}/svc:0.1.0`));
});

for (const registry of [
  'GHCR.io/sabbour',
  'ghcr.io/sabbour/',
  'ghcr.io//sabbour',
  'ghcr.io/sabbour/..',
  'ghcr.io/sabbour?token=value',
  'ghcr.io/sabbour#tag',
  'https://ghcr.io/sabbour',
  'ghcr.io\\sabbour',
  'ghcr.io/%2e%2e/other',
  'user@ghcr.io/sabbour',
  'ghcr.io:65536/sabbour',
  'ghcr..io/sabbour',
]) {
  test(`rejects malformed registry destination ${JSON.stringify(registry)} before publication`, (t) => {
    const f = fixture(t);
    f.prepare();
    assert.throws(() => f.publish({ env: { ...f.env, RELEASE_REGISTRY: registry } }),
      /normalized registry host with an optional lowercase repository path/);
    assert.deepEqual(f.remoteCalls, []);
    assert.deepEqual(f.externalCalls, []);
  });
}

test('rejects an overlong complete image repository path before publication', (t) => {
  const f = fixture(t);
  f.prepare();
  assert.throws(() => f.publish({ env: { ...f.env, RELEASE_REGISTRY: `ghcr.io/${'a'.repeat(252)}` } }),
    /image repository path for Svc exceeds 255 characters/);
  assert.deepEqual(f.remoteCalls, []);
  assert.deepEqual(f.externalCalls, []);
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

for (const priorStatus of ['claimed', 'partial', 'published', 'malformed', 'result-without-claim']) {
  test(`a durable ${priorStatus} record blocks a fresh workspace/redispatch before external commands`, (t) => {
    const f = fixture(t);
    f.prepare();
    const suffix = priorStatus === 'result-without-claim' ? 'result' : 'claim';
    const ref = `refs/tags/agentweaver-publication/${f.sourceSha}/${suffix}`;
    f.refs.set(ref, { ref, object: { type: 'tag', sha: 'c'.repeat(40) }, status: priorStatus });
    assert.equal(existsSync(path.join(f.outDir, 'publication.json')), false);
    const calls = [];
    assert.throws(() => f.publish({ run: (...args) => calls.push(args) }), /claim\/result already exists or is ambiguous/);
    assert.deepEqual(calls, []);
  });
}

test('full workflow retry cannot republish when only the permanent claim survives', (t) => {
  const f = fixture(t);
  f.prepare();
  f.publish();
  const fresh = path.join(f.root, 'artifacts', 'fresh-pack');
  // A redispatch/full rerun has a new artifact and no previous local publication.json.
  mkdirSync(fresh, { recursive: true });
  for (const file of ['provenance.json', 'Pkg.0.1.0.nupkg', 'Svc.0.1.0.tar.gz']) {
    writeFileSync(path.join(fresh, file), readFileSync(path.join(f.outDir, file)));
  }
  const resultRef = `refs/tags/agentweaver-publication/${f.sourceSha}/result`;
  f.refs.delete(resultRef);
  const calls = [];
  assert.throws(() => publishArtifacts('releases/foundation.json', fresh, f.sourceSha, {
    root: f.root, confirmed: true, env: f.env,
    run: (bin, args, input) => bin === 'gh' ? f.github(args, input) : calls.push([bin, args]),
  }), /claim\/result already exists or is ambiguous/);
  assert.deepEqual(calls, []);
});

test('atomic duplicate-claim race loses before any external operation and never retries the ref', (t) => {
  const f = fixture(t);
  f.prepare();
  const calls = [];
  assert.throws(() => f.publish({ run: (...args) => calls.push(args), github: (args, input) => {
    if (args[3].endsWith('/git/refs') && JSON.parse(input).ref.endsWith('/claim')) {
      f.github(args, input); // A concurrent publisher wins this exact source claim first.
    }
    return f.github(args, input);
  } }), /atomic race lost/);
  assert.deepEqual(calls, []);
  assert.equal([...f.refs.keys()].filter((ref) => ref.endsWith('/claim')).length, 1);
  assert.equal(existsSync(path.join(f.outDir, 'publication.json')), false);
});

for (const failure of ['query-error', 'query-malformed', 'tag-source', 'tag-message', 'tag-missing', 'ref-object', 'ref-missing']) {
  test(`durable claim ${failure} fails closed before external operations`, (t) => {
    const f = fixture(t);
    f.prepare();
    const calls = [];
    assert.throws(() => f.publish({ run: (...args) => calls.push(args), github: (args, input) => {
      if (args[args.indexOf('--method') + 1] === 'GET') {
        if (failure === 'query-error') throw new Error('GitHub unavailable');
        if (failure === 'query-malformed') return '{}';
      }
      const result = JSON.parse(f.github(args, input));
      if (args[3].endsWith('/git/tags')) {
        if (failure === 'tag-source') result.object.sha = 'f'.repeat(40);
        if (failure === 'tag-message') result.message = '{}';
        if (failure === 'tag-missing') return '{}';
      }
      if (args[3].endsWith('/git/refs')) {
        if (failure === 'ref-object') result.object.sha = 'f'.repeat(40);
        if (failure === 'ref-missing') return '{}';
      }
      return JSON.stringify(result);
    } }), /GitHub unavailable|claim\/result already exists|does not match/);
    assert.deepEqual(calls, []);
  });
}

for (const externalFailure of [false, true]) {
  test(`terminal receipt failure preserves permanent claim${externalFailure ? ' and original publication failure' : ''}`, (t) => {
    const f = fixture(t, { service: false });
    f.prepare();
    assert.throws(() => f.publish({
      run: () => { if (externalFailure) throw new Error('package publication failed'); return ''; },
      github: (args, input) => {
        if (args[3].endsWith('/git/refs') && JSON.parse(input).ref.endsWith('/result')) {
          throw new Error('terminal ref persistence failed');
        }
        return f.github(args, input);
      },
    }), (error) => {
      assert.ok(error instanceof AggregateError);
      assert.match(error.message, /permanent claim blocks retry/);
      if (externalFailure) assert.match(error.errors[0].message, /package publication failed/);
      assert.match(error.errors.at(-1).message, /terminal ref persistence failed/);
      return true;
    });
    const receipt = JSON.parse(readFileSync(path.join(f.outDir, 'publication.json'), 'utf8'));
    assert.equal(receipt.status, externalFailure ? 'partial' : 'published');
    assert.ok(f.refs.has(`refs/tags/agentweaver-publication/${f.sourceSha}/claim`));
    assert.equal(f.refs.has(`refs/tags/agentweaver-publication/${f.sourceSha}/result`), false);
  });
}

test('publish alone has write permission and its token never goes into pack or Git source', () => {
  const workflow = readFileSync('.github/workflows/v1-release-pack.yml', 'utf8').replaceAll('\r\n', '\n');
  const [pack, publish] = workflow.split('\n  publish:');
  assert.match(pack, /permissions:\n  contents: read/);
  assert.doesNotMatch(pack, /contents: write|GH_TOKEN/);
  assert.match(publish, /permissions:\n      contents: write/);
  assert.match(publish, /GH_TOKEN: \$\{\{ github.token \}\}/);
});

test('local receipt failure surfaces after a durable result and cannot permit automatic repeat', (t) => {
  const f = fixture(t, { service: false });
  f.prepare();
  assert.throws(() => f.publish({ writeFileSync: () => { throw new Error('local receipt disk failure'); } }),
    (error) => error instanceof AggregateError && /local receipt disk failure/.test(error.message));
  const result = [...f.tags.values()].find(({ tag }) => tag.endsWith('/result'));
  assert.equal(JSON.parse(result.message).status, 'published');
  assert.equal(existsSync(path.join(f.outDir, 'publication.json')), false);
  assert.throws(() => f.publish(), /claim\/result already exists or is ambiguous/);
});

test('a feed fragment cannot leak a token into durable destination records', (t) => {
  const f = fixture(t, { service: false });
  f.prepare();
  const calls = [];
  assert.throws(() => f.publish({
    env: { ...f.env, RELEASE_NUGET_SOURCE: 'https://feed.example.invalid/#test-secret' },
    run: (...args) => calls.push(args),
  }), /credential-free HTTPS feed/);
  assert.deepEqual(calls, []);
  assert.deepEqual(f.remoteCalls, []);
});
