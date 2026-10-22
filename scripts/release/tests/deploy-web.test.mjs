import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseWebRolloutArgs, runWebImageRollout } from '../deploy-web.mjs';

const digest = character => `sha256:${character.repeat(64)}`;
const sourceSha = '1e62ed39add74e8aedbdfb781c4a6d18f5b8046f';
const imageSourceSha = '243843e5043a29d222375efe935972746e2f5f14';
const headSha = 'b'.repeat(40);
const sourceTree = '303d5bf8e5a173adb8c63c2005e4e1a5baf72611';
const webDigest = 'sha256:e280c0db7cc53739799e75068bb0d9eb52c541ed725da1e1dcaf745cb461ab8f';
const webImage = `ghcr.io/sabbour/agentweaver-web@${webDigest}`;
const packageSha = 'c83e0f03da4d3c8eebe77d169a2ebe0b72f4ac544d285e6d21c944d13cedfcb3';
const reused = {
  sourceSha: imageSourceSha,
  sourceTree,
  inputFingerprint: 'sha256:a215fe1c3939f0b172e974a8a74c248e23f530f5ace8c393ec2c8774d397889e',
  receiptSourceSha: imageSourceSha,
};

function makeDeployment(name, image, { web = false, initContainer = false } = {}) {
  const labels = web
    ? { 'app.kubernetes.io/name': 'web', 'agentweaver.io/service': 'web' }
    : { 'app.kubernetes.io/name': name, 'agentweaver.io/service': name };
  const podLabels = {
    ...labels,
    ...(web ? { 'agentweaver.io/workload': 'web' } : {}),
  };
  return {
    apiVersion: 'apps/v1',
    kind: 'Deployment',
    metadata: {
      name,
      namespace: 'agentweaver',
      labels,
      annotations: {
        'test.agentweaver.io/keep': name,
        ...(web ? { 'deployment.kubernetes.io/revision': '1' } : {}),
      },
      generation: 1,
      resourceVersion: `${name}-rv1`,
    },
    spec: {
      replicas: 1,
      strategy: { type: web ? 'Recreate' : 'RollingUpdate', rollingUpdate: web ? undefined : { maxSurge: 1 } },
      selector: { matchLabels: { 'app.kubernetes.io/name': name } },
      template: {
        metadata: { labels: podLabels },
        spec: {
          serviceAccountName: name,
          ...(initContainer ? { initContainers: [{ name: 'init', image: `ghcr.io/test/${name}-init@${digest('f')}` }] } : {}),
          containers: [{
            name,
            image,
            env: [{ name: 'KEEP_SETTING', value: name }],
            resources: { requests: { cpu: '100m' } },
          }],
        },
      },
    },
    status: { observedGeneration: 1, updatedReplicas: 1, readyReplicas: 1, availableReplicas: 1 },
  };
}

function makeFixture({
  currentWebImage = `ghcr.io/sabbour/agentweaver-web@${digest('1')}`,
  rejectAncestry,
  mutateNonWeb,
  mutateWebAnnotation,
  bumpWebRevision = true,
  inventoryKind = 'List',
  manifestOverrides,
  publicationOverrides,
} = {}) {
  const manifest = {
    stage: 'draft',
    components: [{
      id: 'Agentweaver.Web',
      kind: 'service',
      version: '1.2.0',
    }],
    ...manifestOverrides,
  };
  const publication = {
    schemaVersion: 1,
    sourceSha,
    status: 'published',
    provenanceSha256: 'a6341ce79dbb0ff91dac62cc32424a40d16d906c7b044d4f7e621f9a6832d50f',
    requiredPackageIds: [],
    planned: [{
      id: 'Agentweaver.Web',
      version: '1.2.0',
      kind: 'image',
      sha256: packageSha,
      destination: `ghcr.io/sabbour/agentweaver-web:1.2.0-${imageSourceSha}`,
      reused: structuredClone(reused),
    }],
    published: [{
      id: 'Agentweaver.Web',
      version: '1.2.0',
      kind: 'image',
      image: webImage,
      archiveSha256: packageSha,
      reused: structuredClone(reused),
    }],
    ...publicationOverrides,
  };
  const state = [
    makeDeployment('web', currentWebImage, { web: true }),
    makeDeployment('orchestrator', `ghcr.io/sabbour/agentweaver.orchestrator@${digest('2')}`, { initContainer: true }),
    makeDeployment('identity-broker', `ghcr.io/sabbour/agentweaver.identity.broker@${digest('3')}`),
  ];
  const inventory = {
    apiVersion: inventoryKind === 'List' ? 'v1' : 'apps/v1',
    items: state,
    kind: inventoryKind,
    metadata: { resourceVersion: 'inventory-rv1' },
  };
  const calls = [];
  const written = [];
  const exec = (command, args, settings = {}) => {
    calls.push({ command, args, settings });
    if (command === 'git') {
      if (args[0] === 'status') return { status: 0, stdout: '', stderr: '' };
      if (args[0] === 'fetch') return { status: 0, stdout: '', stderr: '' };
      if (args[0] === 'rev-parse' && args[1] === 'HEAD') return { status: 0, stdout: `${headSha}\n`, stderr: '' };
      if (args[0] === 'rev-parse' && args[1] === `${imageSourceSha}^{tree}`) {
        return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
      }
      if (args[0] === 'show') return { status: 0, stdout: JSON.stringify(manifest), stderr: '' };
      if (args[0] === 'merge-base') {
        return args[2] === rejectAncestry
          ? { status: 1, stdout: '', stderr: '' }
          : { status: 0, stdout: '', stderr: '' };
      }
      throw new Error(`Unexpected git ${args.join(' ')}`);
    }
    if (command !== 'kubectl') throw new Error(`Unexpected command ${command}`);
    const action = args[4];
    if (action === 'get' && args[5] === 'deployments') {
      return { status: 0, stdout: JSON.stringify(inventory), stderr: '' };
    }
    if (action === 'patch') {
      assert.deepEqual(args.slice(5, 8), ['deployment', 'web', '--type=json']);
      const operations = JSON.parse(args[9]);
      assert.equal(operations[0].op, 'test');
      assert.equal(operations[0].path, '/metadata/resourceVersion');
      const deployment = state.find(item => item.metadata.name === 'web');
      assert.equal(operations[0].value, deployment.metadata.resourceVersion);
      assert.equal(deployment.spec.template.spec.containers[0].image, operations[1].value);
      deployment.spec.template.spec.containers[0].image = operations[2].value;
      if (bumpWebRevision) deployment.metadata.annotations['deployment.kubernetes.io/revision'] = '2';
      if (mutateWebAnnotation) deployment.metadata.annotations['test.agentweaver.io/keep'] = 'changed';
      deployment.metadata.resourceVersion = 'web-rv2';
      deployment.metadata.generation = 2;
      deployment.status.observedGeneration = 2;
      if (mutateNonWeb) state.find(item => item.metadata.name === 'orchestrator').spec.replicas = 2;
      return { status: 0, stdout: 'patched', stderr: '' };
    }
    if (action === 'rollout') return { status: 0, stdout: 'deployment successfully rolled out', stderr: '' };
    throw new Error(`Unexpected kubectl ${args.join(' ')}`);
  };
  const options = parseWebRolloutArgs([
    '--publication', 'publication.json',
    '--context', 'approved-poc',
    '--receipt-dir', 'receipts',
  ]);
  return {
    calls,
    written,
    options,
    inventory,
    manifest,
    publication,
    overrides: {
      root: process.cwd(),
      exec,
      readFile: () => JSON.stringify(publication),
      verifyImage: async image => {
        assert.equal(image, webImage);
        return {
          digest: webDigest,
          acceptedDigests: new Set([webDigest]),
          sourceSha: imageSourceSha,
          sourceTree,
          version: '1.2.0',
        };
      },
      writeReceipt: (file, receipt) => written.push({ file, receipt: structuredClone(receipt) }),
      now: () => new Date('2026-10-22T10:00:00.000Z'),
      newRunId: () => 'offline-test',
      log: () => {},
    },
  };
}

test('arguments select only the Web rollout and reject service overrides', () => {
  assert.throws(() => parseWebRolloutArgs(['--service', 'web']), /unknown argument/);
  assert.throws(() => parseWebRolloutArgs(['--publication', 'receipt.json']), /--context/);
  assert.throws(() => parseWebRolloutArgs(['--publication', 'receipt.json', '--context', 'poc',
    '--readiness-timeout-seconds', '5']), /60 to 3600/);
  assert.equal(parseWebRolloutArgs(['--help']).help, true);
});

test('standard draft manifest and reused publication receipt select the Web image', async () => {
  const fixture = makeFixture();
  const receipt = await runWebImageRollout(fixture.options, fixture.overrides);

  assert.equal(receipt.result, 'planned');
  assert.equal(receipt.publicationSourceSha, sourceSha);
  assert.equal(receipt.webImage, webImage);
  assert.equal(receipt.imageRevision, imageSourceSha);
  assert.equal(receipt.imageVersion, '1.2.0');
});

test('a publication without exactly one planned and published Web entry is rejected', async () => {
  const fixture = makeFixture();
  fixture.publication.planned = [];

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
    /exactly one planned and published Web image/);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});

test('a Web receipt version that differs from the draft manifest is rejected', async () => {
  const fixture = makeFixture();
  fixture.publication.published[0].version = '1.2.1';

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
    /does not prove the exact source-manifest artifact/);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});

test('reused Web provenance must agree between planned and published entries', async () => {
  const fixture = makeFixture();
  fixture.publication.published[0].reused.sourceSha = '9'.repeat(40);

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
    /reused Web image provenance is malformed or differs/);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});

test('reused Web images require the current publication receipt format', async () => {
  const fixture = makeFixture();
  delete fixture.publication.requiredPackageIds;

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
    /reused Web image provenance is malformed or differs/);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});

test('a reused Web image must retain its original build tag', async () => {
  const fixture = makeFixture();
  fixture.publication.planned[0].destination =
    `ghcr.io/sabbour/agentweaver-web:1.2.0-${sourceSha}`;

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
    /tag, repository, digest, and source manifest do not agree/);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});

test('a registry digest that no longer matches the publication is rejected', async () => {
  const fixture = makeFixture();
  fixture.overrides.verifyImage = async () => ({
    digest: digest('1'),
    acceptedDigests: new Set([digest('1')]),
    sourceSha: imageSourceSha,
    sourceTree,
    version: '1.2.0',
  });

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
    /published Web image digest, OCI revision, source tree, or version differs/);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});

test('dry run records current and planned digests for every Deployment without writes', async () => {
  const fixture = makeFixture();
  const receipt = await runWebImageRollout(fixture.options, fixture.overrides);

  assert.equal(receipt.result, 'planned');
  assert.equal(receipt.scope, 'Agentweaver.Web');
  assert.deepEqual(receipt.deployments.before.map(entry => entry.name),
    ['identity-broker', 'orchestrator', 'web']);
  assert.deepEqual(receipt.deployments.before.map(entry => entry.images[0].digest),
    [digest('3'), digest('f'), digest('1')]);
  assert.deepEqual(receipt.deployments.plannedAfter.map(entry => entry.name),
    ['identity-broker', 'orchestrator', 'web']);
  assert.equal(receipt.deployments.plannedAfter.find(entry => entry.name === 'web').images[0].digest, webDigest);
  assert.deepEqual(receipt.deployments.after, []);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl' && call.args[4] !== 'get'), false);
  assert.equal(fixture.written.length, 1);
});

test('kubectl List wrapper is accepted for an apps/v1 Deployment inventory', async () => {
  const fixture = makeFixture();
  const receipt = await runWebImageRollout(fixture.options, fixture.overrides);

  assert.equal(receipt.result, 'planned');
  assert.deepEqual(receipt.deployments.before.map(entry => entry.name),
    ['identity-broker', 'orchestrator', 'web']);
});

test('typed DeploymentList wrapper remains accepted', async () => {
  const fixture = makeFixture({ inventoryKind: 'DeploymentList' });
  const receipt = await runWebImageRollout(fixture.options, fixture.overrides);

  assert.equal(receipt.result, 'planned');
});

test('an empty Deployment list is rejected', async () => {
  const fixture = makeFixture();
  fixture.inventory.items = [];

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
    /has no valid Deployment list/);
});

test('a Deployment list with non-array items is rejected', async () => {
  const fixture = makeFixture();
  fixture.inventory.items = {};

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
    /has no valid Deployment list/);
});

test('every Deployment list item must be an apps/v1 Deployment in agentweaver', async t => {
  const invalidItem = [
    ['a non-Deployment object', item => { item.kind = 'Pod'; }],
    ['an object with a different API version', item => { item.apiVersion = 'v1'; }],
    ['an object from another namespace', item => { item.metadata.namespace = 'other'; }],
  ];
  for (const [description, mutate] of invalidItem) {
    await t.test(description, async () => {
      const fixture = makeFixture();
      mutate(fixture.inventory.items[0]);

      await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
        /not an apps\/v1 Deployment in namespace agentweaver/);
    });
  }
});

test('an unsupported Deployment list wrapper is rejected', async () => {
  const fixture = makeFixture({ inventoryKind: 'PodList' });

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides),
    /has no valid Deployment list/);
});

test('execution patches only Web and verifies every non-Web Deployment is preserved', async () => {
  const fixture = makeFixture();
  const options = { ...fixture.options, execute: true };
  const receipt = await runWebImageRollout(options, fixture.overrides);

  assert.equal(receipt.result, 'succeeded');
  assert.equal(receipt.changed, true);
  assert.equal(receipt.readiness.status, 'passed');
  assert.deepEqual(receipt.webDeploymentRevision, { before: '1', after: '2' });
  assert.equal(receipt.deployments.before.length, 3);
  assert.equal(receipt.deployments.after.length, 3);
  for (const name of ['identity-broker', 'orchestrator']) {
    const before = receipt.deployments.before.find(entry => entry.name === name);
    const after = receipt.deployments.after.find(entry => entry.name === name);
    assert.deepEqual(after.images, before.images);
    assert.equal(after.settingsSha256, before.settingsSha256);
  }
  const patches = fixture.calls.filter(call => call.command === 'kubectl' && call.args[4] === 'patch');
  assert.equal(patches.length, 1);
  assert.deepEqual(patches[0].args.slice(5, 7), ['deployment', 'web']);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl' &&
    ['apply', 'set', 'delete'].includes(call.args[4])), false);
});

test('execution requires a higher Web revision after changing the image', async () => {
  const fixture = makeFixture({ bumpWebRevision: false });

  await assert.rejects(
    runWebImageRollout({ ...fixture.options, execute: true }, fixture.overrides),
    /Web Deployment controller revision did not increase/,
  );
  assert.deepEqual(fixture.written[0].receipt.webDeploymentRevision, { before: '1', after: '1' });
  assert.equal(fixture.written[0].receipt.result, 'failed');
});

test('a Web annotation change other than the controller revision still fails preservation', async () => {
  const fixture = makeFixture({ mutateWebAnnotation: true });

  await assert.rejects(
    runWebImageRollout({ ...fixture.options, execute: true }, fixture.overrides),
    /Web rollout changed settings other than the selected container image/,
  );
  assert.deepEqual(fixture.written[0].receipt.webDeploymentRevision, { before: '1', after: '2' });
  assert.equal(fixture.written[0].receipt.result, 'failed');
});

test('execution fails and records after digests if a non-Web Deployment changes', async () => {
  const fixture = makeFixture({ mutateNonWeb: true });
  const options = { ...fixture.options, execute: true };

  await assert.rejects(runWebImageRollout(options, fixture.overrides), /non-Web Deployment orchestrator changed/);
  assert.equal(fixture.written[0].receipt.result, 'failed');
  assert.equal(fixture.written[0].receipt.deployments.after.length, 3);
});

test('an unvalidated current Web image fails before any cluster write', async () => {
  const fixture = makeFixture({ currentWebImage: `ghcr.io/attacker/web@${digest('1')}` });

  await assert.rejects(runWebImageRollout({ ...fixture.options, execute: true }, fixture.overrides),
    /current Web container image is not a digest-pinned Agentweaver.Web image/);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl' && call.args[4] === 'patch'), false);
});

test('OCI revision mismatch fails before reading or changing Deployments', async () => {
  const fixture = makeFixture();
  fixture.overrides.verifyImage = async () => ({
    digest: webDigest,
    acceptedDigests: new Set([webDigest]),
    sourceSha: '9'.repeat(40),
    sourceTree,
    version: '1.2.0',
  });

  await assert.rejects(runWebImageRollout({ ...fixture.options, execute: true }, fixture.overrides),
    /OCI revision, source tree, or version differs/);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});

test('a publication source outside v1 fails before reading Deployments', async () => {
  const fixture = makeFixture({ rejectAncestry: sourceSha });

  await assert.rejects(runWebImageRollout(fixture.options, fixture.overrides), /publication source .* is not an ancestor/);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});
