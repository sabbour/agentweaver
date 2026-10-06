import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { diagnosticPins as pins } from '../probe-publication-diagnostic.mjs';
import { publishFrozenProbeRecovery, recoveryClaimNamespace } from '../probe-publication-recovery.mjs';

const repository = 'ghcr.io/sabbour/agentweaver-foundation-probe';
const target = `${repository}:0.0.0`;
const initialDigest = 'sha256:452be7e284ee6c33814fcedcf1d7c98f98384d09ea7239ad851f9cb316727c9a';
const platformDigest = `sha256:${'d'.repeat(64)}`;
const newDigest = `sha256:${'e'.repeat(64)}`;
const mediaType = 'application/vnd.oci.image.manifest.v1+json';

function fixture(t) {
  mkdirSync('artifacts/release-tests', { recursive: true });
  const directory = mkdtempSync(path.resolve('artifacts/release-tests/recovery-'));
  t.after(() => rmSync(directory, { recursive: true, force: true }));
  const env = {
    GITHUB_REPOSITORY: 'sabbour/agentweaver', GITHUB_REF: 'refs/heads/v1', GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_RUN_ID: '12345', GITHUB_RUN_ATTEMPT: '1', EXPECTED_SOURCE: 'a'.repeat(40),
    PUBLISH: 'true', PROBE_SAMPLER_REPLACEMENT: 'true', EXPECTED_PROBE_DIGEST: pins.currentDigest,
    RELEASE_REGISTRY: 'ghcr.io/sabbour', RELEASE_REGISTRY_USER: 'sabbour',
    GH_TOKEN: 'synthetic-token', RELEASE_REGISTRY_PASSWORD: 'synthetic-token', GITHUB_TOKEN: 'synthetic-unused-token',
  };
  const config = { os: 'linux', architecture: 'amd64', config: {
    User: '10001:10001', Entrypoint: ['dotnet', 'Agentweaver.FoundationProbe.dll'], Labels: {
      'org.opencontainers.image.revision': pins.sourceSha, 'org.opencontainers.image.version': '0.0.0',
      'io.agentweaver.source-tree': pins.sourceTree, 'io.agentweaver.infrastructure-source-hash': pins.sourceHash,
    },
  } };
  const archive = Buffer.from('synthetic frozen archive');
  const dll = Buffer.from('synthetic frozen DLL');
  const configBytes = Buffer.from(JSON.stringify(config));
  const provenance = Buffer.from(JSON.stringify({ sourceSha: pins.sourceSha, artifacts: [{
    path: 'Agentweaver.FoundationProbe.0.0.0.tar.gz', componentId: 'Agentweaver.FoundationProbe',
    kind: 'image', sha256: pins.archiveSha256,
  }] }));
  const refs = new Map();
  for (const namespace of [
    'agentweaver-publication/initial-foundation-probe-0.0.0-replacement',
    'agentweaver-publication/foundation-probe-0.0.0-sampler-replacement',
    `agentweaver-publication/${pins.sourceSha}`,
    'agentweaver-publication/foundation-probe-0.0.0-sampler-recovery-attempt-1',
    'agentweaver-publication/foundation-probe-0.0.0-sampler-recovery-helper/3b73ddbe556133a7847f048f4a2e6524cb3430b2',
  ]) for (const name of ['claim', 'result']) {
    const ref = `refs/tags/${namespace}/${name}`;
    refs.set(ref, { ref, object: { type: 'tag', sha: 'b'.repeat(40) } });
  }
  const historical = new Map(refs);
  const calls = [];
  let pushed = false;
  let tagReads = 0;
  const raw = (manifestDigest, index = false) => JSON.stringify({
    schemaVersion: 2, mediaType: index ? 'application/vnd.oci.image.index.v1+json' : mediaType,
    ...(index ? { manifests: [{ digest: platformDigest, annotations: { private: 'synthetic-token' } }] } : {}),
    syntheticDigest: manifestDigest, annotations: { private: 'synthetic-token' },
  });
  const f = {
    env, directory, refs, calls, config, archive, dll, configBytes,
    before: () => undefined,
    digest: bytes => Buffer.from(bytes).equals(provenance) ? pins.provenanceSha256
      : Buffer.from(bytes).equals(archive) ? pins.archiveSha256
      : Buffer.from(bytes).equals(configBytes) ? pins.configSha256
      : Buffer.from(bytes).equals(dll) ? pins.dllSha256 : JSON.parse(bytes).syntheticDigest.slice(7),
    readFile: file => path.basename(file) === 'provenance.json' ? provenance : archive,
    assertHistory: () => { for (const [ref, value] of historical) assert.equal(refs.get(ref), value); },
    receipt: () => JSON.parse(readFileSync(path.join(directory, 'publication.json'), 'utf8')),
    raw,
  };
  f.spawn = (bin, args, options) => {
    calls.push({ bin, args, options });
    assert.equal(options.env.GITHUB_TOKEN, undefined);
    assert.equal(options.env.RELEASE_REGISTRY_PASSWORD, undefined);
    assert.equal(options.env.GH_TOKEN, bin === 'gh' ? env.GH_TOKEN : undefined);
    const override = f.before(bin, args, options);
    if (override) return override;
    const success = stdout => ({ status: 0, stdout, stderr: '' });
    if (bin === 'git') return success(args[0] === 'status' ? '' : env.EXPECTED_SOURCE);
    if (bin === 'tar') return success(
      args.at(-1) === 'manifest.json' ? Buffer.from(JSON.stringify([{
        Config: `${pins.configSha256}.json`, Layers: [`${'c'.repeat(64)}/layer.tar`],
        RepoTags: ['agentweaver-foundation-probe:0.0.0'],
      }])) : args.at(-1).endsWith('.json') ? configBytes
        : args.at(-1).endsWith('.dll') ? dll : Buffer.from('synthetic layer'));
    if (bin === 'gh') {
      const method = args[args.indexOf('--method') + 1];
      const endpoint = args[3];
      const body = options.input && JSON.parse(options.input);
      if (method === 'GET') {
        const prefix = `refs/${endpoint.split('/git/matching-refs/')[1]}`;
        return success(JSON.stringify([...refs.values()].filter(item => item.ref.startsWith(prefix))));
      }
      assert.equal(method, 'POST');
      if (endpoint.endsWith('/git/tags')) {
        assert.equal(body.object, env.EXPECTED_SOURCE);
        assert.ok(body.tag.startsWith(`${recoveryClaimNamespace}/`) ||
          body.tag.startsWith(`agentweaver-publication/foundation-probe-0.0.0-sampler-recovery-helper/${env.EXPECTED_SOURCE}/`));
        const sha = createHash('sha1').update(options.input).digest('hex');
        return success(JSON.stringify({ ...body, sha, object: { sha: body.object, type: body.type } }));
      }
      assert.ok(endpoint.endsWith('/git/refs'));
      if (refs.has(body.ref)) return { status: 1, stdout: '', stderr: 'ref already exists synthetic-token' };
      const ref = { ref: body.ref, object: { type: 'tag', sha: body.sha } };
      refs.set(body.ref, ref);
      return success(JSON.stringify(ref));
    }
    assert.equal(bin, 'docker');
    assert.ok(['login', 'load', 'inspect', 'tag', 'push', 'buildx'].includes(args[0]));
    if (args[0] === 'login') assert.equal(options.input, env.GH_TOKEN);
    if (args[0] === 'push') { assert.equal(args[1], target); pushed = true; }
    if (args[0] === 'tag') assert.deepEqual(args, ['tag', 'agentweaver-foundation-probe:0.0.0', target]);
    if (args[0] === 'inspect') return success(JSON.stringify(args.includes('{{json .RepoDigests}}') ?
      [`${repository}@${newDigest}`] : { Id: `sha256:${pins.configSha256}`, Os: 'linux', Architecture: 'amd64', Config: config.config }));
    if (args[0] === 'buildx') {
      const reference = args[3];
      if (reference === target) { tagReads++; return success(raw(pushed ? newDigest : pins.currentDigest)); }
      const manifestDigest = reference.split('@')[1];
      return success(raw(manifestDigest, manifestDigest === initialDigest));
    }
    return success('private native output synthetic-token');
  };
  f.publish = (options = {}) => publishFrozenProbeRecovery(directory, { ...f, ...options });
  f.pushCount = () => calls.filter(call => call.bin === 'docker' && call.args[0] === 'push').length;
  f.tagReads = () => tagReads;
  return f;
}

test('recovery publishes the original frozen image once under distinct permanent attempt and helper claims', t => {
  const f = fixture(t);
  const receipt = f.publish();
  assert.equal(receipt.status, 'published');
  assert.equal(receipt.imageSourceSha, pins.sourceSha);
  assert.equal(receipt.helperSourceSha, f.env.EXPECTED_SOURCE);
  assert.notEqual(receipt.imageSourceSha, receipt.helperSourceSha);
  assert.equal(receipt.newDigest, newDigest);
  assert.equal(receipt.loadedConfigDigest, `sha256:${pins.configSha256}`);
  assert.equal(receipt.frozenArtifactVerified, true);
  assert.equal(receipt.originalIndexAndPlatformsRetained, true);
  assert.equal(receipt.credentialDirectoryRemoved, true);
  assert.equal(f.pushCount(), 1);
  assert.equal(f.tagReads(), 3);
  assert.equal(receipt.claims.length, 2);
  for (const claim of receipt.claims) {
    assert.equal(claim.acknowledged, true);
    for (const name of ['claim', 'result']) assert.ok(f.refs.has(`refs/tags/${claim.namespace}/${name}`));
  }
  for (const retained of [pins.currentDigest, initialDigest, platformDigest])
    assert.ok(f.calls.some(call => call.args[3] === `${repository}@${retained}`));
  const directory = f.calls.find(call => call.bin === 'docker' && call.args[0] === 'login').options.env.DOCKER_CONFIG;
  assert.equal(existsSync(directory), false);
  f.assertHistory();
  assert.deepEqual(f.receipt(), receipt);
  assert.doesNotMatch(JSON.stringify(receipt), /synthetic-token|private native output/);
  assert.throws(() => f.publish(), error => error.recovery.failure.code === 'RECEIPT_ALREADY_EXISTS');
  assert.equal(f.pushCount(), 1);
});

for (const [name, change] of [
  ['wrong repository', { GITHUB_REPOSITORY: 'other/repo' }],
  ['wrong branch', { GITHUB_REF: 'refs/heads/dev' }],
  ['non-dispatch event', { GITHUB_EVENT_NAME: 'push' }],
  ['rerun', { GITHUB_RUN_ATTEMPT: '2' }],
  ['read-only intent', { PUBLISH: 'false' }],
  ['ordinary intent', { PROBE_SAMPLER_REPLACEMENT: 'false' }],
  ['arbitrary digest', { EXPECTED_PROBE_DIGEST: newDigest }],
  ['wrong registry', { RELEASE_REGISTRY: 'ghcr.io/other' }],
  ['separate stored credential', { RELEASE_REGISTRY_PASSWORD: 'other-secret' }],
]) test(`recovery refuses ${name} before native operations or claims`, t => {
  const f = fixture(t);
  assert.throws(() => f.publish({ env: { ...f.env, ...change } }),
    error => error.recovery.failure.code === 'RECOVERY_SCOPE_INVALID');
  assert.deepEqual(f.calls, []);
  assert.equal(existsSync(path.join(f.directory, 'publication.json')), false);
});

test('recovery refuses dirty or non-admitted helper source before native registry operations', t => {
  for (const bad of ['status', 'origin']) {
    const f = fixture(t);
    f.before = (bin, args) => bin === 'git' && (bad === 'status' ? args[0] === 'status' : args[1] === 'refs/remotes/origin/v1') ?
      { status: 0, stdout: bad === 'status' ? ' M tracked.mjs' : 'f'.repeat(40), stderr: '' } : undefined;
    assert.throws(() => f.publish(), error => error.recovery.failure.code === 'HELPER_SOURCE_MISMATCH');
    assert.ok(!f.calls.some(call => call.bin === 'docker' || call.bin === 'gh'));
  }
});

test('recovery does not treat the frozen image source as its new publisher helper', t => {
  const f = fixture(t);
  f.env.EXPECTED_SOURCE = pins.sourceSha;
  assert.throws(() => f.publish(), error => error.recovery.failure.code === 'HELPER_SOURCE_MISMATCH');
  assert.ok(!f.calls.some(call => call.bin === 'docker' || call.bin === 'gh'));
});

test('recovery refuses substituted archive, provenance, config or DLL before a permanent claim', t => {
  for (const altered of ['archive', 'provenance', 'configBytes', 'dll']) {
    const f = fixture(t);
    assert.throws(() => f.publish({ digest: bytes =>
      altered === 'provenance' && Buffer.from(bytes).toString().includes('"sourceSha"') ||
      altered !== 'provenance' && Buffer.from(bytes).equals(f[altered]) ? 'f'.repeat(64) : f.digest(bytes),
    }), error => error.recovery.status === 'partial');
    assert.ok(!f.calls.some(call => call.bin === 'docker' || call.bin === 'gh'));
    assert.equal(f.pushCount(), 0);
  }
});

test('fixed recovery namespace blocks another helper even when local receipts and helper claims are absent', t => {
  const f = fixture(t);
  const ref = `refs/tags/${recoveryClaimNamespace}/claim`;
  f.refs.set(ref, { ref, object: { type: 'tag', sha: 'c'.repeat(40) } });
  f.env.EXPECTED_SOURCE = 'f'.repeat(40);
  assert.throws(() => f.publish(), error => error.recovery.failure.code === 'RECOVERY_ALREADY_CLAIMED');
  assert.ok(!f.calls.some(call => call.bin === 'docker' || call.args.includes('POST')));
  f.assertHistory();
});

test('a prior result without a claim and an ambiguous claim query each fail closed before new writes', t => {
  for (const ambiguous of [false, true]) {
    const f = fixture(t);
    if (ambiguous) f.before = (bin, args) => bin === 'gh' && args.includes('GET') ?
      { status: 0, stdout: '{"private":"synthetic-token"}', stderr: '' } : undefined;
    else {
      const ref = `refs/tags/${recoveryClaimNamespace}/result`;
      f.refs.set(ref, { ref, object: { type: 'tag', sha: 'c'.repeat(40) } });
    }
    assert.throws(() => f.publish(), error => error.recovery.failure.code === 'RECOVERY_ALREADY_CLAIMED');
    assert.ok(!f.calls.some(call => call.bin === 'docker' || call.args.includes('POST')));
  }
});

test('a pre-existing receipt is not overwritten or reused', t => {
  const f = fixture(t);
  const file = path.join(f.directory, 'publication.json');
  writeFileSync(file, 'original immutable receipt');
  assert.throws(() => f.publish(), error => error.recovery.failure.code === 'RECEIPT_ALREADY_EXISTS');
  assert.equal(readFileSync(file, 'utf8'), 'original immutable receipt');
  assert.ok(!f.calls.some(call => call.bin === 'docker' || call.bin === 'gh'));
});

test('preclaim or prepush current-tag drift never pushes, with spent claims and partial receipt after claim', t => {
  for (const concurrent of [false, true]) {
    const f = fixture(t);
    let reads = 0;
    f.before = (bin, args) => bin === 'docker' && args[0] === 'buildx' && args[3] === target &&
      ++reads === (concurrent ? 2 : 1) ? { status: 0, stdout: f.raw(newDigest), stderr: '' } : undefined;
    assert.throws(() => f.publish(), error => error.recovery.failure.code === 'CURRENT_MANIFEST_MISMATCH');
    assert.equal(f.pushCount(), 0);
    if (concurrent) {
      assert.equal(f.receipt().status, 'partial');
      assert.ok(f.refs.has(`refs/tags/${recoveryClaimNamespace}/claim`));
      assert.ok(f.refs.has(`refs/tags/${recoveryClaimNamespace}/result`));
    } else assert.ok(!f.calls.some(call => call.args.includes('POST')));
    f.assertHistory();
  }
});

test('ambiguous first claim response stops with local partial receipt and preserves the actual permanent ref', t => {
  const f = fixture(t);
  let directory;
  f.before = (bin, args, options) => {
    if (bin === 'gh' && args[3].endsWith('/git/refs')) {
      const body = JSON.parse(options.input);
      if (body.ref === `refs/tags/${recoveryClaimNamespace}/claim`) {
        directory = options.env.DOCKER_CONFIG;
        f.refs.set(body.ref, { ref: body.ref, object: { type: 'tag', sha: body.sha } });
        return { status: 1, stdout: '', stderr: 'connection refused synthetic-token' };
      }
    }
  };
  assert.throws(() => f.publish(), error => error.recovery.failure.code === 'TRANSPORT_FAILED');
  const receipt = f.receipt();
  assert.equal(receipt.status, 'partial');
  assert.equal(receipt.claims[0].attempted, true);
  assert.equal(receipt.claims[0].acknowledged, false);
  assert.equal(receipt.claims[1].attempted, false);
  assert.ok(f.refs.has(`refs/tags/${recoveryClaimNamespace}/claim`));
  assert.equal(f.pushCount(), 0);
  assert.equal(existsSync(directory), false);
  assert.equal(receipt.credentialDirectoryRemoved, true);
  assert.doesNotMatch(JSON.stringify(receipt), /synthetic-token|connection refused/);
  f.assertHistory();
});

test('second claim failure persists the first spent result and local partial receipt before any push', t => {
  const f = fixture(t);
  f.before = (bin, args, options) => bin === 'gh' && args[3].endsWith('/git/refs') &&
    JSON.parse(options.input).ref.includes('sampler-recovery-helper/') ?
    { status: 1, stdout: '', stderr: 'forbidden synthetic-token' } : undefined;
  assert.throws(() => f.publish(), error => error.recovery.failure.code === 'AUTHORIZATION_DENIED');
  const receipt = f.receipt();
  assert.equal(receipt.claims[0].acknowledged, true);
  assert.equal(receipt.claims[1].attempted, true);
  assert.equal(receipt.claims[1].acknowledged, false);
  assert.ok(f.refs.has(`refs/tags/${recoveryClaimNamespace}/claim`));
  assert.ok(f.refs.has(`refs/tags/${recoveryClaimNamespace}/result`));
  assert.equal(f.pushCount(), 0);
  assert.equal(receipt.credentialDirectoryRemoved, true);
  f.assertHistory();
});

test('malformed first claim response records partial evidence without proceeding to ref or push', t => {
  const f = fixture(t);
  f.before = (bin, args) => bin === 'gh' && args[3].endsWith('/git/tags') ?
    { status: 0, stdout: '{"sha":"synthetic-token"}', stderr: '' } : undefined;
  assert.throws(() => f.publish(), error => error.recovery.failure.code === 'TAG_RESPONSE_MISMATCH');
  assert.equal(f.receipt().claims[0].attempted, true);
  assert.equal(f.receipt().claims[0].acknowledged, false);
  assert.equal(f.receipt().credentialDirectoryRemoved, true);
  assert.ok(!f.calls.some(call => call.bin === 'gh' && call.args[3].endsWith('/git/refs')));
  assert.equal(f.pushCount(), 0);
});

test('loaded image contract mismatch consumes the new attempt without pushing a substituted image', t => {
  for (const field of ['Id', 'Config']) {
    const f = fixture(t);
    f.before = (bin, args) => bin === 'docker' && args[0] === 'inspect' ? {
      status: 0, stderr: '', stdout: JSON.stringify({
        Id: field === 'Id' ? newDigest : `sha256:${pins.configSha256}`,
        Os: 'linux', Architecture: 'amd64', Config: field === 'Config' ?
          { ...f.config.config, User: '0:0' } : f.config.config,
      }),
    } : undefined;
    assert.throws(() => f.publish(), error => error.recovery.failure.code === 'LOADED_IMAGE_BINDING_INVALID');
    assert.equal(f.pushCount(), 0);
    assert.equal(f.receipt().credentialDirectoryRemoved, true);
    assert.ok(f.refs.has(`refs/tags/${recoveryClaimNamespace}/result`));
    f.assertHistory();
  }
});

test('unexpected private native exception remains sanitized and does not trigger another operation', t => {
  const f = fixture(t);
  f.before = (bin, args) => {
    if (bin === 'docker' && args[0] === 'login') throw Object.assign(new Error('synthetic-token'), {
      operation: 'synthetic-token', code: 'PRIVATE_CODE', exitCode: 'synthetic-token',
    });
  };
  assert.throws(() => f.publish(), error => {
    assert.deepEqual(error.recovery.failure, { operation: 'docker.login', exitCode: null, code: 'RECOVERY_VALIDATION_FAILED' });
    assert.doesNotMatch(JSON.stringify(error.recovery), /synthetic-token|PRIVATE_CODE/);
    return true;
  });
  assert.equal(f.receipt().credentialDirectoryRemoved, true);
  assert.equal(f.pushCount(), 0);
});

for (const operation of ['login', 'load', 'inspect', 'tag', 'push']) {
  test(`native ${operation} denial stops without retry and retains both new claims and safe partial results`, t => {
    const f = fixture(t);
    f.before = (bin, args) => bin === 'docker' && args[0] === operation ? {
      status: 1, stdout: 'synthetic-token', stderr: operation === 'push' ?
        'permission_denied: write_package synthetic-token' : 'unauthorized synthetic-token',
    } : undefined;
    assert.throws(() => f.publish(), error => {
      assert.equal(error.recovery.failure.operation, `docker.${operation}`);
      assert.equal(error.recovery.failure.exitCode, 1);
      assert.equal(error.recovery.failure.code, operation === 'push' ? 'REGISTRY_WRITE_DENIED' : 'AUTHORIZATION_DENIED');
      assert.doesNotMatch(JSON.stringify(error.recovery), /synthetic-token|unauthorized|write_package/);
      return true;
    });
    const receipt = f.receipt();
    assert.equal(receipt.status, 'partial');
    assert.deepEqual(receipt.published, []);
    assert.equal(receipt.credentialDirectoryRemoved, true);
    for (const claim of receipt.claims) for (const name of ['claim', 'result'])
      assert.ok(f.refs.has(`refs/tags/${claim.namespace}/${name}`));
    assert.equal(f.pushCount(), operation === 'push' ? 1 : 0);
    f.assertHistory();
  });
}

test('postpush retention failure preserves the irreversible published digest without claiming completion', t => {
  const f = fixture(t);
  f.before = (bin, args) => bin === 'docker' && args[0] === 'buildx' && args[3] === `${repository}@${pins.currentDigest}` ?
    { status: 1, stdout: '', stderr: 'connection refused synthetic-token' } : undefined;
  assert.throws(() => f.publish(), error => error.recovery.failure.operation === 'docker.manifest-read');
  const receipt = f.receipt();
  assert.equal(receipt.status, 'partial');
  assert.equal(receipt.newDigest, newDigest);
  assert.equal(receipt.published[0].image, `${repository}@${newDigest}`);
  assert.equal(receipt.originalIndexAndPlatformsRetained, undefined);
  assert.equal(f.pushCount(), 1);
  f.assertHistory();
});

test('result persistence failure attempts the other acknowledged result and keeps local irreversible evidence', t => {
  const f = fixture(t);
  f.before = (bin, args, options) => bin === 'gh' && args[3].endsWith('/git/refs') &&
    JSON.parse(options.input).ref === `refs/tags/${recoveryClaimNamespace}/result` ?
    { status: 1, stdout: '', stderr: 'forbidden synthetic-token' } : undefined;
  assert.throws(() => f.publish(), error => error.recovery.persistenceFailures[0].code === 'AUTHORIZATION_DENIED');
  const receipt = f.receipt();
  assert.equal(receipt.status, 'partial');
  assert.equal(receipt.newDigest, newDigest);
  assert.ok(f.refs.has(`refs/tags/${receipt.claims[1].namespace}/result`));
  assert.equal(f.pushCount(), 1);
  f.assertHistory();
});

test('local receipt failure still leaves both permanent results and sanitized evidence in the thrown result', t => {
  const f = fixture(t);
  assert.throws(() => f.publish({ writeReceipt: () => { throw new Error('synthetic-token'); } }), error => {
    assert.equal(error.recovery.status, 'partial');
    assert.equal(error.recovery.newDigest, newDigest);
    assert.equal(error.recovery.persistenceFailures[0].code, 'RECEIPT_PERSISTENCE_FAILED');
    assert.doesNotMatch(JSON.stringify(error.recovery), /synthetic-token/);
    for (const claim of error.recovery.claims) assert.ok(f.refs.has(`refs/tags/${claim.namespace}/result`));
    return true;
  });
  assert.equal(f.pushCount(), 1);
});

test('recovery workflow uses the four existing inputs, skips pack and reuses the original artifact without stored credentials', () => {
  const workflow = readFileSync('.github/workflows/v1-release-pack.yml', 'utf8').replaceAll('\r\n', '\n');
  const recovery = workflow.slice(workflow.indexOf('\n  publish-probe-sampler:'));
  assert.match(workflow, /pack:\n\s+if: \$\{\{ !inputs\.foundation_probe_sampler_replacement \}\}/);
  assert.match(recovery, /inputs\.publish && inputs\.foundation_probe_sampler_replacement/);
  assert.match(recovery, /actions: read/);
  assert.match(recovery, /contents: write/);
  assert.match(recovery, /packages: write/);
  assert.match(recovery, /run-id: 37409181340/);
  assert.match(recovery, /name: v1-release-pack-f46cff3c65a97b76b78c9ea717ec8b2068d56de5-37409181340/);
  assert.match(recovery, /probe-publication-recovery\.mjs artifacts\/release\/frozen-probe/);
  assert.match(recovery, /test "\$GITHUB_RUN_ATTEMPT" = "1"/);
  assert.doesNotMatch(recovery, /needs: pack|setup-dotnet|release:pack|environment:|secrets\.|publish\.mjs|DOCKER_CONFIG:/);
  assert.match(recovery, /if: \$\{\{ always\(\) \}\}/);
  assert.match(recovery, /v1-probe-recovery-receipt-\$\{\{ github.sha \}\}-\$\{\{ github.run_id \}\}/);
});
