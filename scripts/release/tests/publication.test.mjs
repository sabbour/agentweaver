import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
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
  assert.equal((workflow.match(/name: v1-release-pack-\$\{\{ github.sha \}\}-\$\{\{ github.run_id \}\}/g) ?? []).length, 2);
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
