import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { parseWholeReleaseArgs, runWholeRelease } from '../deploy-whole-release.mjs';
import { componentImageRepository } from '../pack.mjs';
import {
  resolveWholeReleaseImages, WHOLE_RELEASE_SERVICES, writeWholeReleaseReceipt,
} from '../whole-release.mjs';

const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
const headSha = 'c'.repeat(40);
const digest = index => `sha256:${'123456789abcdef0'[index].repeat(64)}`;

function makeDeployment(service, image) {
  const labels = {
    'app.kubernetes.io/name': service.deployment,
    'agentweaver.io/service': service.deployment,
  };
  return {
    apiVersion: 'apps/v1', kind: 'Deployment',
    metadata: {
      name: service.deployment, namespace: 'agentweaver', uid: `uid-${service.deployment}`,
      resourceVersion: `rv-${service.deployment}`, generation: 1,
      labels, annotations: { 'deployment.kubernetes.io/revision': '1' },
    },
    spec: {
      replicas: 1, selector: { matchLabels: { 'app.kubernetes.io/name': service.deployment } },
      template: {
        metadata: { labels: { ...labels, 'agentweaver.io/workload': service.deployment } },
        spec: { containers: [{ name: service.container, image }] },
      },
    },
    status: { observedGeneration: 1, updatedReplicas: 1, readyReplicas: 1, availableReplicas: 1 },
  };
}

function makeFixture({ failDeployment, unknownDeployment, maintenanceLock, oldReplicaSetPod } = {}) {
  const manifest = {
    stage: 'draft',
    components: WHOLE_RELEASE_SERVICES.map((service, index) => ({
      id: service.componentId, kind: 'service', version: `1.${index}.0`,
    })),
  };
  const planned = [];
  const published = [];
  const images = new Map();
  for (const [index, service] of WHOLE_RELEASE_SERVICES.entries()) {
    const repository = `ghcr.io/sabbour/${componentImageRepository(service.componentId)}`;
    const image = `${repository}@${digest(index)}`;
    const archive = `${index + 1}`.repeat(64);
    planned.push({
      id: service.componentId, version: `1.${index}.0`, kind: 'image',
      sha256: archive, destination: `${repository}:1.${index}.0`,
    });
    published.push({
      id: service.componentId, version: `1.${index}.0`, kind: 'image',
      image, archiveSha256: archive,
    });
    images.set(image, {
      digest: digest(index), acceptedDigests: new Set([digest(index)]),
      sourceSha, sourceTree, version: `1.${index}.0`,
    });
  }
  const publication = {
    schemaVersion: 1, sourceSha, status: 'published', provenanceSha256: 'd'.repeat(64),
    planned, published,
  };
  const state = WHOLE_RELEASE_SERVICES.map((service, index) =>
    makeDeployment(service, `ghcr.io/sabbour/${componentImageRepository(service.componentId)}@${digest(15 - index)}`));
  if (unknownDeployment) state.push(makeDeployment(
    { deployment: unknownDeployment, container: unknownDeployment },
    `ghcr.io/sabbour/unknown@${digest(1)}`));
  const calls = [];
  const written = [];
  const exec = (command, args) => {
    calls.push({ command, args });
    if (command === 'git') {
      if (args[0] === 'status' || args[0] === 'fetch' || args[0] === 'merge-base') return { status: 0, stdout: '', stderr: '' };
      if (args[0] === 'rev-parse' && args[1] === 'HEAD') return { status: 0, stdout: `${headSha}\n`, stderr: '' };
      if (args[0] === 'rev-parse' && args[1] === `${sourceSha}^{tree}`) return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
      if (args[0] === 'show') return { status: 0, stdout: JSON.stringify(manifest), stderr: '' };
      throw new Error(`unexpected git ${args.join(' ')}`);
    }
    if (command !== 'kubectl') throw new Error(`unexpected command ${command}`);
    const action = args.includes('get') ? 'get' : args.includes('patch') ? 'patch'
      : args.includes('rollout') ? 'rollout' : undefined;
    if (action === 'get' && args.includes('lease')) {
      return maintenanceLock
        ? { status: 0, stdout: JSON.stringify({ spec: { holderIdentity: 'active-maintenance-run' } }), stderr: '' }
        : { status: 1, stdout: '', stderr: 'Error from server (NotFound): leases not found' };
    }
    if (action === 'get' && args.includes('deployments')) {
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items: state }), stderr: '' };
    }
    if (action === 'patch') {
      const name = args[args.indexOf('deployment') + 1];
      if (name === failDeployment) return { status: 1, stdout: '', stderr: 'controlled patch failure' };
      const deployment = state.find(item => item.metadata.name === name);
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      deployment.spec.template.spec.containers[0].image = patch[2].value;
      deployment.metadata.resourceVersion = `${name}-rv2`;
      deployment.metadata.generation = 2;
      deployment.metadata.annotations['deployment.kubernetes.io/revision'] = '2';
      deployment.status.observedGeneration = 2;
      return { status: 0, stdout: 'patched', stderr: '' };
    }
    if (action === 'rollout') return { status: 0, stdout: 'ready', stderr: '' };
    if (action === 'get' && args.includes('replicasets,pods')) {
      const items = state.flatMap(deployment => {
        const service = WHOLE_RELEASE_SERVICES.find(item => item.deployment === deployment.metadata.name);
        const uid = `rs-${service.deployment}`;
        return [
          { apiVersion: 'apps/v1', kind: 'ReplicaSet', metadata: {
            name: `${service.deployment}-rs`, namespace: 'agentweaver', uid,
            ownerReferences: [{ kind: 'Deployment', name: service.deployment, uid: deployment.metadata.uid }],
          } },
          { apiVersion: 'v1', kind: 'Pod', metadata: {
            name: `${service.deployment}-pod`, namespace: 'agentweaver',
            ownerReferences: [{ kind: 'ReplicaSet', uid }],
          }, spec: { containers: [{ name: service.container, image: deployment.spec.template.spec.containers[0].image }] },
          status: { phase: 'Running', containerStatuses: [{
            name: service.container, ready: true,
            imageID: deployment.spec.template.spec.containers[0].image,
          }] } },
        ];
      });
      if (oldReplicaSetPod) {
        const service = WHOLE_RELEASE_SERVICES[0];
        const deployment = state.find(item => item.metadata.name === service.deployment);
        const oldUid = `old-rs-${service.deployment}`;
        items.push(
          { apiVersion: 'apps/v1', kind: 'ReplicaSet', metadata: {
            name: `${service.deployment}-old-rs`, namespace: 'agentweaver', uid: oldUid,
            ownerReferences: [{ kind: 'Deployment', name: service.deployment, uid: deployment.metadata.uid }],
          } },
          { apiVersion: 'v1', kind: 'Pod', metadata: {
            name: `${service.deployment}-old-pod`, namespace: 'agentweaver',
            ownerReferences: [{ kind: 'ReplicaSet', uid: oldUid }],
          }, spec: { containers: [{ name: service.container, image: deployment.spec.template.spec.containers[0].image }] },
          status: { phase: 'Running', containerStatuses: [{
            name: service.container, ready: true,
            imageID: deployment.spec.template.spec.containers[0].image,
          }] } },
        );
      }
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items }), stderr: '' };
    }
    throw new Error(`unexpected kubectl ${args.join(' ')}`);
  };
  return {
    publication, manifest, images, state, calls, written,
    options: {
      publicationPath: 'publication.json', subscriptionId: 'sub', resourceGroup: 'rg',
      clusterName: 'cluster', receiptDir: 'receipts', readinessTimeoutSeconds: 60,
      gatewayUrl: 'https://gateway.example/', tenantId: 'tenant', issuer: 'issuer',
      audience: 'audience', actorId: 'actor', projectId: 'project',
    },
    overrides: {
      root: process.cwd(), exec, readFile: () => JSON.stringify(publication),
      verifyImage: async image => images.get(image),
      withKubeconfig: (target, action) => action(['--kubeconfig', 'offline']),
      schemaCheck: async () => [{ deployment: 'orchestrator', accepted: { minSchema: 1, maxSchema: 2 } }],
      smoke: async () => [
        { operationId: 'listProjects', status: 200 }, { operationId: 'listChiefOfStaffPriorities', status: 200 },
        { operationId: 'listProjectRuns', status: 200 }, { operationId: 'readProjectMetrics', status: 200 },
      ],
      writeReceipt: (file, receipt) => written.push({ file, receipt: structuredClone(receipt) }),
      now: () => new Date('2026-10-22T10:00:00.000Z'), newRunId: () => 'whole-test', log: () => {},
    },
  };
}

test('whole-release arguments require target and authenticated execute smoke inputs', () => {
  assert.equal(parseWholeReleaseArgs(['--help']).help, true);
  assert.throws(() => parseWholeReleaseArgs(['--publication', 'receipt.json']), /--subscription-id/);
  assert.throws(() => parseWholeReleaseArgs([
    '--publication', 'receipt.json', '--subscription-id', 'sub', '--resource-group', 'rg', '--cluster', 'c',
    '--execute',
  ]), /Gateway smoke-check identity/);
});

test('dry run compares every service and performs no cluster write', async () => {
  const world = makeFixture();
  const receipt = await runWholeRelease(world.options, world.overrides);
  assert.equal(receipt.result, 'planned');
  assert.equal(receipt.status, 'partial');
  assert.equal(receipt.comparison.length, WHOLE_RELEASE_SERVICES.length);
  assert.equal(world.calls.some(call => call.command === 'kubectl' && call.args.includes('patch')), false);
});

test('a complete rollout writes deployed only after readiness, Pod, and Gateway checks pass', async () => {
  const world = makeFixture();
  const receipt = await runWholeRelease({ ...world.options, execute: true }, world.overrides);

  assert.equal(receipt.result, 'succeeded');
  assert.equal(receipt.status, 'deployed');
  assert.equal(receipt.installation.status, 'deployed');
  assert.equal(receipt.readiness.status, 'passed');
  assert.equal(receipt.pods.status, 'passed');
  assert.equal(receipt.smoke.status, 'passed');
  assert.equal(world.written[0].receipt.status, 'deployed');
  assert.equal(world.calls.filter(call => call.command === 'kubectl' && call.args.includes('patch')).length,
    WHOLE_RELEASE_SERVICES.length);
});

test('a service planned but omitted from the current publication cannot fall back to a previous image', () => {
  const world = makeFixture();
  const service = WHOLE_RELEASE_SERVICES[0];
  const previous = structuredClone(world.publication);
  world.publication.published = world.publication.published.filter(entry => entry.id !== service.componentId);
  const exec = world.overrides.exec;
  const git = (...args) => {
    const result = exec('git', args, { cwd: process.cwd(), check: false });
    if (result.status !== 0) throw new Error(result.stderr);
    return result.stdout;
  };

  assert.throws(() => resolveWholeReleaseImages(world.publication, world.manifest, previous, {
    git, exec, root: process.cwd(),
  }), /was planned by this publication but has no published image/);
});

test('an unchanged omitted service uses its source-bound image from the previous publication', () => {
  const world = makeFixture();
  const service = WHOLE_RELEASE_SERVICES[0];
  const previousSourceSha = 'e'.repeat(40);
  const previous = structuredClone(world.publication);
  previous.sourceSha = previousSourceSha;
  world.publication.planned = world.publication.planned.filter(entry => entry.id !== service.componentId);
  world.publication.published = world.publication.published.filter(entry => entry.id !== service.componentId);
  const baseExec = world.overrides.exec;
  const exec = (command, args, settings) => {
    if (command === 'git' && args[0] === 'show' &&
        args[1] === `${previousSourceSha}:releases/foundation.json`) {
      return { status: 0, stdout: JSON.stringify(world.manifest), stderr: '' };
    }
    if (command === 'git' && args[0] === 'rev-parse' && args[1] === `${previousSourceSha}^{tree}`) {
      return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
    }
    if (command === 'git' && args[0] === 'merge-base') return { status: 0, stdout: '', stderr: '' };
    return baseExec(command, args, settings);
  };
  const git = (...args) => {
    const result = exec('git', args, { cwd: process.cwd(), check: false });
    if (result.status !== 0) throw new Error(result.stderr);
    return result.stdout;
  };

  const images = resolveWholeReleaseImages(world.publication, world.manifest, previous, {
    git, exec, root: process.cwd(),
  });
  assert.equal(images.find(image => image.deployment === service.deployment).sourcePublicationSha, previousSourceSha);
});

test('previous publication fallback rejects a service whose manifest version changed', () => {
  const world = makeFixture();
  const service = WHOLE_RELEASE_SERVICES[0];
  const previousSourceSha = 'e'.repeat(40);
  const previous = structuredClone(world.publication);
  previous.sourceSha = previousSourceSha;
  const previousManifest = structuredClone(world.manifest);
  const previousVersion = '0.9.0';
  previousManifest.components.find(component => component.id === service.componentId).version = previousVersion;
  const priorPlan = previous.planned.find(entry => entry.id === service.componentId);
  priorPlan.version = previousVersion;
  priorPlan.destination = `ghcr.io/sabbour/${componentImageRepository(service.componentId)}:${previousVersion}`;
  previous.published.find(entry => entry.id === service.componentId).version = previousVersion;
  world.publication.planned = world.publication.planned.filter(entry => entry.id !== service.componentId);
  world.publication.published = world.publication.published.filter(entry => entry.id !== service.componentId);
  const baseExec = world.overrides.exec;
  const exec = (command, args, settings) => {
    if (command === 'git' && args[0] === 'show' &&
        args[1] === `${previousSourceSha}:releases/foundation.json`) {
      return { status: 0, stdout: JSON.stringify(previousManifest), stderr: '' };
    }
    if (command === 'git' && args[0] === 'rev-parse' && args[1] === `${previousSourceSha}^{tree}`) {
      return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
    }
    if (command === 'git' && args[0] === 'merge-base') return { status: 0, stdout: '', stderr: '' };
    return baseExec(command, args, settings);
  };
  const git = (...args) => {
    const result = exec('git', args, { cwd: process.cwd(), check: false });
    if (result.status !== 0) throw new Error(result.stderr);
    return result.stdout;
  };

  assert.throws(() => resolveWholeReleaseImages(world.publication, world.manifest, previous, {
    git, exec, root: process.cwd(),
  }), /changed since the previous publication/);
});

test('published but partially rolled out installation writes partial receipt and fails', async () => {
  const world = makeFixture({ failDeployment: 'gateway' });
  await assert.rejects(runWholeRelease({ ...world.options, execute: true }, world.overrides),
    /Whole-release rollout partial/);
  assert.equal(world.written.length, 1);
  assert.equal(world.written[0].receipt.status, 'partial');
  assert.equal(world.written[0].receipt.result, 'failed');
  assert.equal(world.written[0].receipt.installation.status, 'partial');
});

test('unknown live Deployment fails closed before any patch', async () => {
  const world = makeFixture({ unknownDeployment: 'unmapped' });
  await assert.rejects(runWholeRelease({ ...world.options, execute: true }, world.overrides),
    /unknown or unmapped Deployments/);
  assert.equal(world.calls.some(call => call.command === 'kubectl' && call.args.includes('patch')), false);
});

test('an active database maintenance run blocks whole-release planning and rollout', async () => {
  const world = makeFixture({ maintenanceLock: true });
  await assert.rejects(runWholeRelease({ ...world.options, execute: true }, world.overrides),
    /db:maintenance lock is held/);
  assert.equal(world.written[0].receipt.status, 'partial');
  assert.equal(world.calls.some(call => call.command === 'kubectl' && call.args.includes('patch')), false);
});

test('Pods left from an older ReplicaSet produce a partial whole-release receipt', async () => {
  const world = makeFixture({ oldReplicaSetPod: true });
  await assert.rejects(runWholeRelease({ ...world.options, execute: true }, world.overrides),
    /has 2 Pods; expected 1/);
  assert.equal(world.written[0].receipt.status, 'partial');
  assert.equal(world.written[0].receipt.pods.status, 'failed');
  assert.equal(world.written[0].receipt.smoke.status, 'not-run');
});

test('custom receipt directories also create the whole-release last-receipt directory', () => {
  const temporaryRoot = mkdtempSync(path.join(tmpdir(), 'whole-release-receipt-'));
  try {
    const receipt = { schemaVersion: 1, status: 'partial' };
    const runReceipt = path.join(temporaryRoot, 'custom', 'whole-release-run.json');
    const latestReceipt = path.join(temporaryRoot, 'default', 'last.json');
    writeWholeReleaseReceipt(runReceipt, receipt, undefined, latestReceipt);
    assert.deepEqual(JSON.parse(readFileSync(runReceipt, 'utf8')), receipt);
    assert.deepEqual(JSON.parse(readFileSync(latestReceipt, 'utf8')), receipt);
  } finally {
    rmSync(temporaryRoot, { recursive: true, force: true });
  }
});
