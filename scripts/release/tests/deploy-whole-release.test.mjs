import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { parseWholeReleaseArgs, runWholeRelease } from '../deploy-whole-release.mjs';
import { p1PostgresPlanHash } from '../../azure/lib/p1-postgres-plan.mjs';
import { componentImageRepository } from '../pack.mjs';
import { waitForDeploymentPods } from '../image-rollout-common.mjs';
import {
  checkSchemaCompatibility, resolveWholeReleaseImages, WHOLE_RELEASE_SERVICES, writeWholeReleaseReceipt,
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

function runtimeConfigMaps() {
  return [
    { kind: 'ConfigMap', metadata: { name: 'agenthost-runtime-v1', namespace: 'agentweaver' },
      immutable: true, data: { 'appsettings.json': '{}' } },
    { kind: 'ConfigMap', metadata: { name: 'projects-config-runtime-config', namespace: 'agentweaver' },
      immutable: true, data: { ProjectsConfig__Knowledge__BaseAddress: 'https://knowledge.example.test/' } },
    { kind: 'ConfigMap', metadata: { name: 'gateway-runtime-config', namespace: 'agentweaver' },
      immutable: true, data: {
        Gateway__WebOrigin: 'https://portal.example.test',
        Identity__Issuer: 'https://agentweaver.example.test/',
        Identity__Audience: 'https://agentweaver.example.test/',
      } },
  ];
}

function makeFixture({
  failDeployment, unknownDeployment, maintenanceLock, oldReplicaSetPod,
  terminatingOldReplicaSetPodOnce, changedRuntimeConfigMap, unapprovedRuntimeConfigMap,
  mutableRuntimeConfigMap,
} = {}) {
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
  const sourceMaps = runtimeConfigMaps();
  const liveMaps = new Map(sourceMaps.map(resource => [resource.metadata.name, {
    ...structuredClone(resource),
    metadata: {
      ...resource.metadata, uid: `uid-${resource.metadata.name}`,
      resourceVersion: `rv-${resource.metadata.name}`,
    },
  }]));
  if (changedRuntimeConfigMap) {
    const live = liveMaps.get(changedRuntimeConfigMap);
    if (live) delete live.data.ProjectsConfig__Knowledge__BaseAddress;
  }
  if (mutableRuntimeConfigMap) liveMaps.get(mutableRuntimeConfigMap).immutable = false;
  if (unapprovedRuntimeConfigMap) {
    const desired = {
      kind: 'ConfigMap',
      metadata: { name: unapprovedRuntimeConfigMap, namespace: 'agentweaver' },
      immutable: true,
      data: { value: 'source' },
    };
    sourceMaps.push(desired);
    liveMaps.set(unapprovedRuntimeConfigMap, {
      ...structuredClone(desired),
      metadata: {
        ...desired.metadata, uid: `uid-${unapprovedRuntimeConfigMap}`,
        resourceVersion: `rv-${unapprovedRuntimeConfigMap}`,
      },
      data: { value: 'stale' },
    });
  }
  if (unknownDeployment) state.push(makeDeployment(
    { deployment: unknownDeployment, container: unknownDeployment },
    `ghcr.io/sabbour/unknown@${digest(1)}`));
  const calls = [];
  const written = [];
  const clock = { now: 0 };
  let podInventoryReads = 0;
  const exec = (command, args, settings = {}) => {
    calls.push({ command, args, settings });
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
    if (args.includes('auth') && args.includes('can-i')) return { status: 0, stdout: 'yes\n', stderr: '' };
    if (action === 'get' && args.includes('lease')) {
      return maintenanceLock
        ? { status: 0, stdout: JSON.stringify({ spec: { holderIdentity: 'active-maintenance-run' } }), stderr: '' }
        : { status: 1, stdout: '', stderr: 'Error from server (NotFound): leases not found' };
    }
    if (action === 'get' && args.includes('configmap')) {
      const name = args[args.indexOf('configmap') + 1];
      const resource = liveMaps.get(name);
      if (args.includes('--ignore-not-found') && args.includes('--output=name')) {
        return { status: 0, stdout: resource ? `configmap/${name}\n` : '', stderr: '' };
      }
      return resource
        ? { status: 0, stdout: JSON.stringify(resource), stderr: '' }
        : { status: 1, stdout: '', stderr: `NotFound: configmap/${name}` };
    }
    if (action === 'get' && args.includes('deployment')) {
      const name = args[args.indexOf('deployment') + 1];
      const deployment = state.find(item => item.metadata.name === name);
      return deployment
        ? { status: 0, stdout: JSON.stringify(deployment), stderr: '' }
        : { status: 1, stdout: '', stderr: `NotFound: deployment/${name}` };
    }
    if (action === 'get' && args.includes('pods')) {
      const items = state.filter(deployment => deployment.spec.replicas > 0).map(deployment => {
        const service = WHOLE_RELEASE_SERVICES.find(item => item.deployment === deployment.metadata.name);
        const references = service.deployment === 'projects-config';
        return {
          apiVersion: 'v1', kind: 'Pod',
          metadata: {
            name: `${service.deployment}-pod`, namespace: 'agentweaver',
            labels: { 'app.kubernetes.io/name': service.deployment },
          },
          spec: {
            ...(references ? { volumes: [{
              name: 'runtime', configMap: { name: 'projects-config-runtime-config' },
            }] } : {}),
            containers: [{ name: service.container, image: deployment.spec.template.spec.containers[0].image }],
          },
        };
      });
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items }), stderr: '' };
    }
    if (action === 'get' && args.includes('deployments')) {
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items: state }), stderr: '' };
    }
    if (action === 'patch') {
      const name = args[args.indexOf('deployment') + 1];
      if (name === failDeployment) return { status: 1, stdout: '', stderr: 'controlled patch failure' };
      const deployment = state.find(item => item.metadata.name === name);
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      for (const operation of patch) {
        if (operation.op !== 'replace') continue;
        if (operation.path === '/spec/replicas') deployment.spec.replicas = operation.value;
        else if (operation.path.endsWith('/image')) {
          const index = Number(/containers\/(\d+)\/image/.exec(operation.path)?.[1]);
          deployment.spec.template.spec.containers[index].image = operation.value;
          deployment.metadata.generation += 1;
          deployment.metadata.annotations['deployment.kubernetes.io/revision'] = '2';
          deployment.status.observedGeneration = 2;
        }
      }
      deployment.metadata.resourceVersion = `${name}-rv${Number(deployment.metadata.resourceVersion.split('-rv')[1] ?? 1) + 1}`;
      deployment.status.updatedReplicas = deployment.spec.replicas;
      deployment.status.readyReplicas = deployment.spec.replicas;
      deployment.status.availableReplicas = deployment.spec.replicas;
      return { status: 0, stdout: 'patched', stderr: '' };
    }
    if (action === 'rollout') return { status: 0, stdout: 'ready', stderr: '' };
    if (args.includes('delete') && args.includes('--raw')) {
      const name = decodeURIComponent(args[args.indexOf('--raw') + 1].split('/').at(-1));
      const { preconditions } = JSON.parse(settings.input);
      const current = liveMaps.get(name);
      // Mirror the API server: refuse a guarded delete whose observed identity is stale.
      if (current?.metadata?.uid !== preconditions.uid ||
          current?.metadata?.resourceVersion !== preconditions.resourceVersion) {
        return { status: 1, stdout: '', stderr: `Precondition failed for ConfigMap/${name}` };
      }
      liveMaps.delete(name);
      return { status: 0, stdout: `configmap/${name} deleted`, stderr: '' };
    }
    if (args.includes('create') && args.includes('--filename')) {
      const resource = JSON.parse(settings.input);
      liveMaps.set(resource.metadata.name, {
        ...resource,
        metadata: {
          ...resource.metadata, uid: `uid-${resource.metadata.name}`,
          resourceVersion: `new-${resource.metadata.name}`,
        },
      });
      return { status: 0, stdout: `configmap/${resource.metadata.name} created`, stderr: '' };
    }
    if (action === 'get' && args.includes('replicasets,pods')) {
      const items = state.flatMap(deployment => {
        const service = WHOLE_RELEASE_SERVICES.find(item => item.deployment === deployment.metadata.name);
        const uid = `rs-${service.deployment}`;
        return [
          { apiVersion: 'apps/v1', kind: 'ReplicaSet', metadata: {
            name: `${service.deployment}-rs`, namespace: 'agentweaver', uid,
            annotations: {
              'deployment.kubernetes.io/revision':
                deployment.metadata.annotations['deployment.kubernetes.io/revision'],
            },
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
      const includeOldPod = oldReplicaSetPod ||
        (terminatingOldReplicaSetPodOnce && podInventoryReads === 0);
      if (includeOldPod) {
        const service = WHOLE_RELEASE_SERVICES[0];
        const deployment = state.find(item => item.metadata.name === service.deployment);
        const oldUid = `old-rs-${service.deployment}`;
        items.push(
          { apiVersion: 'apps/v1', kind: 'ReplicaSet', metadata: {
            name: `${service.deployment}-old-rs`, namespace: 'agentweaver', uid: oldUid,
            annotations: { 'deployment.kubernetes.io/revision': '1' },
            ownerReferences: [{ kind: 'Deployment', name: service.deployment, uid: deployment.metadata.uid }],
          } },
          { apiVersion: 'v1', kind: 'Pod', metadata: {
            name: `${service.deployment}-old-pod`, namespace: 'agentweaver',
            ...((oldReplicaSetPod || terminatingOldReplicaSetPodOnce)
              ? { deletionTimestamp: '2026-10-22T10:00:00Z' } : {}),
            ownerReferences: [{ kind: 'ReplicaSet', uid: oldUid }],
          }, spec: { containers: [{ name: service.container, image: deployment.spec.template.spec.containers[0].image }] },
          status: { phase: 'Running', containerStatuses: [{
            name: service.container, ready: true,
            imageID: deployment.spec.template.spec.containers[0].image,
          }] } },
        );
      }
      podInventoryReads += 1;
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items }), stderr: '' };
    }
    throw new Error(`unexpected kubectl ${args.join(' ')}`);
  };
  return {
    publication, manifest, images, state, calls, written, sourceMaps, liveMaps,
    options: {
      publicationPath: 'publication.json', subscriptionId: 'sub', resourceGroup: 'rg',
      clusterName: 'cluster', receiptDir: 'receipts', readinessTimeoutSeconds: 60,
      projectsConfigMigrationReceiptPath: 'migration.json',
      gatewayUrl: 'https://gateway.example/', tenantId: 'tenant', issuer: 'issuer',
      audience: 'audience', actorId: 'actor', projectId: 'project', gatewayTokenEnv: 'TEST_TOKEN',
    },
    overrides: {
      root: process.cwd(),
      exec,
      readFile: file => {
        if (!file.endsWith('migration.json')) return JSON.stringify(publication);
        const projectImage = publication.published.find(image => image.id === 'Agentweaver.Projects.Config').image;
        return JSON.stringify({
          schemaVersion: 2, mode: 'execute', result: 'succeeded', runId: 'abcdef123456',
          sourceSha: headSha, publicationSourceSha: sourceSha,
          image: projectImage, imageSourceSha: sourceSha,
          target: {
            subscriptionId: 'sub', resourceGroup: 'rg', clusterName: 'cluster', namespace: 'agentweaver',
          },
          steps: [{ name: 'migration-job-completed' }, { name: 'runtime-grants-applied' }],
          grants: { status: 'exact', sourceSha: headSha, planSha256: p1PostgresPlanHash() },
        });
      },
      verifyImage: async image => images.get(image),
      withKubeconfig: (target, action) => action(['--kubeconfig', 'offline']),
      schemaCheck: async () => [{ deployment: 'orchestrator', accepted: { minSchema: 1, maxSchema: 2 } }],
      prepareSmoke: async () => ({ token: 'test-broker-token' }),
      smoke: async () => [
        { operationId: 'listProjects', status: 200 }, { operationId: 'listChiefOfStaffPriorities', status: 200 },
        { operationId: 'listProjectRuns', status: 200 }, { operationId: 'readProjectMetrics', status: 200 },
      ],
      writeReceipt: (file, receipt) => written.push({ file, receipt: structuredClone(receipt) }),
      now: () => new Date('2026-10-22T10:00:00.000Z'), newRunId: () => 'whole-test', log: () => {},
      readRuntimeConfigMaps: () => sourceMaps,
      nowMs: () => clock.now, sleep: async milliseconds => { clock.now += milliseconds; },
      podDrainTimeoutMs: 4, podDrainPollIntervalMs: 2,
    },
    podReadCount: () => podInventoryReads,
  };
}

test('whole-release arguments require the target while smoke identity flags remain optional overrides', () => {
  assert.equal(parseWholeReleaseArgs(['--help']).help, true);
  assert.throws(() => parseWholeReleaseArgs(['--publication', 'receipt.json']), /--subscription-id/);
  const target = [
    '--publication', 'receipt.json', '--subscription-id', 'sub', '--resource-group', 'rg', '--cluster', 'c',
    '--execute',
  ];
  const options = parseWholeReleaseArgs(target);
  assert.equal(options.execute, true);
  assert.equal(options.gatewayUrl, undefined);
  assert.equal(options.gatewayTokenEnv, 'AGENTWEAVER_GATEWAY_TOKEN');
  const overridden = parseWholeReleaseArgs([
    ...target, '--gateway-url', 'https://gateway.example/', '--tenant-id', 'tenant',
    '--gateway-token-env', 'CUSTOM_TOKEN', '--issuer', 'https://identity.example/',
    '--audience', 'audience', '--actor-id', 'actor', '--project-id', 'project',
  ]);
  assert.equal(overridden.gatewayTokenEnv, 'CUSTOM_TOKEN');
  assert.equal(overridden.projectId, 'project');
  assert.throws(() => parseWholeReleaseArgs([...target, '--gateway-token-env', 'NOT A NAME']),
    /valid environment variable name/);
});

test('whole-release rejects a schema skipped by the target migrator', async () => {
  const service = WHOLE_RELEASE_SERVICES.find(item => item.componentId === 'Agentweaver.Orchestrator');
  const image = `ghcr.io/sabbour/${componentImageRepository(service.componentId)}@${digest(0)}`;
  const sourceSha = 'e'.repeat(40);
  const root = process.cwd();
  const exec = (command, args) => {
    if (command !== 'git') throw new Error(`unexpected command ${command}`);
    if (args[0] === 'cat-file') return { status: 0, stdout: '', stderr: '' };
    if (args[0] === 'show') {
      const sourcePath = args[1].slice(sourceSha.length + 1);
      return { status: 0, stdout: readFileSync(path.resolve(root, sourcePath), 'utf8'), stderr: '' };
    }
    throw new Error(`unexpected git ${args.join(' ')}`);
  };

  await assert.rejects(checkSchemaCompatibility({
    services: [{ ...service, image }],
    kubectl: () => ({
      status: 0, stdout: JSON.stringify({ data: { 'orchestrator.schema': '34' } }), stderr: '',
    }),
    deps: { readImage: async () => ({ sourceSha }) },
    root,
    exec,
  }), /incompatible with recorded schema 34/);
});

test('dry run compares every service and performs no cluster write', async () => {
  const world = makeFixture();
  const receipt = await runWholeRelease(world.options, world.overrides);
  assert.equal(receipt.result, 'planned');
  assert.equal(receipt.status, 'partial');
  assert.equal(receipt.comparison.length, WHOLE_RELEASE_SERVICES.length);
  assert.equal(receipt.configMaps.status, 'planned');
  assert.deepEqual(receipt.configMaps.replacements, []);
  assert.equal(world.calls.some(call => call.command === 'kubectl' && call.args.includes('patch')), false);
});

test('execute requires a matching successful Projects & Config migration receipt before cluster writes', async () => {
  const world = makeFixture();
  const options = { ...world.options, execute: true, projectsConfigMigrationReceiptPath: undefined };
  await assert.rejects(runWholeRelease(options, world.overrides), /migration-receipt is required/);
  assert.equal(world.calls.some(call => call.command === 'kubectl'), false);
});

test('execute rejects a migration receipt without exact runtime grants for the current plan', async () => {
  const world = makeFixture();
  const baseReadFile = world.overrides.readFile;
  // Each variant is a successful migration that would still leave Projects & Config without usable grants.
  const variants = [
    receipt => ({ ...receipt, schemaVersion: 1, grants: undefined, steps: undefined }),
    receipt => ({ ...receipt, grants: undefined }),
    receipt => ({ ...receipt, grants: { ...receipt.grants, status: 'incomplete' } }),
    receipt => ({ ...receipt, grants: { ...receipt.grants, planSha256: 'f'.repeat(64) } }),
    receipt => ({ ...receipt, grants: { ...receipt.grants, sourceSha: 'e'.repeat(40) } }),
    receipt => ({ ...receipt, steps: [{ name: 'migration-job-completed' }] }),
  ];
  for (const variant of variants) {
    world.calls.length = 0;
    const readFile = (file, encoding) => file.endsWith('migration.json')
      ? JSON.stringify(variant(JSON.parse(baseReadFile(file, encoding))))
      : baseReadFile(file, encoding);
    await assert.rejects(runWholeRelease({ ...world.options, execute: true }, { ...world.overrides, readFile }),
      /does not prove (a successful migration|the exact runtime grants)/);
    assert.equal(world.calls.some(call => call.command === 'kubectl'), false);
  }
});

test('whole-release replaces only approved immutable ConfigMaps after cache expiry and consumer drain', async () => {
  const world = makeFixture({ changedRuntimeConfigMap: 'projects-config-runtime-config' });
  const receipt = await runWholeRelease({ ...world.options, execute: true }, world.overrides);

  assert.equal(receipt.result, 'succeeded');
  assert.deepEqual(receipt.configMaps.replacements, ['projects-config-runtime-config']);
  assert.equal(receipt.configMaps.status, 'replaced');
  assert.equal(receipt.configMaps.cacheExpiryWaitSeconds, 70);
  assert.equal(world.liveMaps.get('projects-config-runtime-config').data.ProjectsConfig__Knowledge__BaseAddress,
    'https://knowledge.example.test/');
  const permissionCheck = world.calls.findIndex(call =>
    call.command === 'kubectl' && call.args.includes('can-i') && call.args.includes('create'));
  const drain = world.calls.findIndex(call =>
    call.command === 'kubectl' && call.args.includes('patch') &&
    call.args[call.args.indexOf('deployment') + 1] === 'projects-config');
  const deletion = world.calls.findIndex(call =>
    call.command === 'kubectl' && call.args.includes('delete') && call.args.includes('--raw'));
  const replacementCreate = world.calls.findIndex(call =>
    call.command === 'kubectl' && call.args.includes('create') && call.args.includes('--filename'));
  const restore = world.calls.findIndex((call, index) =>
    index > replacementCreate && call.command === 'kubectl' && call.args.includes('patch') &&
    call.args[call.args.indexOf('deployment') + 1] === 'projects-config');
  assert.ok(permissionCheck >= 0 && permissionCheck < drain);
  assert.ok(drain < deletion && deletion < replacementCreate && replacementCreate < restore);
});

test('whole-release refuses changed immutable ConfigMaps outside the replacement allow-list', async () => {
  const world = makeFixture({ unapprovedRuntimeConfigMap: 'environment-cilium-config' });
  await assert.rejects(runWholeRelease(world.options, world.overrides),
    /Not eligible for guarded replacement: environment-cilium-config/);
  assert.equal(world.calls.some(call => call.command === 'kubectl' && call.args.includes('patch')), false);
  assert.equal(world.calls.some(call => call.command === 'kubectl' && call.args.includes('delete')), false);
});

test('whole-release refuses a live runtime ConfigMap that is no longer immutable', async () => {
  const world = makeFixture({ mutableRuntimeConfigMap: 'projects-config-runtime-config' });
  await assert.rejects(runWholeRelease(world.options, world.overrides),
    /ConfigMap\/projects-config-runtime-config is not immutable/);
  assert.equal(world.calls.some(call =>
    call.command === 'kubectl' && ['patch', 'delete', 'create'].includes(call.args[0])), false);
});

test('whole-release restores drained ConfigMap consumers when an earlier rollout fails', async () => {
  const world = makeFixture({
    changedRuntimeConfigMap: 'projects-config-runtime-config',
    failDeployment: 'identity-broker',
  });
  await assert.rejects(runWholeRelease({ ...world.options, execute: true }, world.overrides),
    /could not patch deployment\/identity-broker/);
  const projectsConfig = world.state.find(item => item.metadata.name === 'projects-config');
  assert.equal(projectsConfig.spec.replicas, 1);
  assert.equal(projectsConfig.spec.template.spec.containers[0].image,
    world.publication.published.find(item => item.id === 'Agentweaver.Projects.Config').image);
  assert.equal(world.written[0].receipt.result, 'failed');
});

test('a failed restore after a failed ConfigMap replacement keeps both causes in the receipt', async () => {
  const world = makeFixture({ changedRuntimeConfigMap: 'projects-config-runtime-config' });
  const originalExec = world.overrides.exec;
  world.overrides.exec = (command, args, settings) => {
    if (command === 'kubectl' && args.includes('delete') && args.includes('--raw')) {
      return { status: 1, stdout: '', stderr: 'Precondition failed: UID in precondition does not match' };
    }
    if (command === 'kubectl' && args.includes('rollout') && args.includes('deployment/projects-config')) {
      return { status: 1, stdout: '', stderr: 'progress deadline exceeded' };
    }
    return originalExec(command, args, settings);
  };
  await assert.rejects(runWholeRelease({ ...world.options, execute: true }, world.overrides),
    /Precondition failed[\s\S]*projects-config did not become ready/);
  const receipt = world.written[0].receipt;
  assert.equal(receipt.result, 'failed');
  assert.match(receipt.error, /Precondition failed: UID in precondition does not match/);
  assert.match(receipt.error, /projects-config did not become ready.*progress deadline exceeded/);
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
  assert.equal(JSON.stringify(receipt).includes('test-broker-token'), false);
  assert.equal(world.written[0].receipt.status, 'deployed');
  assert.equal(world.calls.filter(call => call.command === 'kubectl' && call.args.includes('patch')).length,
    WHOLE_RELEASE_SERVICES.length);
});

test('smoke identity failure writes a partial receipt before any Deployment patch', async () => {
  const world = makeFixture();
  world.overrides.prepareSmoke = async () => {
    throw new Error('P1 identity could not be established.');
  };
  await assert.rejects(runWholeRelease({ ...world.options, execute: true }, world.overrides),
    /Whole-release rollout partial/);

  const receipt = world.written[0].receipt;
  assert.equal(receipt.status, 'partial');
  assert.equal(receipt.smoke.status, 'failed');
  assert.equal(receipt.deployments.after.length, 0);
  assert.equal(world.calls.some(call => call.command === 'kubectl' && call.args.includes('patch')), false);
});

test('a Gateway outage after sign-in still replaces ConfigMaps and rolls out before smoke runs', async t => {
  for (const outcome of ['recovered', 'still-down']) {
    await t.test(outcome, async () => {
      const world = makeFixture({ changedRuntimeConfigMap: 'projects-config-runtime-config' });
      const order = [];
      world.overrides.prepareSmoke = async () => {
        order.push(['prepare', world.calls.length]);
        return { connect: async () => {} };
      };
      world.overrides.smoke = async () => {
        order.push(['smoke', world.calls.length]);
        if (outcome === 'still-down') throw new Error('Gateway authorization context returned HTTP 504.');
        return [{ operationId: 'listProjects', status: 200 }];
      };
      const run = runWholeRelease({ ...world.options, execute: true }, world.overrides);
      if (outcome === 'still-down') await assert.rejects(run, /Gateway authorization context returned HTTP 504/);
      else assert.equal((await run).result, 'succeeded');

      const receipt = world.written.at(-1).receipt;
      const firstWrite = world.calls.findIndex(call => call.command === 'kubectl'
        && ['patch', 'delete', 'create'].some(verb => call.args.includes(verb)));
      const lastPatch = world.calls.findLastIndex(call => call.command === 'kubectl' && call.args.includes('patch'));
      // Sign-in happens before any write; Gateway reads happen after the final rollout patch.
      assert.deepEqual(order.map(([step]) => step), ['prepare', 'smoke']);
      assert.ok(order[0][1] <= firstWrite);
      assert.ok(order[1][1] > lastPatch);
      assert.equal(receipt.configMaps.status, 'replaced');
      assert.equal(receipt.readiness.status, 'passed');
      assert.equal(receipt.pods.status, 'passed');
      if (outcome === 'still-down') {
        assert.equal(receipt.status, 'partial');
        assert.equal(receipt.result, 'failed');
        assert.equal(receipt.smoke.status, 'failed');
        assert.match(receipt.smoke.error, /HTTP 504/);
      } else {
        assert.equal(receipt.status, 'deployed');
        assert.equal(receipt.smoke.status, 'passed');
      }
    });
  }
});

test('whole-release waits for a terminating old ReplicaSet Pod to disappear before strict verification', async () => {
  const world = makeFixture({ terminatingOldReplicaSetPodOnce: true });
  const receipt = await runWholeRelease({ ...world.options, execute: true }, world.overrides);

  assert.equal(receipt.result, 'succeeded');
  assert.equal(receipt.pods.status, 'passed');
  assert.equal(world.podReadCount(), 2);
  assert.deepEqual(world.calls
    .filter(call => call.command === 'kubectl' && call.args.includes('replicasets,pods'))
    .map(call => call.settings.timeout), [4, 2]);
});

test('whole-release rerun verifies Pods and smoke without repatching target digests', async () => {
  const world = makeFixture();
  const originalExec = world.overrides.exec;
  let failPodInventory = true;
  world.overrides.exec = (command, args, settings) => {
    if (failPodInventory && command === 'kubectl' && args.includes('replicasets,pods')) {
      failPodInventory = false;
      throw new Error('kubectl output exceeded the configured limit');
    }
    return originalExec(command, args, settings);
  };
  const executeOptions = { ...world.options, execute: true };

  await assert.rejects(runWholeRelease(executeOptions, world.overrides), /Whole-release rollout partial/);
  assert.equal(world.written[0].receipt.status, 'partial');
  assert.equal(world.written[0].receipt.pods.status, 'failed');
  const patchesAfterFirstRun = world.calls.filter(call =>
    call.command === 'kubectl' && call.args.includes('patch')).length;
  assert.equal(patchesAfterFirstRun, WHOLE_RELEASE_SERVICES.length);

  const retry = await runWholeRelease(executeOptions, world.overrides);
  assert.equal(retry.result, 'succeeded');
  assert.equal(retry.pods.status, 'passed');
  assert.equal(retry.smoke.status, 'passed');
  assert.equal(world.calls.filter(call =>
    call.command === 'kubectl' && call.args.includes('patch')).length, patchesAfterFirstRun);
});

test('Pod settling rejects an inventory that returns clean after its deadline', async () => {
  let now = 0;
  let readTimeout;
  let checkedInventory = false;
  await assert.rejects(waitForDeploymentPods(timeout => {
    readTimeout = timeout;
    now = 120_001;
    return [];
  }, () => {
    checkedInventory = true;
    return null;
  }, {
    label: 'test', timeoutMs: 120_000, intervalMs: 2_000,
    now: () => now, sleep: async () => {},
  }), /Timed out waiting for test Pods to settle/);
  assert.equal(readTimeout, 120_000);
  assert.equal(checkedInventory, false);
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

test('terminating old ReplicaSet Pods that never clear time out with a partial receipt', async () => {
  const world = makeFixture({ oldReplicaSetPod: true });
  await assert.rejects(runWholeRelease({ ...world.options, execute: true }, world.overrides),
    /Timed out waiting for whole-release Pods to settle/);
  assert.equal(world.written[0].receipt.status, 'partial');
  assert.equal(world.written[0].receipt.pods.status, 'failed');
  assert.equal(world.written[0].receipt.smoke.status, 'not-run');
  assert.equal(world.written[0].receipt.error.includes('Timed out waiting for whole-release Pods to settle'), true);
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
