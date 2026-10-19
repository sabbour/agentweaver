import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  HELPER_ADMISSION_PULL_FIELDS,
  continuePartialPublication,
  isRegistryAbsent,
  originalBinding,
  readPackageBytes,
  readRemoteImage,
  verifyHelperAdmission,
  verifyPartialPublicationCompletion,
  verifyRegistryWriteAccess,
} from '../partial-publication.mjs';
import { PARTIAL_PUBLICATION as P } from '../partial-publication-contract.mjs';
import { readDockerArchiveConfig } from '../docker-archive.mjs';
import { measureAgentHostArchiveSync } from '../measure-agenthost-image.mjs';
import {
  AGENT_SANDBOX_RELEASE,
  deployRelease,
  validatePublicationReceipt,
} from '../deploy.mjs';
import { WEB_LOCK_PATH } from '../validate.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const fixture = path.join(root, 'scripts', 'release', 'tests', 'fixtures', 'partial-publication', 'original-publication.json');
const originalBytes = readFileSync(fixture);
const original = JSON.parse(originalBytes.toString('utf8'));
const sha = value => createHash('sha256').update(value).digest('hex');
const digest = value => `sha256:${sha(value)}`;
const HELPER = 'a'.repeat(40);
const RUN_ID = '40000000001';
// Keep the deployment smoke test offline while verifying the pinned manifest dry-run input.
const agentSandboxTestManifest = [
  'apiVersion: v1',
  'kind: Namespace',
  'metadata:',
  '  name: agent-sandbox-system',
].join('\n');
const agentSandboxTestRelease = Object.freeze({
  ...AGENT_SANDBOX_RELEASE,
  manifestUrl: 'https://example.test/agent-sandbox.yaml',
  manifestSha256: sha(agentSandboxTestManifest),
});

function newWorld(overrides = {}) {
  const tags = new Map();
  const refs = new Map();
  const { claimSha, status, published, ...claimFields } = original;
  const namespace = `agentweaver-publication/${P.sourceSha}`;
  const addTag = (name, objectSha, message) => {
    tags.set(objectSha, { sha: objectSha, tag: name, message, object: { type: 'commit', sha: P.sourceSha } });
    refs.set(`refs/tags/${name}`, { ref: `refs/tags/${name}`, object: { type: 'tag', sha: objectSha } });
  };
  addTag(`${namespace}/claim`, P.claimSha, JSON.stringify({ ...claimFields, status: 'claimed', published: [] }));
  addTag(`${namespace}/result`, P.resultSha, JSON.stringify(original));

  const images = new Map();
  const packages = new Map();
  for (const record of original.published) {
    if (record.kind === 'package') packages.set(record.id, record.sha256);
    else {
      const [repository, imageDigest] = record.image.split('@');
      images.set(`${repository}:${record.version}`, {
        digest: imageDigest, platformDigest: imageDigest, configDigest: record.imageConfigDigest, layerCount: 3,
      });
    }
  }
  const artifacts = {
    [P.packArtifactId]: { name: P.packArtifactName, size: P.packArtifactSize, digest: P.packArtifactDigest },
    [P.receiptArtifactId]: { name: P.receiptArtifactName, size: P.receiptSize, digest: P.receiptArtifactDigest },
  };
  const world = {
    tags, refs, images, packages, calls: [], tagCounter: 0,
    artifacts,
    runs: {
      [P.runId]: {
        id: Number(P.runId), path: P.originalWorkflowPath, head_sha: P.sourceSha, head_branch: 'v1',
        event: 'workflow_dispatch', run_attempt: 1, status: 'completed', conclusion: 'failure',
      },
      [RUN_ID]: {
        id: Number(RUN_ID), path: P.workflowPath, head_sha: HELPER, head_branch: 'v1',
        event: 'workflow_dispatch', run_attempt: 1, status: 'completed', conclusion: 'success',
      },
    },
    pulls: [{ state: 'closed', merged_at: '2026-10-20T00:00:00Z', merge_commit_sha: HELPER,
      base: { ref: 'v1', repo: { full_name: P.repository } } }],
    failPush: undefined,
    failRefCreate: undefined,
    pushReadback: undefined,
    ...overrides,
  };
  world.run = (bin, args, input) => {
    world.calls.push([bin, ...args].join(' '));
    if (bin === 'gh') return JSON.stringify(gh(world, args, input));
    if (bin === 'docker') return docker(world, args);
    if (bin === 'dotnet') {
      const id = path.basename(args[2]);
      if (world.failPush === id) throw new Error(`push failed for ${id}`);
      world.packages.set(id, world.selected.find(item => item.component.id === id).artifact.sha256);
      return '';
    }
    throw new Error(`unexpected ${bin}`);
  };
  world.selected = original.planned.map(entry => ({
    component: { id: entry.id, version: entry.version },
    artifact: { kind: entry.kind, sha256: entry.sha256 },
    file: `/pack/${entry.id}`,
    ...(entry.kind === 'image' ? { localImageReference: `local/${entry.id.toLowerCase()}:${entry.version}` } : {}),
    ...(entry.id === 'Agentweaver.AgentHost' ? { agentHost: {} } : {}),
  }));
  world.imageRepositories = new Map(original.planned.filter(entry => entry.kind === 'image')
    .map(entry => [entry.id, entry.destination.slice(0, -`:${entry.version}`.length)]));
  return world;
}

function gh(world, args, input) {
  const method = args[args.indexOf('--method') + 1];
  const endpoint = args[args.indexOf('--method') + 2].slice(`repos/${P.repository}/`.length);
  let match;
  if (method === 'GET' && (match = /^actions\/artifacts\/(\d+)$/.exec(endpoint))) {
    const artifact = world.artifacts[match[1]];
    return {
      id: Number(match[1]), name: artifact.name, size_in_bytes: artifact.size, expired: false, digest: artifact.digest,
      workflow_run: { id: Number(P.runId), head_sha: P.sourceSha, head_branch: 'v1' },
    };
  }
  if (method === 'GET' && (match = /^actions\/runs\/(\d+)$/.exec(endpoint))) return world.runs[match[1]] ?? {};
  if (method === 'GET' && (match = /^git\/ref\/tags\/(.+)$/.exec(endpoint))) {
    return world.refs.get(`refs/tags/${decodeURIComponent(match[1])}`) ?? {};
  }
  if (method === 'GET' && (match = /^git\/tags\/([a-f0-9]{40})$/.exec(endpoint))) return world.tags.get(match[1]) ?? {};
  if (method === 'GET' && (match = /^git\/matching-refs\/tags\/(.+)$/.exec(endpoint))) {
    return [...world.refs.values()].filter(ref => ref.ref.startsWith(`refs/tags/${match[1]}`));
  }
  if (method === 'GET' && endpoint.startsWith('commits/') && endpoint.endsWith('/pulls')) return [];
  if (method === 'GET' && endpoint.startsWith('pulls?')) {
    const query = new URLSearchParams(endpoint.slice('pulls?'.length));
    assert.equal(query.get('state'), 'closed');
    assert.equal(query.get('base'), 'v1');
    assert.equal(query.get('per_page'), '100');
    const start = (Number(query.get('page')) - 1) * 100;
    // Helper admission must project each page inside gh; mirror that projection here.
    assert.equal(args[args.indexOf('--jq') + 1], HELPER_ADMISSION_PULL_FIELDS);
    return world.pulls.slice(start, start + 100).map(pull => ({
      state: pull?.state ?? null, merged_at: pull?.merged_at ?? null, merge_commit_sha: pull?.merge_commit_sha ?? null,
      base: { ref: pull?.base?.ref ?? null, repo: { full_name: pull?.base?.repo?.full_name ?? null } },
    }));
  }
  const body = JSON.parse(input);
  if (method === 'POST' && endpoint === 'git/tags') {
    const objectSha = createHash('sha1').update(`${world.tagCounter++}`).digest('hex');
    world.tags.set(objectSha, { sha: objectSha, tag: body.tag, message: body.message, object: { type: body.type, sha: body.object } });
    return world.tags.get(objectSha);
  }
  if (method === 'POST' && endpoint === 'git/refs') {
    if (world.refs.has(body.ref) || world.failRefCreate?.(body.ref)) throw new Error('Reference already exists');
    world.refs.set(body.ref, { ref: body.ref, object: { type: 'tag', sha: body.sha } });
    return world.refs.get(body.ref);
  }
  throw new Error(`unexpected gh ${method} ${endpoint}`);
}

function preparedConfig(id) {
  const version = original.planned.find(entry => entry.id === id).version;
  return {
    configDigest: digest(`config:${id}`),
    config: {
      os: 'linux', architecture: 'amd64',
      config: { Labels: { 'org.opencontainers.image.revision': P.sourceSha, 'org.opencontainers.image.version': version } },
      rootfs: { diff_ids: [digest(`layer1:${id}`), digest(`layer2:${id}`)] },
    },
  };
}

function writeDockerArchive(archive, imageReference, config) {
  const directory = mkdtempSync(path.join(tmpdir(), 'partial-publication-archive-'));
  try {
    const layerDirectory = path.join(directory, 'layer');
    const layerArchive = path.join(directory, 'layer.tar');
    mkdirSync(layerDirectory);
    writeFileSync(path.join(layerDirectory, 'layer.txt'), 'synthetic image layer');
    execFileSync('tar', ['-cf', layerArchive, 'layer.txt'], { cwd: layerDirectory });
    const layerBytes = readFileSync(layerArchive);
    const layerDigest = `sha256:${sha(layerBytes)}`;
    config.rootfs.diff_ids = [layerDigest];
    const layerPath = `${layerDigest.slice(7)}/layer.tar`;
    mkdirSync(path.dirname(path.join(directory, layerPath)), { recursive: true });
    writeFileSync(path.join(directory, layerPath), layerBytes);
    const configBytes = Buffer.from(JSON.stringify(config));
    const configPath = `${sha(configBytes)}.json`;
    writeFileSync(path.join(directory, 'manifest.json'), JSON.stringify([{
      Config: configPath, RepoTags: [imageReference], Layers: [layerPath],
    }]));
    writeFileSync(path.join(directory, configPath), configBytes);
    execFileSync('tar', ['-czf', archive, 'manifest.json', configPath, layerPath], { cwd: directory });
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

const idForReference = (world, reference) =>
  world.selected.find(item => item.localImageReference === reference)?.component.id;

function docker(world, args) {
  if (args[0] === 'login' || args[0] === 'load' || args[0] === 'tag') return '';
  if (args[0] === 'image' && args[1] === 'inspect') {
    const id = idForReference(world, args.at(-1));
    const { config } = preparedConfig(id);
    return JSON.stringify({
      Os: 'linux', Architecture: 'amd64', Config: { Labels: config.config.Labels }, RootFS: { Layers: config.rootfs.diff_ids },
    });
  }
  if (args[0] === 'push') {
    const remote = args[1];
    const id = [...world.imageRepositories].find(([, repository]) => remote.startsWith(`${repository}:`))[0];
    if (world.failPush === id) throw new Error(`push failed for ${id}`);
    const pushed = { digest: digest(`manifest:${remote}`), platformDigest: digest(`manifest:${remote}`),
      configDigest: preparedConfig(id).configDigest, layerCount: 2 };
    world.images.set(remote, world.pushReadback?.(id, pushed) ?? pushed);
    return `1.0.0: digest: ${pushed.digest} size: 1234`;
  }
  throw new Error(`unexpected docker ${args.join(' ')}`);
}

function options(world, directory, overrides = {}) {
  const receiptPath = path.join(directory, 'original.json');
  if (!existsSync(receiptPath)) writeFileSync(receiptPath, overrides.originalReceiptBytes ?? originalBytes);
  return {
    sourceSha: P.sourceSha,
    helperSha: HELPER,
    selected: world.selected,
    imageRepositories: world.imageRepositories,
    env: {
      GH_TOKEN: 'token', GITHUB_REPOSITORY: P.repository, RELEASE_REGISTRY: P.registry,
      RELEASE_NUGET_SOURCE: P.feed, RELEASE_NUGET_USERNAME: 'sabbour', RELEASE_NUGET_API_KEY: 'key',
      RELEASE_REGISTRY_USER: 'sabbour', RELEASE_REGISTRY_PASSWORD: 'secret',
    },
    run: world.run,
    readArchiveConfig: (file, reference) => {
      const id = idForReference(world, reference);
      assert.equal(file, `/pack/${id}`);
      return preparedConfig(id);
    },
    readPackageSha256: async component => world.packages.get(component.id) ?? null,
    readImage: async (repository, tag) => world.images.get(`${repository}:${tag}`) ?? null,
    verifyRegistryWrite: async (repositories, credential, fetchImpl) => {
      world.calls.push(`registry-write-check ${repositories.join(' ')}`);
      if (world.registryWriteFetch) await verifyRegistryWriteAccess(repositories, credential, world.registryWriteFetch);
    },
    sleep: async () => {},
    directory,
    originalReceiptPath: receiptPath,
    originalReceiptBytes: originalBytes,
    originalReceipt: original,
    provenanceSha256: P.provenanceSha256,
    runId: RUN_ID,
    runAttempt: '1',
    ...overrides,
  };
}

function withDirectory(fn) {
  return async () => {
    const directory = mkdtempSync(path.join(tmpdir(), 'partial-publication-test-'));
    try {
      await fn(directory);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  };
}

const continuationRefs = world => [...world.refs.keys()].filter(ref => ref.startsWith(`refs/tags/${P.namespace}/`));
const mutations = world => world.calls.filter(call => / push |^gh api --method POST /.test(call));

test('the fixture is the exact retained original partial receipt', () => {
  assert.equal(sha(originalBytes), P.receiptSha256);
  assert.equal(original.status, 'partial');
  assert.deepEqual(original.published.map(({ id }) => id), [...P.publishedIds]);
});

test('without approval history, continuation publishes only the eleven missing artifacts and completion verification accepts its proof', withDirectory(async directory => {
  const world = newWorld();
  const receipt = await continuePartialPublication(options(world, directory));
  assert.equal(receipt.status, 'published');
  assert.equal(receipt.published.length, 23);
  assert.deepEqual(receipt.published.slice(0, 12), original.published);
  assert.deepEqual(receipt.published.slice(12).map(({ id }) => id), [...P.missingIds]);
  for (const record of receipt.published.slice(12).filter(record => record.kind === 'image')) {
    assert.deepEqual(Object.keys(record).sort(), ['archiveSha256', 'configDigest', 'id', 'image', 'kind', 'version']);
    assert.equal(record.configDigest, preparedConfig(record.id).configDigest);
  }
  assert.deepEqual(receipt.continuation.original, originalBinding());
  assert.equal(receipt.continuation.originalReceiptText, originalBytes.toString('utf8'));
  const publication = JSON.parse(readFileSync(path.join(directory, 'publication.json'), 'utf8'));
  assert.deepEqual(JSON.parse(readFileSync(path.join(directory, 'continuation.json'), 'utf8')), publication);
  assert.deepEqual(continuationRefs(world), [`refs/tags/${P.namespace}/claim`, `refs/tags/${P.namespace}/result`]);
  const pushes = world.calls.filter(call => / push /.test(call));
  assert.equal(pushes.length, 11);
  assert.ok(!pushes.some(call => P.publishedIds.some(id => call.includes(`/pack/${id} `))));
  assert.equal(verifyPartialPublicationCompletion(publication, publication, P.sourceSha, world.run), publication);
  assert.ok(!world.calls.some(call => call.includes('/approvals')));

  rmSync(path.join(directory, 'publication.json'));
  rmSync(path.join(directory, 'continuation.json'));
  await assert.rejects(continuePartialPublication(options(world, directory, { runId: '40000000002' })),
    /recovery is consumed/);
}));

test('continuation accepts an SDK image archive without a revision label but rejects conflicting archive and loaded labels', withDirectory(async directory => {
  const world = newWorld();
  const item = world.selected.find(value => value.component.id === 'Agentweaver.Identity.Broker');
  const reference = item.localImageReference;
  const version = item.component.version;
  const labels = { 'org.opencontainers.image.version': version };
  const config = {
    architecture: 'amd64',
    config: { Labels: labels },
    os: 'linux',
    rootfs: { type: 'layers', diff_ids: [] },
  };
  const archive = path.join(directory, 'broker.tar.gz');
  writeDockerArchive(archive, reference, config);
  const diffIds = config.rootfs.diff_ids;
  item.file = archive;
  const readArchiveConfig = (file, imageReference) => file === archive
    ? readDockerArchiveConfig(file, imageReference)
    : preparedConfig(idForReference(world, imageReference));
  const brokerRemote = `${world.imageRepositories.get(item.component.id)}:${version}`;
  const originalRun = world.run;
  world.run = (bin, args, input) => {
    if (bin === 'docker' && args[0] === 'image' && args[1] === 'inspect' && args.at(-1) === reference) {
      return JSON.stringify({
        Os: 'linux', Architecture: 'amd64', Config: { Labels: labels }, RootFS: { Layers: diffIds },
      });
    }
    if (bin === 'docker' && args[0] === 'push' && args[1] === brokerRemote) {
      const output = originalRun(bin, args, input);
      world.images.set(brokerRemote, {
        ...world.images.get(brokerRemote),
        configDigest: readDockerArchiveConfig(archive, reference).configDigest,
        layerCount: diffIds.length,
      });
      return output;
    }
    return originalRun(bin, args, input);
  };

  const receipt = await continuePartialPublication(options(world, directory, { readArchiveConfig }));
  assert.equal(receipt.status, 'published');
  assert.equal(receipt.published.find(value => value.id === item.component.id).configDigest,
    readDockerArchiveConfig(archive, reference).configDigest);

  const conflictingWorld = newWorld();
  const conflictingDirectory = path.join(directory, 'conflicting');
  mkdirSync(conflictingDirectory);
  const conflictingRead = (file, imageReference) => {
    const value = preparedConfig(idForReference(conflictingWorld, imageReference));
    value.config.config.Labels['org.opencontainers.image.revision'] = 'f'.repeat(40);
    return value;
  };
  await assert.rejects(
    continuePartialPublication(options(conflictingWorld, conflictingDirectory, { readArchiveConfig: conflictingRead })),
    /prepared Agentweaver.Identity.Broker archive is not the exact source-bound linux\/amd64 image/,
  );

  const loadedConflictWorld = newWorld();
  const loadedConflictDirectory = path.join(directory, 'loaded-conflicting');
  mkdirSync(loadedConflictDirectory);
  loadedConflictWorld.selected.find(value => value.component.id === item.component.id).file = archive;
  const loadedConflictRun = loadedConflictWorld.run;
  loadedConflictWorld.run = (bin, args, input) => {
    if (bin === 'docker' && args[0] === 'image' && args[1] === 'inspect' && args.at(-1) === reference) {
      return JSON.stringify({
        Os: 'linux',
        Architecture: 'amd64',
        Config: { Labels: { ...labels, 'org.opencontainers.image.revision': 'f'.repeat(40) } },
        RootFS: { Layers: diffIds },
      });
    }
    return loadedConflictRun(bin, args, input);
  };
  await assert.rejects(
    continuePartialPublication(options(loadedConflictWorld, loadedConflictDirectory, {
      readArchiveConfig: (file, imageReference) => file === archive
        ? readDockerArchiveConfig(file, imageReference)
        : preparedConfig(idForReference(loadedConflictWorld, imageReference)),
    })),
    /loaded Agentweaver.Identity.Broker image does not match its archive layers, source, version, identity, or platform/,
  );
  assert.deepEqual(mutations(loadedConflictWorld), []);
}));

const preflightRejections = [
  ['the original run id', { runId: P.runId }, /approved recovery/],
  ['a second attempt', { runAttempt: '2' }, /approved recovery/],
  ['the original source as helper', { helperSha: P.sourceSha }, /approved recovery/],
  ['a different product source', { sourceSha: 'b'.repeat(40) }, /approved recovery/],
  ['changed provenance', { provenanceSha256: 'c'.repeat(64) }, /provenance/],
  ['changed receipt bytes', { originalReceiptBytes: Buffer.concat([originalBytes, Buffer.from(' ')]) }, /receipt bytes/],
];
for (const [name, override, pattern] of preflightRejections) {
  test(`preflight rejects ${name} before any mutation`, withDirectory(async directory => {
    const world = newWorld();
    await assert.rejects(continuePartialPublication(options(world, directory, override)), pattern);
    assert.deepEqual(mutations(world), []);
  }));
}

const worldRejections = [
  ['changed pack artifact digest', world => { world.artifacts[P.packArtifactId].digest = digest('other'); }, /Actions artifact/],
  ['changed receipt artifact size', world => { world.artifacts[P.receiptArtifactId].size += 1; }, /Actions artifact/],
  ['a changed image destination', world => { world.imageRepositories.set('Agentweaver.Gateway', `${P.registry}/other`); }, /original plan/],
  ['a changed prepared artifact hash', world => { world.selected[13].artifact.sha256 = 'd'.repeat(64); }, /original plan/],
  ['a changed original result record', world => {
    world.tags.get(P.resultSha).message = JSON.stringify({ ...original, status: 'published' });
  }, /original immutable result/],
  ['a changed original claim record', world => {
    const claim = JSON.parse(world.tags.get(P.claimSha).message);
    world.tags.get(P.claimSha).message = JSON.stringify({ ...claim, planned: claim.planned.slice(1) });
  }, /original immutable claim/],
  ['a changed original run', world => { world.runs[P.runId].run_attempt = 2; }, /original release workflow run/],
  ['a changed previously published package', world => { world.packages.set('Agentweaver.Identity', 'e'.repeat(64)); }, /previously published Agentweaver.Identity/],
  ['a missing previously published image', world => { world.images.clear(); }, /previously published Agentweaver.AgentHost/],
  ['a missing output that is already present', world => {
    world.packages.set('Agentweaver.Providers.Storage.AzureFiles', 'f'.repeat(64));
  }, /already present; refusing overwrite/],
  ['an existing continuation claim', world => {
    world.refs.set(`refs/tags/${P.namespace}/claim`, { ref: `refs/tags/${P.namespace}/claim`, object: { type: 'tag', sha: '1'.repeat(40) } });
  }, /recovery is consumed/],
  ['an unmerged helper', world => { world.pulls = [{ merged_at: null, base: { ref: 'v1', repo: { full_name: P.repository } } }]; }, /merged into v1/],
  ['a helper merged elsewhere', world => { world.pulls[0].base.ref = 'main'; }, /merged into v1/],
  ['a helper merged in another repository', world => { world.pulls[0].base.repo.full_name = 'elsewhere/agentweaver'; }, /merged into v1/],
  ['another merged helper SHA', world => { world.pulls[0].merge_commit_sha = 'b'.repeat(40); }, /merged into v1/],
  ['an open helper PR', world => { world.pulls[0].state = 'open'; }, /merged into v1/],
  ['a loaded image with different layers', world => {
    const base = world.run;
    world.run = (bin, args, input) => bin === 'docker' && args[0] === 'image'
      ? JSON.stringify({ ...JSON.parse(base(bin, args, input)), RootFS: { Layers: [digest('other')] } })
      : base(bin, args, input);
  }, /loaded Agentweaver.Identity.Broker image/],
];
for (const [name, mutate, pattern] of worldRejections) {
  test(`preflight rejects ${name} before any claim or push`, withDirectory(async directory => {
    const world = newWorld();
    mutate(world);
    const refsBefore = continuationRefs(world);
    await assert.rejects(continuePartialPublication(options(world, directory)), pattern);
    assert.deepEqual(mutations(world), []);
    assert.deepEqual(continuationRefs(world), refsBefore);
  }));
}

test('a failed push keeps the continuation consumed, records partial state, and cannot deploy', withDirectory(async directory => {
  const world = newWorld({ failPush: 'Agentweaver.Gateway' });
  await assert.rejects(continuePartialPublication(options(world, directory)), /push failed for Agentweaver.Gateway/);
  assert.equal(existsSync(path.join(directory, 'publication.json')), false);
  const partial = JSON.parse(readFileSync(path.join(directory, 'continuation.json'), 'utf8'));
  assert.equal(partial.status, 'partial');
  assert.deepEqual(partial.unknown, ['Agentweaver.Gateway']);
  assert.deepEqual(partial.unattempted, P.missingIds.slice(3));
  assert.deepEqual(partial.published.slice(12).map(({ id }) => id), P.missingIds.slice(0, 2));
  assert.deepEqual(continuationRefs(world), [`refs/tags/${P.namespace}/claim`, `refs/tags/${P.namespace}/result`]);
  assert.throws(() => verifyPartialPublicationCompletion(partial, partial, P.sourceSha, world.run), /partial/);
  rmSync(path.join(directory, 'continuation.json'));
  await assert.rejects(continuePartialPublication(options(world, directory)), /recovery is consumed/);
}));

test('a pushed image that reads back with another config is partial and unknown', withDirectory(async directory => {
  const world = newWorld({
    pushReadback: (id, pushed) => id === 'Agentweaver.Mcp' ? { ...pushed, configDigest: digest('tampered') } : pushed,
  });
  await assert.rejects(continuePartialPublication(options(world, directory)), /exact pushed archive image/);
  const partial = JSON.parse(readFileSync(path.join(directory, 'continuation.json'), 'utf8'));
  assert.equal(partial.status, 'partial');
  assert.deepEqual(partial.unknown, ['Agentweaver.Mcp']);
  assert.equal(existsSync(path.join(directory, 'publication.json')), false);
}));

test('a package that never reads back after push is unknown', withDirectory(async directory => {
  const world = newWorld();
  const base = world.run;
  world.run = (bin, args, input) => bin === 'dotnet' ? '' : base(bin, args, input);
  let sleeps = 0;
  await assert.rejects(continuePartialPublication(options(world, directory, { sleep: async () => { sleeps += 1; } })),
    /confirmed byte-for-byte/);
  assert.equal(sleeps, 9);
  const partial = JSON.parse(readFileSync(path.join(directory, 'continuation.json'), 'utf8'));
  assert.deepEqual(partial.unknown, ['Agentweaver.Providers.Storage.AzureFiles']);
  assert.deepEqual(partial.unattempted, ['Agentweaver.Providers.Sandbox.AgentSandbox']);
}));

test('a claim-only failure is consumed and has no deployable proof', withDirectory(async directory => {
  const world = newWorld({ failRefCreate: ref => ref.endsWith('/result') });
  await assert.rejects(continuePartialPublication(options(world, directory)), error =>
    error instanceof AggregateError && /finalization failed/.test(error.message));
  assert.deepEqual(continuationRefs(world), [`refs/tags/${P.namespace}/claim`]);
  assert.equal(existsSync(path.join(directory, 'publication.json')), false);
  const written = JSON.parse(readFileSync(path.join(directory, 'continuation.json'), 'utf8'));
  assert.equal(written.status, 'published');
  assert.throws(() => verifyPartialPublicationCompletion(written, written, P.sourceSha, world.run),
    /exactly its one-use claim and result/);
  rmSync(path.join(directory, 'continuation.json'));
  await assert.rejects(continuePartialPublication(options(world, directory)), /recovery is consumed/);
}));

test('a pre-existing local receipt stops before any registry or GitHub access', withDirectory(async directory => {
  const world = newWorld();
  writeFileSync(path.join(directory, 'continuation.json'), '{}');
  await assert.rejects(continuePartialPublication(options(world, directory)), /local continuation or publication receipt/);
  assert.deepEqual(world.calls, []);
}));

test('completion verification rejects altered, unmerged, or unbacked proofs without approval history', withDirectory(async directory => {
  const world = newWorld();
  const completion = await continuePartialPublication(options(world, directory));
  const verify = (value, w = world) => verifyPartialPublicationCompletion(value, value, P.sourceSha, w.run);
  const clone = () => structuredClone(completion);

  assert.throws(() => verifyPartialPublicationCompletion(completion, { ...clone(), status: 'partial' }, P.sourceSha, world.run),
    /missing, partial, changed/);
  assert.throws(() => verifyPartialPublicationCompletion(completion, completion, 'b'.repeat(40), world.run), /another release/);
  const altered = clone();
  altered.published[0].sha256 = '0'.repeat(64);
  assert.throws(() => verify(altered), /original/);
  const alteredAddition = clone();
  alteredAddition.published[20].image = alteredAddition.published[20].image.replace(/@sha256:./, '@sha256:0');
  assert.throws(() => verify(alteredAddition), /immutable continuation result differs/);
  const extra = clone();
  extra.unknown = [];
  assert.throws(() => verify(extra), /missing, partial, changed/);
  const wrongRun = clone();
  wrongRun.continuation.runId = '40000000002';
  assert.throws(() => verify(wrongRun), /continuation claim does not match/);

  const unmerged = { run: (bin, args, input) => bin === 'gh' && args.some(arg => arg.includes('/pulls?')) ? '[]' : world.run(bin, args, input) };
  assert.throws(() => verify(completion, unmerged), /merged into v1/);
  world.runs[RUN_ID].conclusion = 'failure';
  assert.throws(() => verify(completion), /successful first-attempt/);
  world.runs[RUN_ID].conclusion = 'success';
  world.runs[RUN_ID].head_sha = 'b'.repeat(40);
  assert.throws(() => verify(completion), /successful first-attempt/);
}));

test('helper admission finds the exact merged result on non-default v1 without commit-associated pulls', () => {
  const world = newWorld();
  assert.deepEqual(JSON.parse(world.run('gh', [
    'api', '--method', 'GET', `repos/${P.repository}/commits/${HELPER}/pulls`,
  ])), []);
  world.calls.length = 0;
  verifyHelperAdmission(world.run, HELPER);
  assert.equal(world.calls.length, 1);
  assert.match(world.calls[0], /\/pulls\?state=closed&base=v1&/);
});

test('helper deploy CLI uses a product cwd with the Kustomize bundle and accepts its valid continuation', withDirectory(async directory => {
  const world = newWorld();
  const completion = await continuePartialPublication(options(world, directory));
  const productRoot = path.join(directory, 'original-product');
  const manifest = JSON.parse(execFileSync('git', [
    'show', `${P.sourceSha}:releases/foundation.json`,
  ], { cwd: root, encoding: 'utf8' }));
  manifest.stage = 'release';
  manifest.evidence = {
    sourceSha: P.sourceSha, deploymentSha: P.sourceSha,
    personaRuns: ['api', 'ui', 'mcp'].map(surface => ({
      surface, sourceSha: P.sourceSha, runUrl: `https://github.com/${P.repository}/actions/runs/${RUN_ID}`,
    })),
  };
  for (const component of manifest.components) {
    const published = completion.published.find(item => item.id === component.id);
    if (published?.kind === 'image') component.imageDigest = published.image.split('@')[1];
  }
  for (const relative of [...manifest.components.map(component => component.project), WEB_LOCK_PATH]) {
    const destination = path.join(productRoot, relative);
    mkdirSync(path.dirname(destination), { recursive: true });
    writeFileSync(destination, execFileSync('git', ['show', `${P.sourceSha}:${relative}`], {
      cwd: root, encoding: 'utf8',
    }));
  }
  // The release helper resolves and renders the install bundle from its product working directory.
  cpSync(path.join(root, 'deploy', 'k8s'), path.join(productRoot, 'deploy', 'k8s'), { recursive: true });
  const manifestPath = path.join(directory, 'release-manifest.json');
  const publicationPath = path.join(directory, 'publication.json');
  writeFileSync(manifestPath, JSON.stringify(manifest));
  const target = {
    subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
    tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
    resourceGroup: 'agentweaver', clusterName: 'agentweaver-aks', namespace: 'agentweaver',
    nodeMin: 2, nodeMax: 3,
  };
  const argv = ['v1.0.0', '--manifest', manifestPath, '--publication', publicationPath,
    '--completion', publicationPath, '--provider-catalog-configmap', 'projects-config-provider-catalog',
    '--provider-catalog-source-commit', P.sourceSha,
    '--subscription-id', target.subscriptionId, '--tenant-id', target.tenantId,
    '--resource-group', target.resourceGroup, '--cluster', target.clusterName,
    '--namespace', target.namespace, '--node-min', '2', '--node-max', '3'];
  let head = P.sourceSha;
  let dirty = '';
  let rendered = 0;
  const releaseTagSha = 'e'.repeat(40);
  const dependencies = {
    agentSandboxRelease: agentSandboxTestRelease,
    fetchAgentSandbox: async url => {
      assert.equal(url, agentSandboxTestRelease.manifestUrl);
      return {
        ok: true,
        status: 200,
        arrayBuffer: async () => Buffer.from(agentSandboxTestManifest),
      };
    },
    verifyImages: async () => new Map(validatePublicationReceipt(completion, manifest, P.sourceSha)
      .map(image => [image.name, new Set([image.image.split('@')[1], image.sandboxImageDigest].filter(Boolean))])),
    verifyTarget: async () => ({ exact: true }),
    verifyPrerequisites: () => {},
    run: (file, args, opts = {}) => {
      if (file === 'git') {
        assert.equal(opts.cwd, productRoot);
        if (args[0] === 'rev-parse') return head;
        if (args[0] === 'status') return dirty;
      }
      if (file === 'gh' && !args.includes('--method')) {
        assert.equal(opts.cwd, productRoot);
        const endpoint = args.at(-1);
        if (endpoint.endsWith('/releases/tags/v1.0.0')) {
          return JSON.stringify({ tag_name: 'v1.0.0', draft: false, prerelease: false });
        }
        if (endpoint.endsWith('/git/ref/tags/v1.0.0')) {
          return JSON.stringify({ object: { type: 'tag', sha: releaseTagSha } });
        }
        if (endpoint.endsWith(`/git/tags/${releaseTagSha}`)) {
          return JSON.stringify({ tag: 'v1.0.0', object: { type: 'commit', sha: P.sourceSha } });
        }
        return JSON.stringify(gh(world, ['api', '--method', 'GET', endpoint]));
      }
      if (file === 'gh') return world.run(file, args, opts.input);
      if (file === 'kubectl') {
        assert.equal(opts.cwd, productRoot);
        if (args[0] === 'kustomize') {
          return execFileSync('kubectl', args, { cwd: opts.cwd, encoding: 'utf8' });
        }
        if (args[0] === 'apply' && args[1] === '--dry-run=client' && args[2] === '--filename') {
          assert.deepEqual(args, ['apply', '--dry-run=client', '--filename', '-']);
          assert.equal(opts.input, agentSandboxTestManifest);
          return '';
        }
        assert.deepEqual(args.slice(0, 3), ['apply', '--dry-run=client', '-k']);
        assert.equal(args.length, 4);
        assert.equal(path.basename(args[3]), 'k8s');
        assert.equal(path.basename(path.dirname(args[3])), 'deploy');
        rendered += 1;
        return '';
      }
      assert.fail(`unexpected command ${file} ${args.join(' ')}`);
    },
  };
  const previousCwd = process.cwd();
  process.chdir(productRoot);
  try {
    // No root or release-verification override: the helper module uses the product cwd.
    assert.notEqual(productRoot, root);
    const result = await deployRelease({ argv }, dependencies);
    assert.equal(result.ok, true);
    assert.equal(result.dryRun, true);
    assert.equal(result.sourceSha, P.sourceSha);
    assert.equal(result.services.length, 9);
    assert.equal(rendered, 1);
    head = HELPER;
    await assert.rejects(deployRelease({ argv }, dependencies), /exact clean local source commit/);
    head = P.sourceSha;
    dirty = ' M product.csproj';
    await assert.rejects(deployRelease({ argv }, dependencies), /checkout must be clean/);
    dirty = '';
    const noCompletion = argv.filter((_, index) => index !== 5 && index !== 6);
    await assert.rejects(deployRelease({ argv: noCompletion }, dependencies), /requires an explicit --completion/);
    head = HELPER;
    await assert.rejects(deployRelease({
      releaseTag: 'v1.0.0', target, manifest, publication: original, dryRun: true,
    }, dependencies), /exact clean local source commit/);
    assert.equal(rendered, 1);
  } finally {
    process.chdir(previousCwd);
  }
}));

test('helper admission paginates closed v1 PRs and rejects an absent exact merge result', () => {
  const world = newWorld();
  const admitted = world.pulls[0];
  world.pulls = [...Array.from({ length: 100 }, () => ({ ...admitted, merge_commit_sha: 'b'.repeat(40) })), admitted];
  verifyHelperAdmission(world.run, HELPER);
  assert.equal(world.calls.length, 2);
  assert.match(world.calls[1], /&page=2 --jq /);
  world.pulls.pop();
  assert.throws(() => verifyHelperAdmission(world.run, HELPER), /exact result.*merged into v1/);
});

test('helper admission keeps a full page of large closed pull requests under the 1 MiB command buffer', () => {
  // Run 38966094647 failed when a raw 100-PR page (about 1.8 MB) overflowed spawnSync's 1 MiB default.
  const large = Array.from({ length: 100 }, (_, index) => ({
    state: 'closed', merged_at: '2026-10-20T00:00:00Z', merge_commit_sha: index.toString(16).padStart(40, '0'),
    base: { ref: 'v1', repo: { full_name: P.repository } }, body: 'x'.repeat(20_000),
  }));
  assert.ok(JSON.stringify(large).length > 1024 * 1024);
  const world = newWorld();
  world.pulls = large;
  let largest = 0;
  const run = (bin, args, input) => {
    const output = world.run(bin, args, input);
    largest = Math.max(largest, Buffer.byteLength(output));
    return output;
  };
  assert.throws(() => verifyHelperAdmission(run, HELPER), /merged into v1/);
  assert.ok(largest < 64 * 1024, `projected page was ${largest} bytes`);
});

test('helper admission fails closed on malformed responses and API failures', () => {
  assert.throws(() => verifyHelperAdmission(() => '{}', HELPER), /invalid response/);
  assert.throws(() => verifyHelperAdmission(() => { throw new Error('GitHub unavailable'); }, HELPER),
    /GitHub unavailable/);
  assert.throws(() => verifyHelperAdmission(() => { throw new Error('must not query'); }, 'v1'),
    /exact SHA/);
});

test('registry absence requires an explicit missing-manifest diagnostic', () => {
  const reference = `${P.registry}/agentweaver-gateway:1.0.0`;
  const error = nativeDiagnostic => Object.assign(new Error('x'), { operation: 'docker.manifest-read', nativeDiagnostic });
  assert.equal(isRegistryAbsent(error('MANIFEST_UNKNOWN: manifest unknown'), reference), true);
  assert.equal(isRegistryAbsent(error(`ERROR: ${reference}: not found`), reference), true);
  assert.equal(isRegistryAbsent(error('denied: requested access to the resource is denied'), reference), false);
  assert.equal(isRegistryAbsent(error('unauthorized: authentication required; manifest unknown'), reference), false);
  assert.equal(isRegistryAbsent(error('dial tcp: i/o timeout'), reference), false);
  assert.equal(isRegistryAbsent(error(`ERROR: ${P.registry}/other:1.0.0: not found`), reference), false);
  assert.equal(isRegistryAbsent(Object.assign(new Error('x'), { operation: 'docker.manifest-read', code: 'REGISTRY_NOT_FOUND' }), reference), false);
  assert.equal(isRegistryAbsent(Object.assign(new Error('x'), { operation: 'gh.api', nativeDiagnostic: 'manifest unknown' }), reference), false);
});

test('remote image readback verifies manifest bytes and selects the one linux/amd64 image', () => {
  const repository = `${P.registry}/agentweaver-gateway`;
  const manifest = JSON.stringify({
    schemaVersion: 2, mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: { digest: digest('config') }, layers: [{ digest: digest('l1') }, { digest: digest('l2') }],
  });
  const index = JSON.stringify({
    schemaVersion: 2, mediaType: 'application/vnd.oci.image.index.v1+json',
    manifests: [
      { digest: digest(manifest), platform: { os: 'linux', architecture: 'amd64' } },
      { digest: digest('attestation'), platform: { os: 'unknown', architecture: 'unknown' },
        annotations: { 'vnd.docker.reference.type': 'attestation-manifest' } },
    ],
  });
  const blobs = new Map([[digest(index), index], [digest(manifest), manifest]]);
  const run = top => (bin, args) => {
    const reference = args[3];
    if (args.includes('--format')) return JSON.stringify({ digest: top });
    return blobs.get(reference.slice(repository.length + 1));
  };
  assert.deepEqual(readRemoteImage(run(digest(index)), repository, '1.0.0'), {
    digest: digest(index), platformDigest: digest(manifest), configDigest: digest('config'), layerCount: 2,
  });
  blobs.set(digest(manifest), `${manifest} `);
  assert.throws(() => readRemoteImage(run(digest(index)), repository, '1.0.0'), /do not match their digest/);
  const absent = () => { throw Object.assign(new Error('x'), { operation: 'docker.manifest-read', nativeDiagnostic: 'manifest unknown' }); };
  assert.equal(readRemoteImage(absent, repository, '1.0.0'), null);
  const denied = () => { throw Object.assign(new Error('denied'), { operation: 'docker.manifest-read', nativeDiagnostic: 'denied' }); };
  assert.throws(() => readRemoteImage(denied, repository, '1.0.0'), /denied/);
});

test('an explicit AgentHost measurement source must be an exact commit SHA', () => {
  assert.throws(() => measureAgentHostArchiveSync('archive.tar.gz', '.', 'HEAD'), /exact commit SHA/);
});

// actions/download-artifact@v4 extracts a by-ID download into path/<artifact-name>/
// unless merge-multiple is set; the publisher reads both artifacts at their path roots.
test('continuation workflow extracts both original artifacts at the paths the publisher reads', () => {
  const workflow = readFileSync(path.join(root, P.workflowPath), 'utf8').replaceAll('\r\n', '\n');
  for (const [artifactId, directory] of [
    [P.packArtifactId, 'artifacts/release/pack/'],
    [P.receiptArtifactId, 'artifacts/release/original/'],
  ]) {
    const step = new RegExp(
      `run-id: ${P.runId}\\n\\s+artifact-ids: ${artifactId}\\n\\s+path: ${directory.replaceAll('/', '\\/')}\\n(?:\\s+#[^\\n]*\\n)*\\s+merge-multiple: true\\n`);
    assert.match(workflow, step);
  }
  assert.match(workflow, /node scripts\/release\/publish\.mjs releases\/foundation\.json artifacts\/release\/pack\n/);
  assert.match(workflow, /--original-receipt artifacts\/release\/original\/publication\.json\n/);
});

test('continuation workflow needs no environment approval and keeps exact one-use dispatch guards', () => {
  const workflow = readFileSync(path.join(root, P.workflowPath), 'utf8').replaceAll('\r\n', '\n');
  assert.doesNotMatch(workflow, /^\s*environment:/m);
  assert.doesNotMatch(workflow, /vars\.RELEASE_NUGET_SOURCE/);
  assert.equal(workflow.split(`RELEASE_NUGET_SOURCE: ${P.feed}\n`).length - 1, 2);
  assert.match(workflow, /test "\$GITHUB_REPOSITORY" = "sabbour\/agentweaver"/);
  assert.match(workflow, /test "\$GITHUB_EVENT_NAME" = "workflow_dispatch"/);
  assert.match(workflow, /test "\$GITHUB_REF" = "refs\/heads\/v1"/);
  assert.match(workflow, /test "\$GITHUB_RUN_ATTEMPT" = "1"/);
  assert.ok(workflow.includes(`test "$EXPECTED_SOURCE" = "${P.sourceSha}"`));
  assert.match(workflow, /test "\$GITHUB_SHA" != "\$EXPECTED_SOURCE"/);
  assert.match(workflow, /group: v1-product-publication-\$\{\{ github.repository \}\}/);
  assert.match(workflow, /cancel-in-progress: false/);
  assert.match(workflow, /packages: write/);
});

const feedBase = new URL('https://nuget.pkg.github.com/sabbour/download/');
const feedAuth = 'Basic ZmVlZA==';
const abstractions = { id: 'Agentweaver.Abstractions', version: '1.0.0' };
const redirect = (location, status = 302) => new Response(null, { status, headers: location ? { location } : {} });

function scriptedFetch(responses) {
  const calls = [];
  return {
    calls,
    fetch: async (url, init) => {
      calls.push({ url: String(url), init });
      const next = responses.shift();
      if (!next) throw new Error(`unexpected fetch ${url}`);
      return typeof next === 'function' ? next(url, init) : next;
    },
  };
}

test('package reads follow a cross-origin redirect without sending the feed credential', async () => {
  const storage = 'https://pkg-containers.githubusercontent.com/ghcr1/blobs/sha256:abc?sig=1';
  const { calls, fetch } = scriptedFetch([redirect(storage), new Response(Buffer.from('nupkg'), { status: 200 })]);
  const bytes = await readPackageBytes(abstractions, { base: feedBase, authorization: feedAuth }, fetch);
  assert.equal(bytes.toString(), 'nupkg');
  assert.equal(calls[0].url, 'https://nuget.pkg.github.com/sabbour/download/agentweaver.abstractions/1.0.0/agentweaver.abstractions.1.0.0.nupkg');
  assert.equal(calls[0].init.headers.authorization, feedAuth);
  assert.equal(calls[1].url, storage);
  assert.equal(calls[1].init.headers.authorization, undefined);
  for (const { init } of calls) {
    assert.equal(init.cache, 'no-store');
    assert.equal(init.redirect, 'manual');
  }
});

test('package reads keep the credential on same-origin redirects and accept three hops', async () => {
  const { calls, fetch } = scriptedFetch([
    redirect('/sabbour/download/a', 301), redirect('b', 307), redirect('https://storage.example/c', 308),
    new Response(Buffer.from('ok'), { status: 200 }),
  ]);
  assert.equal((await readPackageBytes(abstractions, { base: feedBase, authorization: feedAuth }, fetch)).toString(), 'ok');
  assert.deepEqual(calls.map(({ init }) => init.headers.authorization), [feedAuth, feedAuth, feedAuth, undefined]);
  assert.equal(calls[2].url, 'https://nuget.pkg.github.com/sabbour/download/b');
});

test('a package 404 after a redirect is absent', async () => {
  const { fetch } = scriptedFetch([redirect('https://storage.example/x'), new Response(null, { status: 404 })]);
  assert.equal(await readPackageBytes(abstractions, { base: feedBase, authorization: feedAuth }, fetch), null);
});

const packageRedirectFailures = [
  ['an http Location', [redirect('http://storage.example/x')], /not a plain https URL/],
  ['a Location with userinfo', [redirect('https://user:pass@storage.example/x')], /not a plain https URL/],
  ['more than three redirects', [redirect('/1'), redirect('/2'), redirect('/3'), redirect('/4')], /exceeded 3 redirects/],
  ['a redirect without Location', [redirect(undefined)], /HTTP 302\) from nuget\.pkg\.github\.com has no Location/],
  ['a storage error', [redirect('https://storage.example/x'), new Response(null, { status: 403 })], /HTTP 403 from storage\.example/],
];
for (const [name, responses, pattern] of packageRedirectFailures) {
  test(`package reads fail on ${name}`, async () => {
    const { fetch } = scriptedFetch(responses);
    await assert.rejects(readPackageBytes(abstractions, { base: feedBase, authorization: feedAuth }, fetch), pattern);
  });
}

const missingImageRepositories = [
  'identity.broker', 'eventsandsessions', 'gateway', 'mcp', '-web', 'orchestrator', 'environment', 'knowledge', 'projects.config',
].map(name => `sabbour/agentweaver${name.startsWith('-') ? name : `.${name}`}`);

function registryWriteFetch({ uploadStatus = () => 202, deleteError = false } = {}) {
  const calls = [];
  return {
    calls,
    fetch: async (url, init = {}) => {
      const target = new URL(String(url));
      calls.push({ method: init.method ?? 'GET', url: target, init });
      if (target.pathname === '/token') return Response.json({ token: `push-${target.searchParams.get('scope')}` });
      const repository = target.pathname.replace(/^\/v2\//, '').replace(/\/blobs\/uploads\/.*$/, '');
      if (init.method === 'POST') {
        const status = uploadStatus(repository);
        return new Response(null, {
          status, headers: status === 202 ? { location: `/v2/${repository}/blobs/uploads/session-1?_state=x` } : {},
        });
      }
      if (init.method === 'DELETE') {
        if (deleteError) throw new Error('delete failed');
        return new Response(null, { status: 204 });
      }
      throw new Error(`unexpected registry ${init.method} ${url}`);
    },
  };
}

test('registry write checks start and cancel one upload session per repository without committing content', async () => {
  const { calls, fetch } = registryWriteFetch();
  await verifyRegistryWriteAccess(missingImageRepositories, { username: 'sabbour', password: 'secret' }, fetch);
  assert.equal(calls.length, missingImageRepositories.length * 3);
  missingImageRepositories.forEach((repository, index) => {
    const [token, upload, cancel] = calls.slice(index * 3, index * 3 + 3);
    assert.equal(token.url.searchParams.get('scope'), `repository:${repository}:pull,push`);
    assert.equal(token.init.headers.authorization, `Basic ${Buffer.from('sabbour:secret').toString('base64')}`);
    assert.equal(upload.method, 'POST');
    assert.equal(upload.url.href, `https://ghcr.io/v2/${repository}/blobs/uploads/`);
    assert.equal(upload.init.headers.authorization, `Bearer push-repository:${repository}:pull,push`);
    assert.equal(cancel.method, 'DELETE');
    assert.equal(cancel.url.href, `https://ghcr.io/v2/${repository}/blobs/uploads/session-1?_state=x`);
  });
  assert.ok(calls.every(({ method, url }) => method !== 'PUT' && method !== 'PATCH' && !url.pathname.includes('/manifests/')));
});

test('registry write checks cancel the session even when cancellation fails', async () => {
  const { calls, fetch } = registryWriteFetch({ deleteError: true });
  await verifyRegistryWriteAccess(missingImageRepositories.slice(0, 1), { username: 'u', password: 'p' }, fetch);
  assert.deepEqual(calls.map(({ method }) => method), ['GET', 'POST', 'DELETE']);
});

test('a denied registry write check names the repository and stops before the claim', withDirectory(async directory => {
  const world = newWorld();
  const registry = registryWriteFetch({ uploadStatus: repository => repository.endsWith('.gateway') ? 403 : 202 });
  world.registryWriteFetch = registry.fetch;
  await assert.rejects(continuePartialPublication(options(world, directory)),
    /GHCR write check for sabbour\/agentweaver\.gateway returned HTTP 403/);
  assert.deepEqual(mutations(world), []);
  assert.deepEqual(continuationRefs(world), []);
  assert.equal(existsSync(path.join(directory, 'continuation.json')), false);
  const cancelled = registry.calls.filter(({ method }) => method === 'DELETE').map(({ url }) => url.pathname);
  assert.deepEqual(cancelled, missingImageRepositories.slice(0, 2).map(repository => `/v2/${repository}/blobs/uploads/session-1`));
}));

test('continuation checks write access to the nine exact missing image repositories before its claim', withDirectory(async directory => {
  const world = newWorld();
  await continuePartialPublication(options(world, directory));
  const check = world.calls.findIndex(call => call.startsWith('registry-write-check '));
  const claim = world.calls.findIndex(call => /^gh api --method POST .*git\/tags/.test(call));
  assert.ok(check >= 0 && check < claim);
  assert.deepEqual(world.calls[check].split(' ').slice(1), missingImageRepositories);
}));

test('a transient read error after a push is retried before the output is recorded', withDirectory(async directory => {
  const world = newWorld();
  let failed = false;
  const receipt = await continuePartialPublication(options(world, directory, {
    readPackageSha256: async component => {
      const value = world.packages.get(component.id) ?? null;
      if (component.id === 'Agentweaver.Providers.Storage.AzureFiles' && value !== null && !failed) {
        failed = true;
        throw new Error('partial publication: transient HTTP 502');
      }
      return value;
    },
  }));
  assert.equal(failed, true);
  assert.equal(receipt.status, 'published');
}));
