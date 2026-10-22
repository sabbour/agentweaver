import assert from 'node:assert/strict';
import { test } from 'node:test';
import { parseBackendRolloutArgs, runBackendImageRollout } from '../deploy-backend-services.mjs';
import { componentImageRepository, componentImageTag } from '../pack.mjs';

const sourceSha = 'a'.repeat(40);
const buildSha = 'b'.repeat(40);
const sourceTree = 'c'.repeat(40);
const buildTree = 'd'.repeat(40);
const headSha = 'e'.repeat(40);
const hexCharacter = index => '0123456789abcdef'[index];
const archiveSha = index => hexCharacter(index + 1).repeat(64);
const digest = index => `sha256:${hexCharacter(index + 1).repeat(64)}`;
const services = [
  { componentId: 'Agentweaver.Gateway', deployment: 'gateway', container: 'gateway', version: '1.3.0' },
  { componentId: 'Agentweaver.Identity.Broker', deployment: 'identity-broker', container: 'identity-broker', version: '1.2.0' },
  { componentId: 'Agentweaver.Mcp', deployment: 'mcp', container: 'mcp', version: '1.0.4' },
  { componentId: 'Agentweaver.Environment', deployment: 'environment', container: 'environment', version: '1.2.0' },
  { componentId: 'Agentweaver.Knowledge', deployment: 'knowledge', container: 'knowledge', version: '1.1.0' },
  { componentId: 'Agentweaver.Projects.Config', deployment: 'projects-config', container: 'projects-config', version: '1.3.0' },
];

function makeDeployment(service, image = `ghcr.io/sabbour/${componentImageRepository(service.componentId)}@${digest(9)}`) {
  const labels = {
    'app.kubernetes.io/name': service.deployment,
    'agentweaver.io/service': service.deployment,
  };
  return {
    apiVersion: 'apps/v1',
    kind: 'Deployment',
    metadata: {
      name: service.deployment,
      namespace: 'agentweaver',
      uid: `deployment-${service.deployment}`,
      resourceVersion: `${service.deployment}-rv1`,
      generation: 1,
      labels,
      annotations: { 'deployment.kubernetes.io/revision': '1', 'test.keep': service.deployment },
    },
    spec: {
      replicas: 1,
      selector: { matchLabels: { 'app.kubernetes.io/name': service.deployment } },
      template: {
        metadata: {
          labels: {
            ...labels,
            'agentweaver.io/workload': service.deployment,
          },
        },
        spec: {
          containers: [{
            name: service.container,
            image,
            env: [{ name: 'KEEP_SETTING', value: service.deployment }],
          }],
        },
      },
    },
    status: { observedGeneration: 1, updatedReplicas: 1, readyReplicas: 1, availableReplicas: 1 },
  };
}

function makePublication() {
  const manifest = {
    stage: 'draft',
    components: services.map(service => ({
      id: service.componentId,
      kind: 'service',
      version: service.version,
    })),
  };
  const planned = [];
  const published = [];
  const imageSources = new Map();
  for (const [index, service] of services.entries()) {
    const imageSourceSha = index === 4 ? sourceSha : buildSha;
    const imageSourceTree = index === 4 ? sourceTree : buildTree;
    const reused = index === 4 ? undefined : {
      sourceSha: imageSourceSha,
      sourceTree: imageSourceTree,
      inputFingerprint: `sha256:${'f'.repeat(64)}`,
      receiptSourceSha: sourceSha,
    };
    const repository = `ghcr.io/sabbour/${componentImageRepository(service.componentId)}`;
    const plan = {
      id: service.componentId,
      version: service.version,
      kind: 'image',
      sha256: archiveSha(index),
      destination: `${repository}:${componentImageTag(service.version, imageSourceSha)}`,
      ...(reused ? { reused } : {}),
    };
    planned.push(plan);
    const image = `${repository}@${digest(index)}`;
    published.push({
      id: service.componentId,
      version: service.version,
      kind: 'image',
      image,
      archiveSha256: archiveSha(index),
      ...(reused ? { reused: structuredClone(reused) } : {}),
    });
    imageSources.set(image, {
      digest: digest(index),
      acceptedDigests: new Set([digest(index)]),
      sourceSha: imageSourceSha,
      sourceTree: imageSourceTree,
      version: service.version,
    });
  }
  return {
    manifest,
    publication: {
      schemaVersion: 1,
      sourceSha,
      status: 'published',
      provenanceSha256: '1'.repeat(64),
      requiredPackageIds: [],
      planned,
      published,
    },
    imageSources,
  };
}

function makePodsAndReplicaSets(deployments, badImageIDs = new Map(), {
  oldReplicaSetPod = false, terminatingOldReplicaSetPod = false,
} = {}) {
  const items = [];
  for (const deployment of deployments) {
    const name = deployment.metadata.name;
    const replicaSetUid = `replicaset-${name}`;
    const container = deployment.spec.template.spec.containers[0];
    items.push({
      apiVersion: 'apps/v1',
      kind: 'ReplicaSet',
      metadata: {
        name: `${name}-rs`,
        namespace: 'agentweaver',
        uid: replicaSetUid,
        annotations: {
          'deployment.kubernetes.io/revision':
            deployment.metadata.annotations['deployment.kubernetes.io/revision'],
        },
        ownerReferences: [{
          apiVersion: 'apps/v1',
          kind: 'Deployment',
          name,
          uid: deployment.metadata.uid,
          controller: true,
        }],
      },
    });
    items.push({
      apiVersion: 'v1',
      kind: 'Pod',
      metadata: {
        name: `${name}-pod`,
        namespace: 'agentweaver',
        uid: `pod-${name}`,
        labels: deployment.spec.selector.matchLabels,
        ownerReferences: [{
          apiVersion: 'apps/v1',
          kind: 'ReplicaSet',
          name: `${name}-rs`,
          uid: replicaSetUid,
          controller: true,
        }],
      },
      spec: { containers: [{ name: container.name, image: container.image }] },
      status: {
        phase: 'Running',
        containerStatuses: [{
          name: container.name,
          ready: true,
          imageID: badImageIDs.get(name) ?? container.image,
        }],
      },
    });
  }
  if (oldReplicaSetPod || terminatingOldReplicaSetPod) {
    const deployment = deployments.find(item => item.metadata.name === 'gateway');
    const service = services.find(item => item.deployment === 'gateway');
    const replicaSetUid = 'old-replicaset-gateway';
    items.push({
      apiVersion: 'apps/v1',
      kind: 'ReplicaSet',
      metadata: {
        name: 'gateway-old-rs',
        namespace: 'agentweaver',
        uid: replicaSetUid,
        annotations: { 'deployment.kubernetes.io/revision': '1' },
        ownerReferences: [{
          apiVersion: 'apps/v1', kind: 'Deployment', name: 'gateway',
          uid: deployment.metadata.uid, controller: true,
        }],
      },
    });
    items.push({
      apiVersion: 'v1',
      kind: 'Pod',
      metadata: {
        name: 'gateway-old-pod',
        namespace: 'agentweaver',
        ...((oldReplicaSetPod || terminatingOldReplicaSetPod)
          ? { deletionTimestamp: '2026-10-22T10:00:00Z' } : {}),
        ownerReferences: [{
          apiVersion: 'apps/v1', kind: 'ReplicaSet', name: 'gateway-old-rs',
          uid: replicaSetUid, controller: true,
        }],
      },
      spec: { containers: [{ name: service.container, image: deployment.spec.template.spec.containers[0].image }] },
      status: {
        phase: 'Running',
        containerStatuses: [{
          name: service.container, ready: true,
          imageID: deployment.spec.template.spec.containers[0].image,
        }],
      },
    });
  }
  return items;
}

function makeFixture({
  rejectAncestry,
  missingPublicationComponent,
  missingDeployment,
  mutateServiceLabel,
  badImageIDs = new Map(),
  publicationOverrides,
  inventoryKind = 'List',
  oldReplicaSetPod,
  terminatingOldReplicaSetPodOnce,
} = {}) {
  const { manifest, publication, imageSources } = makePublication();
  if (missingPublicationComponent) {
    publication.planned = publication.planned.filter(entry => entry.id !== missingPublicationComponent);
    publication.published = publication.published.filter(entry => entry.id !== missingPublicationComponent);
  }
  Object.assign(publication, publicationOverrides);
  const state = services
    .filter(service => service.deployment !== missingDeployment)
    .map(service => makeDeployment(service));
  if (mutateServiceLabel) {
    state.find(item => item.metadata.name === mutateServiceLabel).metadata.labels['agentweaver.io/service'] = 'unexpected';
  }
  const calls = [];
  const written = [];
  const kubeconfigCalls = [];
  const clock = { now: 0 };
  let podInventoryReads = 0;
  const options = parseBackendRolloutArgs([
    '--publication', 'publication.json',
    '--subscription-id', 'p0-subscription',
    '--resource-group', 'p0-resource-group',
    '--cluster', 'p0-cluster',
    '--receipt-dir', 'receipts',
  ]);
  const exec = (command, args, settings = {}) => {
    calls.push({ command, args, settings });
    if (command === 'git') {
      if (args[0] === 'status') return { status: 0, stdout: '', stderr: '' };
      if (args[0] === 'fetch') return { status: 0, stdout: '', stderr: '' };
      if (args[0] === 'rev-parse' && args[1] === 'HEAD') return { status: 0, stdout: `${headSha}\n`, stderr: '' };
      if (args[0] === 'rev-parse' && args[1] === `${sourceSha}^{tree}`) {
        return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
      }
      if (args[0] === 'rev-parse' && args[1] === `${buildSha}^{tree}`) {
        return { status: 0, stdout: `${buildTree}\n`, stderr: '' };
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
      return {
        status: 0,
        stdout: JSON.stringify({
          apiVersion: 'v1',
          kind: inventoryKind,
          items: state,
          metadata: { resourceVersion: 'inventory-rv1' },
        }),
        stderr: '',
      };
    }
    if (action === 'patch') {
      const name = args[6];
      const deployment = state.find(item => item.metadata.name === name);
      assert.deepEqual(args.slice(5, 8), ['deployment', name, '--type=json']);
      const patch = JSON.parse(args[9]);
      const imagePath = `/spec/template/spec/containers/0/image`;
      assert.deepEqual(patch.map(operation => operation.op), ['test', 'test', 'replace']);
      assert.equal(patch[0].path, '/metadata/resourceVersion');
      assert.equal(patch[0].value, deployment.metadata.resourceVersion);
      assert.equal(patch[1].path, imagePath);
      assert.equal(patch[1].value, deployment.spec.template.spec.containers[0].image);
      deployment.spec.template.spec.containers[0].image = patch[2].value;
      deployment.metadata.annotations['deployment.kubernetes.io/revision'] = '2';
      deployment.metadata.resourceVersion = `${name}-rv2`;
      deployment.metadata.generation = 2;
      deployment.status.observedGeneration = 2;
      return { status: 0, stdout: 'patched', stderr: '' };
    }
    if (action === 'rollout') return { status: 0, stdout: 'deployment successfully rolled out', stderr: '' };
    if (action === 'get' && args[5] === 'replicasets,pods') {
      const terminatingOldReplicaSetPod = terminatingOldReplicaSetPodOnce && podInventoryReads === 0;
      const items = makePodsAndReplicaSets(state, badImageIDs, {
        oldReplicaSetPod: oldReplicaSetPod || terminatingOldReplicaSetPod,
        terminatingOldReplicaSetPod,
      });
      podInventoryReads += 1;
      return {
        status: 0,
        stdout: JSON.stringify({
          apiVersion: 'v1',
          kind: 'List',
          items,
        }),
        stderr: '',
      };
    }
    throw new Error(`Unexpected kubectl ${args.join(' ')}`);
  };
  return {
    calls,
    written,
    kubeconfigCalls,
    options,
    state,
    publication,
    podReadCount: () => podInventoryReads,
    overrides: {
      root: process.cwd(),
      exec,
      readFile: () => JSON.stringify(publication),
      verifyImage: async image => {
        const verified = imageSources.get(image);
        assert.ok(verified, `unexpected image ${image}`);
        return verified;
      },
      withKubeconfig: (target, action, settings) => {
        kubeconfigCalls.push({ target, settings });
        return action(['--kubeconfig', 'temporary-user-config']);
      },
      writeReceipt: (file, receipt) => written.push({ file, receipt: structuredClone(receipt) }),
      now: () => new Date('2026-10-22T10:00:00.000Z'),
      nowMs: () => clock.now, sleep: async milliseconds => { clock.now += milliseconds; },
      podDrainTimeoutMs: 4, podDrainPollIntervalMs: 2,
      newRunId: () => 'offline-test',
      log: () => {},
    },
  };
}

test('arguments require one explicit P0 target and reject workload overrides', () => {
  assert.throws(() => parseBackendRolloutArgs(['--service', 'web']), /unknown argument/);
  assert.throws(() => parseBackendRolloutArgs(['--publication', 'receipt.json']), /--subscription-id/);
  assert.throws(() => parseBackendRolloutArgs([
    '--publication', 'receipt.json', '--subscription-id', 'sub', '--resource-group', 'rg',
    '--cluster', 'p0', '--readiness-timeout-seconds', '5',
  ]), /60 to 3600/);
  assert.equal(parseBackendRolloutArgs(['--help']).help, true);
});

test('dry run verifies all six source-bound images and plans without cluster writes', async () => {
  const fixture = makeFixture();
  const receipt = await runBackendImageRollout(fixture.options, fixture.overrides);

  assert.equal(receipt.result, 'planned');
  assert.equal(receipt.publicationSourceSha, sourceSha);
  assert.deepEqual(receipt.images.map(image => image.deployment), services.map(service => service.deployment));
  assert.deepEqual(receipt.deployments.before.map(entry => entry.name), [
    'environment', 'gateway', 'identity-broker', 'knowledge', 'mcp', 'projects-config',
  ]);
  assert.deepEqual(receipt.deployments.plannedAfter.map(entry => entry.name), receipt.deployments.before.map(entry => entry.name));
  assert.deepEqual(receipt.deployments.plannedAfter.map(entry => entry.images[0].digest),
    receipt.deployments.plannedAfter.map(entry => digest(
      services.findIndex(service => service.deployment === entry.name),
    )));
  assert.deepEqual(receipt.deployments.after, []);
  assert.equal(receipt.changed, true);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl' && call.args[4] !== 'get'), false);
  assert.deepEqual(fixture.kubeconfigCalls[0].settings.requiredPermissions, [
    ['get', 'namespace/agentweaver'],
    ['get', 'deployments', 'agentweaver'],
  ]);
  assert.equal(fixture.written.length, 1);
});

test('a missing publication image fails before reading the cluster', async () => {
  const fixture = makeFixture({ missingPublicationComponent: 'Agentweaver.Gateway' });

  await assert.rejects(runBackendImageRollout(fixture.options, fixture.overrides),
    /exactly one planned and published gateway image/);
  assert.equal(fixture.kubeconfigCalls.length, 0);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});

test('a publication source outside v1 fails before reading the cluster', async () => {
  const fixture = makeFixture({ rejectAncestry: sourceSha });

  await assert.rejects(runBackendImageRollout(fixture.options, fixture.overrides),
    /publication source .* is not an ancestor/);
  assert.equal(fixture.kubeconfigCalls.length, 0);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl'), false);
});

test('a missing or misidentified Deployment fails before any patch', async t => {
  for (const [name, options, expected] of [
    ['missing Deployment', { missingDeployment: 'gateway' }, /gateway Deployment is missing/],
    ['changed service label', { mutateServiceLabel: 'gateway' }, /gateway Deployment is missing or its identity/],
  ]) {
    await t.test(name, async () => {
      const fixture = makeFixture(options);
      await assert.rejects(runBackendImageRollout({ ...fixture.options, execute: true }, fixture.overrides), expected);
      assert.equal(fixture.calls.some(call => call.command === 'kubectl' && call.args[4] === 'patch'), false);
      assert.equal(fixture.written[0].receipt.result, 'failed');
    });
  }
});

test('execution patches only the six selected images and verifies readiness and Pod image IDs', async () => {
  const fixture = makeFixture();
  const receipt = await runBackendImageRollout({ ...fixture.options, execute: true }, fixture.overrides);

  assert.equal(receipt.result, 'succeeded');
  assert.equal(receipt.installation.status, 'partial');
  assert.equal(receipt.readiness.status, 'passed');
  assert.equal(receipt.pods.status, 'passed');
  assert.deepEqual(receipt.pods.services.map(service => service.deployment), services.map(service => service.deployment));
  assert.ok(receipt.pods.services.every(service => service.pods.length === 1 && service.pods[0].ready));
  assert.deepEqual(
    fixture.calls.filter(call => call.command === 'kubectl' && call.args[4] === 'patch')
      .map(call => call.args[6]),
    services.map(service => service.deployment),
  );
  assert.equal(fixture.calls.filter(call => call.command === 'kubectl' && call.args[4] === 'rollout').length, 6);
  assert.deepEqual(fixture.kubeconfigCalls[0].settings.requiredPermissions, [
    ['get', 'namespace/agentweaver'],
    ['get', 'deployments', 'agentweaver'],
    ['patch', 'deployments', 'agentweaver'],
    ['watch', 'deployments', 'agentweaver'],
    ['get', 'replicasets', 'agentweaver'],
    ['get', 'pods', 'agentweaver'],
  ]);
  assert.equal(fixture.calls.some(call => call.command === 'kubectl' &&
    ['apply', 'set', 'delete', 'create'].includes(call.args[4])), false);
});

test('backend rollout waits for a terminating old ReplicaSet Pod to disappear', async () => {
  const fixture = makeFixture({ terminatingOldReplicaSetPodOnce: true });
  const receipt = await runBackendImageRollout({ ...fixture.options, execute: true }, fixture.overrides);

  assert.equal(receipt.result, 'succeeded');
  assert.equal(receipt.pods.status, 'passed');
  assert.equal(fixture.podReadCount(), 2);
  assert.deepEqual(fixture.calls
    .filter(call => call.command === 'kubectl' && call.args.includes('replicasets,pods'))
    .map(call => call.settings.timeout), [4, 2]);
});

test('backend rollout times out with a partial receipt while a terminating old Pod remains', async () => {
  const fixture = makeFixture({ oldReplicaSetPod: true });
  await assert.rejects(
    runBackendImageRollout({ ...fixture.options, execute: true }, fixture.overrides),
    /Timed out waiting for backend-service Pods to settle/,
  );

  assert.equal(fixture.written[0].receipt.result, 'failed');
  assert.equal(fixture.written[0].receipt.pods.status, 'failed');
  assert.match(fixture.written[0].receipt.error, /Timed out waiting for backend-service Pods to settle/);
});

test('a Pod image ID that is not a verified published digest fails and records the result', async () => {
  const fixture = makeFixture({ badImageIDs: new Map([['mcp', `ghcr.io/sabbour/agentweaver.mcp@${digest(8)}`]]) });

  await assert.rejects(runBackendImageRollout({ ...fixture.options, execute: true }, fixture.overrides),
    /mcp Pod mcp-pod imageID does not match/);
  assert.equal(fixture.written[0].receipt.result, 'failed');
  assert.equal(fixture.written[0].receipt.readiness.status, 'passed');
  assert.equal(fixture.written[0].receipt.pods.status, 'failed');
  assert.equal(fixture.written[0].receipt.deployments.after.length, 6);
});
