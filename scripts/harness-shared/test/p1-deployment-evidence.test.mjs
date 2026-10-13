import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';

import {
  loadValidatedPublicationImages,
  observeP1ServiceDeployments,
  validateP1PublicationReceipt,
} from '../p1-deployment-evidence.mjs';

const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
const providerSource = 'c'.repeat(40);
const context = 'approved-aks-context';
const namespace = 'agentweaver-v1-p0';
const providerCatalog = { configMapName: 'provider-catalog-v1', sourceCommit: providerSource };
const serviceNames = [
  'identity-broker',
  'events-and-sessions',
  'gateway',
  'mcp',
  'web',
  'orchestrator',
  'environment',
  'knowledge',
  'projects-config',
];
const identityServices = new Set([
  'identity-broker',
  'events-and-sessions',
  'environment',
  'knowledge',
  'orchestrator',
  'projects-config',
]);
const p1ServiceComponents = [
  ['Agentweaver.Identity.Broker', 'identity-broker'],
  ['Agentweaver.EventsAndSessions', 'events-and-sessions'],
  ['Agentweaver.Gateway', 'gateway'],
  ['Agentweaver.Mcp', 'mcp'],
  ['Agentweaver.Web', 'web'],
  ['Agentweaver.Orchestrator', 'orchestrator'],
  ['Agentweaver.Environment', 'environment'],
  ['Agentweaver.Knowledge', 'knowledge'],
  ['Agentweaver.Projects.Config', 'projects-config'],
  ['Agentweaver.AgentHost', 'agenthost'],
];

function digest(bytes) {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}

function imageMetadata(name, treeLabel) {
  const labels = { 'org.opencontainers.image.revision': sourceSha };
  if (treeLabel !== undefined) labels['io.agentweaver.source-tree'] = treeLabel;
  const config = Buffer.from(JSON.stringify({
    os: 'linux',
    architecture: 'amd64',
    config: { Labels: labels },
  }));
  const configDigest = digest(config);
  const manifest = Buffer.from(JSON.stringify({
    schemaVersion: 2,
    mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: { mediaType: 'application/vnd.oci.image.config.v1+json', digest: configDigest, size: config.length },
    layers: [{ digest: `sha256:${name.padEnd(64, '0').slice(0, 64)}`, size: 1 }],
  }));
  return { config, configDigest, manifest, manifestDigest: digest(manifest) };
}

function fixture({ webTreeLabel, gatewayIndex = false, gatewayRuntimeDigest } = {}) {
  const items = [{
    apiVersion: 'v1',
    kind: 'ConfigMap',
    metadata: { name: providerCatalog.configMapName, namespace, uid: 'catalog-uid', resourceVersion: '12' },
    data: { catalog: 'not retained' },
  }];
  const images = [];
  const registryBodies = new Map();
  let gatewayChildDigest = null;
  for (const name of serviceNames) {
    const metadata = imageMetadata(name, name === 'web' ? webTreeLabel : sourceTree);
    const repository = `sabbour/agentweaver/${name}`;
    let registryManifest = metadata.manifest;
    let registryDigest = metadata.manifestDigest;
    let runtimeDigest = metadata.manifestDigest;
    if (name === 'gateway' && gatewayIndex) {
      gatewayChildDigest = metadata.manifestDigest;
      registryManifest = Buffer.from(JSON.stringify({
        schemaVersion: 2,
        mediaType: 'application/vnd.oci.image.index.v1+json',
        manifests: [{
          digest: metadata.manifestDigest,
          mediaType: 'application/vnd.oci.image.manifest.v1+json',
          size: metadata.manifest.length,
          platform: { os: 'linux', architecture: 'amd64' },
        }],
      }));
      registryDigest = digest(registryManifest);
      runtimeDigest = gatewayRuntimeDigest ?? registryDigest;
    }
    const image = `ghcr.io/${repository}@${registryDigest}`;
    const deploymentUid = `deployment-${name}-uid`;
    const replicaSetUid = `replicaset-${name}-uid`;
    const labels = {
      'app.kubernetes.io/name': name,
      'agentweaver.io/service': name,
      'agentweaver.io/workload': name,
      ...(identityServices.has(name) ? { 'azure.workload.identity/use': 'true' } : {}),
    };
    const annotations = name === 'projects-config'
      ? { 'agentweaver.io/provider-catalog-source': providerSource }
      : {};
    const volumes = name === 'projects-config'
      ? [{ name: 'provider-catalog', configMap: { name: providerCatalog.configMapName } }]
      : [];
    items.push({
      apiVersion: 'v1',
      kind: 'Service',
      metadata: {
        name,
        namespace,
        labels: { 'app.kubernetes.io/name': name, 'agentweaver.io/service': name },
      },
    });
    items.push({
      apiVersion: 'apps/v1',
      kind: 'Deployment',
      metadata: {
        name,
        namespace,
        uid: deploymentUid,
        generation: 2,
        labels: { 'app.kubernetes.io/name': name, 'agentweaver.io/service': name },
      },
      spec: {
        replicas: 1,
        selector: { matchLabels: { 'app.kubernetes.io/name': name } },
        template: {
          metadata: { labels, annotations },
          spec: { containers: [{ name, image }], volumes },
        },
      },
      status: { observedGeneration: 2, updatedReplicas: 1, readyReplicas: 1, availableReplicas: 1 },
    });
    items.push({
      apiVersion: 'apps/v1',
      kind: 'ReplicaSet',
      metadata: {
        name: `${name}-replicaset`,
        namespace,
        uid: replicaSetUid,
        labels,
        ownerReferences: [{ kind: 'Deployment', uid: deploymentUid, controller: true }],
      },
      spec: { replicas: 1 },
    });
    items.push({
      apiVersion: 'v1',
      kind: 'Pod',
      metadata: {
        name: `${name}-pod`,
        namespace,
        uid: `pod-${name}-uid`,
        labels,
        annotations,
        ownerReferences: [{ kind: 'ReplicaSet', uid: replicaSetUid, controller: true }],
      },
      spec: { containers: [{ name, image }], volumes, nodeName: 'node-a' },
      status: {
        phase: 'Running',
        conditions: [{ type: 'Ready', status: 'True' }],
        containerStatuses: [{ name, ready: true, imageID: `ghcr.io/${repository}@${runtimeDigest}` }],
      },
    });
    registryBodies.set(`https://ghcr.io/v2/${repository}/manifests/${registryDigest}`, registryManifest);
    if (registryDigest !== metadata.manifestDigest) {
      registryBodies.set(`https://ghcr.io/v2/${repository}/manifests/${metadata.manifestDigest}`, metadata.manifest);
    }
    registryBodies.set(`https://ghcr.io/v2/${repository}/blobs/${metadata.configDigest}`, metadata.config);
    images.push({
      id: `Agentweaver.${name}`,
      name,
      image,
    });
  }
  const hostMetadata = imageMetadata('agenthost', sourceTree);
  images.push({
    id: 'Agentweaver.AgentHost',
    name: 'agenthost',
    image: `ghcr.io/sabbour/agentweaver/agenthost@${hostMetadata.manifestDigest}`,
    sandboxImage: `ghcr.io/sabbour/agentweaver/agenthost@${hostMetadata.manifestDigest}`,
    sandboxImageDigest: hostMetadata.manifestDigest,
    compressedPullBytes: 1024,
  });
  return { items, images, registryBodies, gatewayChildDigest };
}

test('validates exact source-bound publication products before treating images as receipts', async () => {
  const source = fixture();
  let calls = 0;
  const images = await loadValidatedPublicationImages({
    publication: { sourceSha },
    manifest: { sourceSha },
    sourceSha,
    validatePublicationReceipt(publication, manifest, expectedSha) {
      calls += 1;
      assert.deepEqual(publication, { sourceSha });
      assert.deepEqual(manifest, { sourceSha });
      assert.equal(expectedSha, sourceSha);
      return source.images;
    },
  });
  assert.equal(calls, 1);
  assert.equal(images.length, 10);
  await assert.rejects(
    loadValidatedPublicationImages({
      publication: {},
      manifest: {},
      sourceSha,
      validatePublicationReceipt: () => source.images.slice(0, -1),
    }),
    /complete P1 product set/,
  );
  await assert.rejects(
    loadValidatedPublicationImages({
      publication: {},
      manifest: {},
      sourceSha,
    }),
    /validator is unavailable/,
  );
});

test('validates the exact P1 published image rows and source-bound AgentHost measurement', () => {
  const manifest = {
    schemaVersion: 1,
    stage: 'draft',
    components: p1ServiceComponents.map(([id]) => ({ id, kind: 'service', version: '1.0.0', project: `services/${id}.csproj` })),
    compatibility: [],
  };
  const planned = [];
  const published = [];
  for (const [index, [id, name]] of p1ServiceComponents.entries()) {
    const repository = `ghcr.io/sabbour/agentweaver/${name}`;
    const version = '1.0.0';
    const hash = String(index + 1).padStart(2, '0').repeat(32);
    const archiveSha256 = hash;
    const digest = `sha256:${hash}`;
    planned.push({
      id,
      version,
      kind: 'image',
      sha256: archiveSha256,
      destination: `${repository}:${version}`,
    });
    published.push({
      id,
      version,
      kind: 'image',
      archiveSha256,
      image: `${repository}@${digest}`,
      ...(id === 'Agentweaver.AgentHost' ? {
        ociArchiveSha256: 'a'.repeat(64),
        imageConfigDigest: `sha256:${'c'.repeat(64)}`,
        preparedOciMeasurement: {
          kind: 'agenthost-image',
          schemaVersion: 1,
          sourceSha,
          sourceTree,
          image: {
            digest: `sha256:${'d'.repeat(64)}`,
            configDigest: `sha256:${'c'.repeat(64)}`,
            platform: 'linux/amd64',
            compressedPullBytes: 2048,
          },
        },
      } : {}),
    });
  }
  planned.push({
    id: 'Agentweaver.FoundationProbe',
    version: '0.0.1',
    kind: 'image',
    sha256: 'f'.repeat(64),
    destination: 'ghcr.io/sabbour/agentweaver-foundation-probe:0.0.1',
  });
  published.push({
    id: 'Agentweaver.FoundationProbe',
    version: '0.0.1',
    kind: 'image',
    archiveSha256: 'f'.repeat(64),
    image: `ghcr.io/sabbour/agentweaver-foundation-probe@sha256:${'e'.repeat(64)}`,
  });
  const publication = {
    schemaVersion: 1,
    sourceSha,
    provenanceSha256: '9'.repeat(64),
    status: 'published',
    planned,
    published,
  };
  const images = validateP1PublicationReceipt(publication, manifest, sourceSha, sourceTree);
  assert.equal(images.length, 10);
  assert.equal(images.find(image => image.id === 'Agentweaver.AgentHost').compressedPullBytes, 2048);

  const wrongHostMeasurement = structuredClone(publication);
  wrongHostMeasurement.published.find(row => row.id === 'Agentweaver.AgentHost')
    .preparedOciMeasurement.sourceTree = 'f'.repeat(40);
  assert.throws(() => validateP1PublicationReceipt(wrongHostMeasurement, manifest, sourceSha, sourceTree),
    /source-bound OCI archive measurement/);

  const missingService = structuredClone(publication);
  missingService.published = missingService.published.filter(row => row.id !== 'Agentweaver.Gateway');
  assert.throws(() => validateP1PublicationReceipt(missingService, manifest, sourceSha, sourceTree),
    /one source-matched image for Agentweaver.Gateway/);
});

test('records native service UIDs and verifies pod imageIDs plus registry source labels without credentials', async () => {
  const data = fixture();
  const calls = [];
  const result = await observeP1ServiceDeployments({
    context,
    namespace,
    sourceSha,
    sourceTree,
    images: data.images,
    providerCatalog,
    readObjects: async target => {
      assert.deepEqual(target, { context, namespace });
      return data.items;
    },
    async fetchImpl(url, options) {
      calls.push({ url: String(url), options });
      assert.equal(options.redirect, 'error');
      assert.equal(new Headers(options.headers).has('authorization'), false);
      const body = data.registryBodies.get(String(url));
      if (!body) return new Response('missing', { status: 404 });
      const expectedDigest = String(url).split('/').at(-1);
      return new Response(body, {
        status: 200,
        headers: {
          'content-type': String(url).includes('/manifests/')
            ? 'application/vnd.oci.image.manifest.v1+json'
            : 'application/vnd.oci.image.config.v1+json',
          'docker-content-digest': expectedDigest,
        },
      });
    },
    now: () => '2026-10-13T00:00:00.000Z',
  });

  assert.equal(result.status, 'passed');
  assert.equal(result.observedAt, '2026-10-13T00:00:00.000Z');
  assert.equal(result.services.length, 9);
  assert.equal(result.providerCatalog.uid, 'catalog-uid');
  const projects = result.services.find(item => item.componentId === 'Agentweaver.projects-config');
  assert.equal(projects?.deployment.uid, 'deployment-projects-config-uid');
  assert.equal(projects?.pods[0].uid, 'pod-projects-config-uid');
  assert.equal(projects?.pods[0].registry.sourceSha, sourceSha);
  const web = result.services.find(item => item.componentId === 'Agentweaver.web');
  assert.equal(web?.pods[0].registry.sourceTree, sourceTree);
  assert.equal(result.agentHostRuntime.status, 'blocked');
  assert.ok(calls.length >= 18);
  assert.ok(calls.every(call => !new Headers(call.options.headers).has('authorization')));
});

test('accepts only the reviewed index or its verified linux/amd64 child as a runtime imageID', async () => {
  const data = fixture({ gatewayIndex: true, gatewayRuntimeDigest: undefined });
  const observe = (items) => observeP1ServiceDeployments({
    context,
    namespace,
    sourceSha,
    sourceTree,
    images: data.images,
    providerCatalog,
    readObjects: async () => items,
    fetchImpl: async url => {
      const body = data.registryBodies.get(String(url));
      if (!body) return new Response('missing', { status: 404 });
      const expectedDigest = String(url).split('/').at(-1);
      return new Response(body, { status: 200, headers: { 'docker-content-digest': expectedDigest } });
    },
  });

  const valid = structuredClone(data.items);
  const gatewayPod = valid.find(item => item.kind === 'Pod' && item.metadata.name === 'gateway-pod');
  gatewayPod.status.containerStatuses[0].imageID =
    `ghcr.io/sabbour/agentweaver/gateway@${data.gatewayChildDigest}`;
  assert.equal((await observe(valid)).status, 'passed');

  const unlisted = structuredClone(data.items);
  const unlistedGatewayPod = unlisted.find(item => item.kind === 'Pod' && item.metadata.name === 'gateway-pod');
  unlistedGatewayPod.status.containerStatuses[0].imageID =
    `ghcr.io/sabbour/agentweaver/gateway@sha256:${'f'.repeat(64)}`;
  await assert.rejects(observe(unlisted), /running container imageID does not match the reviewed registry manifest/);
});

test('blocks stale, incomplete, foreign, or unverifiable deployment evidence', async () => {
  const data = fixture();
  const observe = (overrides = {}) => observeP1ServiceDeployments({
    context,
    namespace,
    sourceSha,
    sourceTree,
    images: data.images,
    providerCatalog,
    readObjects: async () => data.items,
    fetchImpl: async url => {
      const body = data.registryBodies.get(String(url));
      if (!body) return new Response('missing', { status: 404 });
      const expectedDigest = String(url).split('/').at(-1);
      return new Response(body, { status: 200, headers: { 'docker-content-digest': expectedDigest } });
    },
    ...overrides,
  });
  await assert.rejects(observe({ namespace: 'default' }), /restricted to agentweaver-v1-p0/);
  await assert.rejects(observe({ sourceSha: 'short' }), /source commit and Git tree/);
  await assert.rejects(observe({ providerCatalog: { ...providerCatalog, sourceCommit: 'bad' } }), /provider catalog/);
  await assert.rejects(observe({ images: data.images.slice(1) }), /all nine services/);

  const notReady = structuredClone(data.items);
  notReady.find(item => item.kind === 'Deployment' && item.metadata.name === 'gateway').status.readyReplicas = 0;
  await assert.rejects(
    observe({ readObjects: async () => notReady }),
    /fully available rollout/,
  );

  const staleImage = structuredClone(data.items);
  const stalePod = staleImage.find(item => item.kind === 'Pod' && item.metadata.name === 'gateway-pod');
  stalePod.status.containerStatuses[0].imageID = 'ghcr.io/sabbour/agentweaver/gateway@sha256:' + 'f'.repeat(64);
  await assert.rejects(
    observe({ readObjects: async () => staleImage }),
    /running container imageID does not match the reviewed registry manifest/,
  );

  const wrongLabels = structuredClone(data.items);
  const gatewayPod = wrongLabels.find(item => item.kind === 'Pod' && item.metadata.name === 'gateway-pod');
  gatewayPod.metadata.labels = { ...gatewayPod.metadata.labels };
  delete gatewayPod.metadata.labels['agentweaver.io/workload'];
  await assert.rejects(
    observe({ readObjects: async () => wrongLabels }),
    /ready owner of gateway/,
  );

  const conflictingWebLabel = fixture({ webTreeLabel: 'f'.repeat(40) });
  await assert.rejects(
    observeP1ServiceDeployments({
      context,
      namespace,
      sourceSha,
      sourceTree,
      images: conflictingWebLabel.images,
      providerCatalog,
      readObjects: async () => conflictingWebLabel.items,
      fetchImpl: async url => {
        const body = conflictingWebLabel.registryBodies.get(String(url));
        if (!body) return new Response('missing', { status: 404 });
        const expectedDigest = String(url).split('/').at(-1);
        return new Response(body, { status: 200, headers: { 'docker-content-digest': expectedDigest } });
      },
    }),
    /not bound to the reviewed source/,
  );
});
