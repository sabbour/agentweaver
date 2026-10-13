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
const sourceAnnotation = 'agentweaver.io/provider-catalog-source';
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
const registryToken = 'controlled-registry-pull-token';

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
      ? { [sourceAnnotation]: providerSource }
      : {};
    const providerCatalogBinding = () => name === 'projects-config'
      ? {
        envFrom: [{
          prefix: 'ProjectsConfig__ProviderCatalog__',
          configMapRef: { name: providerCatalog.configMapName, optional: false },
        }],
      }
      : {};
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
          spec: { containers: [{ name, image, ...providerCatalogBinding() }] },
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
      spec: { containers: [{ name, image, ...providerCatalogBinding() }], nodeName: 'node-a' },
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

function createRegistryFetch(registryBodies, calls = [], {
  tokenEndpointStatus = 200,
  missingToken = false,
  tokenRepository = repository => repository,
} = {}) {
  let tokenRepositoryForRead;
  return async (url, options = {}) => {
    const requestUrl = new URL(String(url));
    calls.push({ url: requestUrl.href, options });
    if (requestUrl.origin === 'https://ghcr.io' && requestUrl.pathname === '/token') {
      assert.equal(requestUrl.searchParams.get('service'), 'ghcr.io');
      const scopeMatch = /^repository:(sabbour\/agentweaver\/[a-z-]+):pull$/
        .exec(requestUrl.searchParams.get('scope') ?? '');
      assert.ok(scopeMatch, 'token request must be scoped to one service repository');
      assert.equal(new Headers(options.headers).has('authorization'), false);
      if (tokenEndpointStatus !== 200) {
        return new Response('token exchange failed', { status: tokenEndpointStatus });
      }
      tokenRepositoryForRead = tokenRepository(scopeMatch[1]);
      return new Response(JSON.stringify(missingToken ? {} : { token: registryToken }), {
        status: tokenEndpointStatus,
        headers: { 'content-type': 'application/json' },
      });
    }

    assert.equal(options.redirect, 'error');
    assert.equal(
      new Headers(options.headers).get('authorization') === `Bearer ${registryToken}`,
      true,
    );
    const repositoryMatch = /^\/v2\/(.+)\/(?:manifests|blobs)\/[^/]+$/
      .exec(requestUrl.pathname);
    assert.ok(repositoryMatch, 'registry read must identify its repository');
    if (tokenRepositoryForRead !== repositoryMatch[1]) {
      return new Response('repository-scoped token required', { status: 401 });
    }
    const body = registryBodies.get(requestUrl.href);
    if (!body) return new Response('missing', { status: 404 });
    const expectedDigest = requestUrl.pathname.split('/').at(-1);
    return new Response(body, {
      status: 200,
      headers: {
        'content-type': requestUrl.pathname.includes('/manifests/')
          ? 'application/vnd.oci.image.manifest.v1+json'
          : 'application/vnd.oci.image.config.v1+json',
        'docker-content-digest': expectedDigest,
      },
    });
  };
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
    fetchImpl: createRegistryFetch(data.registryBodies, calls),
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
  const tokenRequests = calls.filter(call => new URL(call.url).pathname === '/token');
  const imageRequests = calls.filter(call => new URL(call.url).pathname.startsWith('/v2/'));
  assert.equal(tokenRequests.length, serviceNames.length);
  assert.ok(tokenRequests.every(call => !new Headers(call.options.headers).has('authorization')));
  assert.deepEqual(
    tokenRequests.map(call => new URL(call.url).searchParams.get('scope')).sort(),
    serviceNames.map(name => `repository:sabbour/agentweaver/${name}:pull`).sort(),
  );
  assert.ok(imageRequests.length >= 18);
  assert.ok(imageRequests.every(call =>
    new Headers(call.options.headers).get('authorization') === `Bearer ${registryToken}`));
});

test('fails explicitly when anonymous GHCR token exchange fails or omits its token', async () => {
  const data = fixture();
  const observe = fetchImpl => observeP1ServiceDeployments({
    context,
    namespace,
    sourceSha,
    sourceTree,
    images: data.images,
    providerCatalog,
    readObjects: async () => data.items,
    fetchImpl,
  });

  await assert.rejects(
    observe(createRegistryFetch(data.registryBodies, [], { tokenEndpointStatus: 503 })),
    /GHCR does not expose sabbour\/agentweaver\/identity-broker for anonymous pull verification \(HTTP 503\)/,
  );
  await assert.rejects(
    observe(createRegistryFetch(data.registryBodies, [], { missingToken: true })),
    /GHCR returned no pull token for sabbour\/agentweaver\/identity-broker/,
  );
});

test('rejects a pull token that is associated with a different repository', async () => {
  const data = fixture();
  const fetchImpl = createRegistryFetch(data.registryBodies, [], {
    tokenRepository: repository => repository === 'sabbour/agentweaver/identity-broker'
      ? 'sabbour/agentweaver/gateway'
      : repository,
  });

  await assert.rejects(
    observeP1ServiceDeployments({
      context,
      namespace,
      sourceSha,
      sourceTree,
      images: data.images,
      providerCatalog,
      readObjects: async () => data.items,
      fetchImpl,
    }),
    /Authenticated public registry read returned HTTP 401/,
  );
});

test('rejects registry manifest bytes that do not match the immutable descriptor digest', async () => {
  const data = fixture();
  const registryBodies = new Map(data.registryBodies);
  const manifestUrl = `https://ghcr.io/v2/sabbour/agentweaver/identity-broker/manifests/${
    data.images[0].image.split('@')[1]
  }`;
  const manifest = registryBodies.get(manifestUrl);
  assert.ok(manifest);
  registryBodies.set(manifestUrl, Buffer.concat([manifest, Buffer.from(' ')]));

  await assert.rejects(
    observeP1ServiceDeployments({
      context,
      namespace,
      sourceSha,
      sourceTree,
      images: data.images,
      providerCatalog,
      readObjects: async () => data.items,
      fetchImpl: createRegistryFetch(registryBodies),
    }),
    /Registry response bytes do not match the immutable image descriptor/,
  );
});

test('requires the exact provider catalog binding on the Deployment template and ready owner Pod', async () => {
  const data = fixture();
  const observe = items => observeP1ServiceDeployments({
    context,
    namespace,
    sourceSha,
    sourceTree,
    images: data.images,
    providerCatalog,
    readObjects: async () => items,
    fetchImpl: createRegistryFetch(data.registryBodies),
  });
  const mutateProjectsContainer = (items, kind, mutate) => {
    const resource = items.find(item => item.kind === kind
      && item.metadata.name === (kind === 'Deployment' ? 'projects-config' : 'projects-config-pod'));
    const spec = kind === 'Deployment' ? resource.spec.template.spec : resource.spec;
    mutate(spec.containers[0]);
  };

  const badTemplatePrefix = structuredClone(data.items);
  mutateProjectsContainer(badTemplatePrefix, 'Deployment', container => {
    container.envFrom[0].prefix = 'Wrong__Prefix__';
  });
  await assert.rejects(observe(badTemplatePrefix), /exact observed provider catalog source and ConfigMap/);

  const missingTemplateBinding = structuredClone(data.items);
  mutateProjectsContainer(missingTemplateBinding, 'Deployment', container => {
    delete container.envFrom;
  });
  await assert.rejects(observe(missingTemplateBinding), /exact observed provider catalog source and ConfigMap/);

  const optionalTemplateBinding = structuredClone(data.items);
  mutateProjectsContainer(optionalTemplateBinding, 'Deployment', container => {
    container.envFrom[0].configMapRef.optional = true;
  });
  await assert.rejects(observe(optionalTemplateBinding), /exact observed provider catalog source and ConfigMap/);

  const duplicateTemplateBinding = structuredClone(data.items);
  mutateProjectsContainer(duplicateTemplateBinding, 'Deployment', container => {
    container.envFrom.push(structuredClone(container.envFrom[0]));
  });
  await assert.rejects(observe(duplicateTemplateBinding), /exact observed provider catalog source and ConfigMap/);

  const wrongSourceAnnotation = structuredClone(data.items);
  const deployment = wrongSourceAnnotation.find(item =>
    item.kind === 'Deployment' && item.metadata.name === 'projects-config');
  deployment.spec.template.metadata.annotations[sourceAnnotation] = 'd'.repeat(40);
  await assert.rejects(observe(wrongSourceAnnotation), /exact observed provider catalog source and ConfigMap/);

  const foreignPodConfigMap = structuredClone(data.items);
  mutateProjectsContainer(foreignPodConfigMap, 'Pod', container => {
    container.envFrom[0].configMapRef.name = 'other-catalog';
  });
  await assert.rejects(observe(foreignPodConfigMap), /ready owner of projects-config/);

  const missingPodBinding = structuredClone(data.items);
  mutateProjectsContainer(missingPodBinding, 'Pod', container => {
    delete container.envFrom;
  });
  await assert.rejects(observe(missingPodBinding), /ready owner of projects-config/);
});

test('rejects case-insensitive direct provider catalog overrides on the Deployment and Pod', async () => {
  const data = fixture();
  const observe = items => observeP1ServiceDeployments({
    context,
    namespace,
    sourceSha,
    sourceTree,
    images: data.images,
    providerCatalog,
    readObjects: async () => items,
    fetchImpl: createRegistryFetch(data.registryBodies),
  });
  const mutateProjectsContainer = (items, kind, mutate) => {
    const resource = items.find(item => item.kind === kind
      && item.metadata.name === (kind === 'Deployment' ? 'projects-config' : 'projects-config-pod'));
    const spec = kind === 'Deployment' ? resource.spec.template.spec : resource.spec;
    mutate(spec.containers[0]);
  };
  const overrides = [
    { name: 'ProjectsConfig__ProviderCatalog__Registrations__0__Id', value: 'override' },
    {
      name: 'pRoJeCtScOnFiG__pRoViDeRcAtAlOg__Registrations__0__Id',
      valueFrom: { configMapKeyRef: { name: 'other-config', key: 'value' } },
    },
    { name: 'projectsconfig__providercatalog__Registrations__0__Id', value: '' },
  ];

  for (const kind of ['Deployment', 'Pod']) {
    for (const override of overrides) {
      const items = structuredClone(data.items);
      mutateProjectsContainer(items, kind, container => { container.env = [override]; });
      await assert.rejects(
        observe(items),
        kind === 'Deployment'
          ? /exact observed provider catalog source and ConfigMap/
          : /ready owner of projects-config/);
    }

    const unrelatedEnvironment = structuredClone(data.items);
    mutateProjectsContainer(unrelatedEnvironment, kind, container => {
      container.env = [{ name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }];
    });
    const result = await observe(unrelatedEnvironment);
    assert.equal(result.status, 'passed');
  }
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
    fetchImpl: createRegistryFetch(data.registryBodies),
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
    fetchImpl: createRegistryFetch(data.registryBodies),
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
      fetchImpl: createRegistryFetch(conflictingWebLabel.registryBodies),
    }),
    /not bound to the reviewed source/,
  );
});
