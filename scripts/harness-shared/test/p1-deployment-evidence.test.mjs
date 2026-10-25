import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import test from 'node:test';

import {
  createP1ReleaseDeploymentEvidence,
  loadValidatedPublicationImages,
  observeP1ServiceDeployments,
  prepareP1DeploymentEvidence,
  resolveReviewedSource,
  validateP1IntegrationDeploymentReceipt,
  validateP1PublicationReceipt,
} from '../p1-deployment-evidence.mjs';
import { componentImageRepository, componentImageTag } from '../../release/pack.mjs';
import { summarizeP1SourceEvidence } from '../p1-surface-common.mjs';

const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
const providerSource = 'c'.repeat(40);
const context = 'approved-aks-context';
const namespace = 'agentweaver';
const sourceAnnotation = 'agentweaver.io/provider-catalog-source';
const providerCatalog = { configMapName: 'provider-catalog-v1', sourceCommit: providerSource };

test('allows a clean newer helper checkout only when the exact historical source objects exist', () => {
  const helperSha = 'd'.repeat(40);
  const outputs = new Map([
    ['rev-parse --verify HEAD', helperSha],
    [`rev-parse --verify ${sourceSha}^{commit}`, sourceSha],
    [`rev-parse --verify ${sourceSha}^{tree}`, sourceTree],
    ['status --porcelain --untracked-files=normal', ''],
  ]);
  const spawnSyncImpl = (_executable, args) => ({
    status: 0,
    stdout: `${outputs.get(args.join(' ')) ?? ''}\n`,
  });
  assert.deepEqual(resolveReviewedSource({
    repoRoot: 'C:\\repository',
    expectedSourceSha: sourceSha,
    requireHeadMatch: false,
    spawnSyncImpl,
  }), { sourceSha, sourceTree });
  assert.throws(() => resolveReviewedSource({
    repoRoot: 'C:\\repository',
    expectedSourceSha: sourceSha,
    spawnSyncImpl,
  }), /clean repository does not contain/);
});
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

function imageMetadata(name, treeLabel, revision = sourceSha) {
  const labels = { 'org.opencontainers.image.revision': revision };
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

/** Build deterministic live objects and registry bytes for exact service-source readback tests. */
function fixture({ webTreeLabel = sourceTree, omitWebTreeLabel = false, gatewayIndex = false, gatewayRuntimeDigest, reused = {} } = {}) {
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
    const metadata = reused[name]
      ? imageMetadata(name, reused[name].sourceTree, reused[name].sourceSha)
      : imageMetadata(name, name === 'web' ? (omitWebTreeLabel ? undefined : webTreeLabel) : sourceTree);
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
        uid: `service-${name}-uid`,
        resourceVersion: '8',
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
      ...(reused[name] ? { reusedFrom: reused[name] } : {}),
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
  blobRedirectOrigin,
} = {}) {
  let tokenRepositoryForRead;
  return async (url, options = {}) => {
    const requestUrl = new URL(String(url));
    calls.push({ url: requestUrl.href, options });
    if (requestUrl.origin === 'https://ghcr.io' && requestUrl.pathname === '/token') {
      assert.equal(requestUrl.searchParams.get('service'), 'ghcr.io');
      const scopeMatch = /^repository:(sabbour\/agentweaver[/.-][a-z.-]+):pull$/
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

    if (requestUrl.origin === 'https://ghcr.io' && blobRedirectOrigin
      && requestUrl.pathname.includes('/blobs/')) {
      return new Response(null, {
        status: 307,
        headers: { location: `${blobRedirectOrigin}${requestUrl.pathname}` },
      });
    }
    if (requestUrl.origin === 'https://pkg-containers.githubusercontent.com') {
      assert.equal(options.redirect, 'manual');
      assert.equal(new Headers(options.headers).has('authorization'), false);
      const repositoryMatch = /^\/v2\/(.+)\/(?:manifests|blobs)\/[^/]+$/
        .exec(requestUrl.pathname);
      assert.ok(repositoryMatch, 'redirected blob must retain the descriptor path');
      const body = registryBodies.get(`https://ghcr.io${requestUrl.pathname}`);
      if (!body) return new Response('missing', { status: 404 });
      const expectedDigest = requestUrl.pathname.split('/').at(-1);
      return new Response(body, {
        status: 200,
        headers: {
          'content-type': 'application/vnd.oci.image.config.v1+json',
          'docker-content-digest': expectedDigest,
        },
      });
    }
    assert.equal(options.redirect, 'manual');
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
      destination: `${repository}:${sourceSha}`,
    });
    published.push({
      id,
      version,
      kind: 'image',
      archiveSha256,
      image: `${repository}@${digest}`,
      sourceSha,
      sourceTree,
      tag: sourceSha,
      configDigest: id === 'Agentweaver.AgentHost'
        ? `sha256:${'c'.repeat(64)}`
        : `sha256:${String(index + 21).padStart(2, '0').repeat(32)}`,
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
  const publication = {
    schemaVersion: 1,
    mode: 'images-only',
    sourceSha,
    sourceTree,
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
    /complete result for the exact reviewed source/);
});

/** Create a source-tagged release receipt whose image digests are pinned by the release manifest. */
function releasePublicationFixture() {
  const manifest = {
    schemaVersion: 1,
    stage: 'release',
    evidence: { sourceSha },
    components: [],
    compatibility: [],
  };
  const planned = [];
  const published = [];
  for (const [index, [id, name]] of p1ServiceComponents.entries()) {
    const version = '1.0.0';
    const repository = `ghcr.io/sabbour/${componentImageRepository(id)}`;
    const archiveSha256 = String(index + 1).padStart(2, '0').repeat(32);
    const imageDigest = `sha256:${String(index + 11).padStart(2, '0').repeat(32)}`;
    const configDigest = `sha256:${String(index + 21).padStart(2, '0').repeat(32)}`;
    manifest.components.push({
      id,
      kind: 'service',
      version,
      project: `services/${id}/${id}.csproj`,
      imageDigest,
    });
    planned.push({
      id,
      version,
      kind: 'image',
      sha256: archiveSha256,
      destination: `${repository}:${componentImageTag(version, sourceSha)}`,
    });
    published.push({
      id,
      version,
      kind: 'image',
      archiveSha256,
      image: `${repository}@${imageDigest}`,
      ...(id === 'Agentweaver.AgentHost' ? {
        ociArchiveSha256: 'a'.repeat(64),
        imageConfigDigest: configDigest,
        preparedOciMeasurement: {
          kind: 'agenthost-image',
          schemaVersion: 1,
          sourceSha,
          sourceTree,
          image: {
            digest: imageDigest,
            configDigest,
            version,
            platform: 'linux/amd64',
            compressedPullBytes: 2048,
          },
        },
      } : {}),
    });
  }
  const publication = {
    schemaVersion: 1,
    sourceSha,
    requiredPackageIds: [],
    provenanceSha256: '9'.repeat(64),
    status: 'published',
    planned,
    published,
  };
  return { manifest, publication };
}

/** Mirror the exact readback fields emitted by the live P1 service observer. */
function releaseServiceReadbackFixture(images) {
  return {
    status: 'passed',
    observedAt: '2026-10-19T07:24:28.554Z',
    context,
    namespace,
    sourceSha,
    sourceTree,
    providerCatalog: {
      name: providerCatalog.configMapName,
      uid: 'catalog-uid',
      resourceVersion: '12',
      sourceCommit: providerSource,
      sha256: '1'.repeat(64),
    },
    services: images.filter(image => image.id !== 'Agentweaver.AgentHost').map((image, index) => {
      const manifestDigest = image.image.slice(image.image.lastIndexOf('@') + 1);
      const configDigest = `sha256:${String(index + 31).padStart(2, '0').repeat(32)}`;
      return {
        componentId: image.id,
        service: {
          name: image.name,
          uid: `service-${index}`,
          resourceVersion: '8',
          sha256: '2'.repeat(64),
        },
        deployment: {
          name: image.name,
          uid: `deployment-${index}`,
          sha256: '3'.repeat(64),
          generation: 1,
        },
        replicaSet: {
          name: `${image.name}-set`,
          uid: `replicaset-${index}`,
          sha256: '4'.repeat(64),
        },
        pods: [{
          name: `${image.name}-pod`,
          uid: `pod-${index}`,
          sha256: '5'.repeat(64),
          ready: true,
          image: image.image,
          imageID: image.image,
          registry: {
            manifestDigest,
            platformDigest: manifestDigest,
            configDigest,
            sourceSha,
            sourceTree,
            platform: 'linux/amd64',
          },
        }],
      };
    }),
  };
}

test('accepts release publication and live readbacks only when exact source tags and digests match', () => {
  const fixture = releasePublicationFixture();
  const images = validateP1PublicationReceipt(
    fixture.publication, fixture.manifest, sourceSha, sourceTree);
  assert.equal(images.length, p1ServiceComponents.length);
  assert.equal(images.find(image => image.id === 'Agentweaver.Identity.Broker').result.version, '1.0.0');
  assert.equal(
    fixture.publication.planned[0].destination,
    `ghcr.io/sabbour/${componentImageRepository(fixture.publication.planned[0].id)}:${componentImageTag('1.0.0', sourceSha)}`,
  );
  const serviceEvidence = releaseServiceReadbackFixture(images);
  const deployment = createP1ReleaseDeploymentEvidence({
    sourceSha,
    sourceTree,
    manifest: fixture.manifest,
    publication: fixture.publication,
    context,
    namespace,
    providerCatalog,
    images,
    serviceEvidence,
  });
  assert.equal(deployment.status, 'deployed');
  assert.equal(deployment.resultType, 'release');
  assert.equal(images.find(image => image.id === 'Agentweaver.AgentHost').ociArchiveSha256, 'a'.repeat(64));
  assert.equal(deployment.publicationSha256,
    createHash('sha256').update(JSON.stringify(fixture.publication)).digest('hex'));
  assert.equal(deployment.readbackSha256,
    createHash('sha256').update(JSON.stringify(deployment.readbacks)).digest('hex'));

  const wrongTag = structuredClone(fixture.publication);
  wrongTag.planned[0].destination = `ghcr.io/sabbour/${componentImageRepository(wrongTag.planned[0].id)}:latest`;
  assert.throws(() => validateP1PublicationReceipt(wrongTag, fixture.manifest, sourceSha, sourceTree),
    /release manifest disagree/);

  const wrongDigest = structuredClone(fixture.publication);
  wrongDigest.published[0].image = `ghcr.io/sabbour/${componentImageRepository(wrongDigest.published[0].id)}@sha256:${'f'.repeat(64)}`;
  assert.throws(() => validateP1PublicationReceipt(wrongDigest, fixture.manifest, sourceSha, sourceTree),
    /release manifest disagree/);

  const wrongReadback = structuredClone(serviceEvidence);
  wrongReadback.services[0].pods[0].imageID =
    `ghcr.io/sabbour/${componentImageRepository(wrongReadback.services[0].componentId)}@sha256:${'f'.repeat(64)}`;
  assert.throws(() => createP1ReleaseDeploymentEvidence({
    sourceSha, sourceTree, manifest: fixture.manifest, publication: fixture.publication,
    context, namespace, providerCatalog, images, serviceEvidence: wrongReadback,
  }), /image readback.*differs/);

  const missingResourceHash = structuredClone(serviceEvidence);
  delete missingResourceHash.services[0].service.sha256;
  assert.throws(() => createP1ReleaseDeploymentEvidence({
    sourceSha, sourceTree, manifest: fixture.manifest, publication: fixture.publication,
    context, namespace, providerCatalog, images, serviceEvidence: missingResourceHash,
  }), /release deployment readback.*incomplete/);
});

test('accepts a bootstrap draft only with its separately pinned publication manifest', () => {
  const fixture = releasePublicationFixture();
  const manifest = structuredClone(fixture.manifest);
  manifest.stage = 'draft';
  delete manifest.evidence;
  // Bootstrap starts from a digest-free tracked draft; the publication supplies only deployment pins.
  const deploymentManifest = structuredClone(manifest);
  for (const component of deploymentManifest.components) {
    component.imageDigest = fixture.publication.published.find(row => row.id === component.id)
      .image.split('@').at(-1);
  }
  const images = validateP1PublicationReceipt(
    fixture.publication, manifest, sourceSha, sourceTree, deploymentManifest);
  const deployment = createP1ReleaseDeploymentEvidence({
    sourceSha,
    sourceTree,
    manifest,
    deploymentManifest,
    publication: fixture.publication,
    context,
    namespace,
    providerCatalog,
    images,
    serviceEvidence: releaseServiceReadbackFixture(images),
  });
  assert.equal(deployment.resultType, 'pre-acceptance');
  assert.equal(deployment.manifestSha256,
    createHash('sha256').update(JSON.stringify(manifest)).digest('hex'));
  assert.equal(deployment.deploymentManifestSha256,
    createHash('sha256').update(JSON.stringify(deploymentManifest)).digest('hex'));
  assert.equal(images.find(image => image.id === 'Agentweaver.AgentHost').ociArchiveSha256, 'a'.repeat(64));

  const wrongPin = structuredClone(deploymentManifest);
  wrongPin.components[0].imageDigest = `sha256:${'f'.repeat(64)}`;
  assert.throws(() => validateP1PublicationReceipt(
    fixture.publication, manifest, sourceSha, sourceTree, wrongPin),
  /release manifest disagree/);
  assert.throws(() => validateP1PublicationReceipt(
    fixture.publication, manifest, sourceSha, sourceTree),
  /complete result for the exact reviewed source/);
});

function integrationReceiptFixture() {
  const manifest = {
    schemaVersion: 1,
    stage: 'draft',
    components: p1ServiceComponents.map(([id]) => ({
      id,
      kind: 'service',
      version: '1.0.0',
      project: `services/${id}.csproj`,
    })),
    compatibility: [],
  };
  const planned = [];
  const published = [];
  for (const [index, [id, name]] of p1ServiceComponents.entries()) {
    const repository = `ghcr.io/sabbour/agentweaver/${name}`;
    const archiveSha256 = String(index + 1).padStart(2, '0').repeat(32);
    const imageDigest = `sha256:${String(index + 11).padStart(2, '0').repeat(32)}`;
    const configDigest = `sha256:${String(index + 21).padStart(2, '0').repeat(32)}`;
    planned.push({
      id,
      version: '1.0.0',
      kind: 'image',
      sha256: archiveSha256,
      destination: `${repository}:${sourceSha}`,
    });
    published.push({
      id,
      version: '1.0.0',
      kind: 'image',
      archiveSha256,
      image: `${repository}@${imageDigest}`,
      sourceSha,
      sourceTree,
      tag: sourceSha,
      configDigest,
      ...(id === 'Agentweaver.AgentHost' ? {
        ociArchiveSha256: 'a'.repeat(64),
        imageConfigDigest: configDigest,
        preparedOciMeasurement: {
          kind: 'agenthost-image',
          schemaVersion: 1,
          sourceSha,
          sourceTree,
          image: {
            digest: imageDigest,
            configDigest,
            version: '1.0.0',
            platform: 'linux/amd64',
            compressedPullBytes: 2048,
          },
        },
      } : {}),
    });
  }
  const publication = {
    schemaVersion: 1,
    mode: 'images-only',
    sourceSha,
    sourceTree,
    provenanceSha256: '9'.repeat(64),
    status: 'published',
    planned,
    published,
  };
  const images = validateP1PublicationReceipt(publication, manifest, sourceSha, sourceTree);
  const resources = serviceNames.flatMap(name => ['deployment', 'service', 'pods'].map(kind => ({
    kind,
    name,
    sha256: 'b'.repeat(64),
  })));
  const readbacks = {
    resources,
    sha256: createHash('sha256').update(JSON.stringify(resources)).digest('hex'),
  };
  const receipt = {
    schemaVersion: 1,
    status: 'deployed',
    sourceSha,
    sourceTree,
    manifestSha256: createHash('sha256').update(JSON.stringify(manifest)).digest('hex'),
    provenanceSha256: publication.provenanceSha256,
    publicationSha256: createHash('sha256').update(JSON.stringify(publication)).digest('hex'),
    runtimeManifestSha256: 'c'.repeat(64),
    target: {
      subscriptionId: 'controlled-subscription',
      tenantId: 'controlled-tenant',
      resourceGroup: 'controlled-resource-group',
      clusterName: 'controlled-cluster',
      namespace,
      nodeMin: 2,
      nodeMax: 3,
    },
    registryImages: images.map(image => ({
      id: image.id,
      image: image.id === 'Agentweaver.AgentHost' ? image.sandboxImage : image.image,
      configDigest: image.result.configDigest,
      sourceSha,
      sourceTree,
      version: image.component.version,
    })),
    readbacks,
    completedAt: '2026-10-13T00:00:00.000Z',
  };
  return { manifest, publication, images, receipt };
}

test('accepts an exact integration deployment receipt bound to its publication and service readbacks', () => {
  const fixture = integrationReceiptFixture();
  const evidence = validateP1IntegrationDeploymentReceipt(fixture.receipt, {
    sourceSha,
    sourceTree,
    manifest: fixture.manifest,
    publication: fixture.publication,
    namespace,
    images: fixture.images,
  });
  assert.deepEqual(evidence, {
    status: 'deployed',
    sourceSha,
    sourceTree,
    manifestSha256: fixture.receipt.manifestSha256,
    publicationSha256: fixture.receipt.publicationSha256,
    readbackSha256: fixture.receipt.readbacks.sha256,
    completedAt: fixture.receipt.completedAt,
    target: { clusterName: 'controlled-cluster', namespace },
  });

  const invalidSource = structuredClone(fixture.receipt);
  invalidSource.sourceSha = 'f'.repeat(40);
  assert.throws(() => validateP1IntegrationDeploymentReceipt(invalidSource, {
    sourceSha, sourceTree, manifest: fixture.manifest, publication: fixture.publication, namespace, images: fixture.images,
  }), /exact source, manifest, and publication/);

  const wrongManifest = structuredClone(fixture.receipt);
  wrongManifest.manifestSha256 = 'f'.repeat(64);
  assert.throws(() => validateP1IntegrationDeploymentReceipt(wrongManifest, {
    sourceSha, sourceTree, manifest: fixture.manifest, publication: fixture.publication, namespace, images: fixture.images,
  }), /exact source, manifest, and publication/);

  const wrongPublication = structuredClone(fixture.receipt);
  wrongPublication.publicationSha256 = 'f'.repeat(64);
  assert.throws(() => validateP1IntegrationDeploymentReceipt(wrongPublication, {
    sourceSha, sourceTree, manifest: fixture.manifest, publication: fixture.publication, namespace, images: fixture.images,
  }), /exact source, manifest, and publication/);

  const wrongNamespace = structuredClone(fixture.receipt);
  wrongNamespace.target.namespace = 'default';
  assert.throws(() => validateP1IntegrationDeploymentReceipt(wrongNamespace, {
    sourceSha, sourceTree, manifest: fixture.manifest, publication: fixture.publication, namespace, images: fixture.images,
  }), /exact P1 namespace/);

  const wrongImage = structuredClone(fixture.receipt);
  wrongImage.registryImages[0].image = 'ghcr.io/sabbour/agentweaver/gateway@sha256:' + 'f'.repeat(64);
  assert.throws(() => validateP1IntegrationDeploymentReceipt(wrongImage, {
    sourceSha, sourceTree, manifest: fixture.manifest, publication: fixture.publication, namespace, images: fixture.images,
  }), /differs from its publication receipt/);

  const missingReadback = structuredClone(fixture.receipt);
  missingReadback.readbacks.resources = missingReadback.readbacks.resources
    .filter(resource => !(resource.kind === 'pods' && resource.name === 'gateway'));
  missingReadback.readbacks.resources.push({
    kind: 'replicaset',
    name: 'gateway',
    sha256: 'd'.repeat(64),
  });
  missingReadback.readbacks.sha256 = createHash('sha256')
    .update(JSON.stringify(missingReadback.readbacks.resources)).digest('hex');
  assert.throws(() => validateP1IntegrationDeploymentReceipt(missingReadback, {
    sourceSha, sourceTree, manifest: fixture.manifest, publication: fixture.publication, namespace, images: fixture.images,
  }), /missing the pods readback for gateway/);

  const wrongReadbackDigest = structuredClone(fixture.receipt);
  wrongReadbackDigest.readbacks.sha256 = 'f'.repeat(64);
  assert.throws(() => validateP1IntegrationDeploymentReceipt(wrongReadbackDigest, {
    sourceSha, sourceTree, manifest: fixture.manifest, publication: fixture.publication, namespace, images: fixture.images,
  }), /readback digest does not match/);
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
  const webService = data.items.find(item => item.kind === 'Service' && item.metadata.name === 'web');
  assert.equal(web?.service.sha256,
    createHash('sha256').update(JSON.stringify(webService)).digest('hex'));
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

test('verifies a reused image against the labels of the commit that built it', async () => {
  const buildSha = 'e'.repeat(40);
  const buildTree = 'f'.repeat(40);
  const observe = data => observeP1ServiceDeployments({
    context, namespace, sourceSha, sourceTree, images: data.images, providerCatalog,
    readObjects: async () => data.items,
    fetchImpl: createRegistryFetch(data.registryBodies, []),
    now: () => '2026-10-13T00:00:00.000Z',
  });
  const data = fixture({ reused: { gateway: { sourceSha: buildSha, sourceTree: buildTree } } });
  const result = await observe(data);
  assert.equal(result.status, 'passed');
  const gateway = result.services.find(item => item.componentId === 'Agentweaver.gateway');
  assert.equal(gateway.pods[0].registry.sourceSha, buildSha);
  assert.equal(gateway.pods[0].registry.sourceTree, buildTree);
  assert.equal(result.services.find(item => item.componentId === 'Agentweaver.web').pods[0].registry.sourceSha, sourceSha);

  const unclaimed = fixture({ reused: { gateway: { sourceSha: buildSha, sourceTree: buildTree } } });
  delete unclaimed.images.find(image => image.name === 'gateway').reusedFrom;
  await assert.rejects(observe(unclaimed));
});

test('accepts a release publication that reuses an image tagged by its original build source', () => {
  const buildSha = 'e'.repeat(40);
  const fixtureData = releasePublicationFixture();
  const id = fixtureData.publication.planned.find(entry => entry.id !== 'Agentweaver.AgentHost').id;
  const component = fixtureData.manifest.components.find(entry => entry.id === id);
  const reused = { sourceSha: buildSha, sourceTree: 'f'.repeat(40), inputFingerprint: `sha256:${'1'.repeat(64)}`, receiptSourceSha: buildSha };
  const plan = fixtureData.publication.planned.find(entry => entry.id === id);
  plan.destination = `ghcr.io/sabbour/${componentImageRepository(id)}:${componentImageTag(component.version, buildSha)}`;
  plan.reused = reused;
  fixtureData.publication.published.find(entry => entry.id === id).reused = structuredClone(reused);
  const images = validateP1PublicationReceipt(fixtureData.publication, fixtureData.manifest, sourceSha, sourceTree);
  assert.deepEqual(images.find(image => image.id === id).reusedFrom, { sourceSha: buildSha, sourceTree: reused.sourceTree });

  const sourceTag = structuredClone(fixtureData.publication);
  sourceTag.planned.find(entry => entry.id === id).destination =
    `ghcr.io/sabbour/${componentImageRepository(id)}:${componentImageTag(component.version, sourceSha)}`;
  assert.throws(() => validateP1PublicationReceipt(sourceTag, fixtureData.manifest, sourceSha, sourceTree),
    /release manifest disagree/);
});

function reuseReleaseImage(data, id) {
  const reused = {
    sourceSha: 'e'.repeat(40),
    sourceTree: 'f'.repeat(40),
    inputFingerprint: `sha256:${'1'.repeat(64)}`,
    receiptSourceSha: 'e'.repeat(40),
  };
  const plan = data.publication.planned.find(row => row.id === id);
  const published = data.publication.published.find(row => row.id === id);
  plan.destination = `ghcr.io/sabbour/${componentImageRepository(id)}:${componentImageTag(plan.version, reused.sourceSha)}`;
  plan.reused = reused;
  published.reused = structuredClone(reused);
  if (published.preparedOciMeasurement) {
    published.preparedOciMeasurement.sourceSha = reused.sourceSha;
    published.preparedOciMeasurement.sourceTree = reused.sourceTree;
  }
  return reused;
}

test('release deployment accepts reused service build labels, not release labels or unchecked image rows', () => {
  const data = releasePublicationFixture();
  const id = 'Agentweaver.Gateway';
  const build = reuseReleaseImage(data, id);
  const images = validateP1PublicationReceipt(data.publication, data.manifest, sourceSha, sourceTree);
  const serviceEvidence = releaseServiceReadbackFixture(images);
  const registry = serviceEvidence.services.find(service => service.componentId === id).pods[0].registry;
  Object.assign(registry, { sourceSha: build.sourceSha, sourceTree: build.sourceTree });
  const create = (overrides = {}) => createP1ReleaseDeploymentEvidence({
    sourceSha, sourceTree, ...data, context, namespace, providerCatalog, images, serviceEvidence, ...overrides,
  });
  assert.equal(create().sourceSha, sourceSha);
  assert.equal(create().sourceTree, sourceTree);
  for (const field of ['sourceSha', 'sourceTree']) {
    const correct = registry[field];
    registry[field] = field === 'sourceSha' ? sourceSha : sourceTree;
    assert.throws(() => create(), /image readback.*differs/);
    registry[field] = correct;
  }
  const unchecked = structuredClone(images);
  unchecked.find(image => image.id === id).reusedFrom.sourceSha = sourceSha;
  assert.throws(() => create({ images: unchecked }), /images differ from the validated publication/);
  const editedPublication = structuredClone(data.publication);
  editedPublication.published.find(row => row.id === id).reused.sourceTree = sourceTree;
  assert.throws(() => create({ publication: editedPublication }), /invalid reused-image provenance/);
});

/** Exercise real publication and readback validation with controlled Git, registry, and archive seams. */
function preparationFixture(t, reused) {
  const parent = path.resolve('artifacts', 'release-tests');
  mkdirSync(parent, { recursive: true });
  const root = mkdtempSync(path.join(parent, 'p1-preparation-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const data = releasePublicationFixture();
  data.manifest.components.find(component => component.id === 'Agentweaver.Web').project = 'apps/web/package.json';
  data.manifest.evidence = {
    sourceSha,
    deploymentSha: sourceSha,
    personaRuns: ['api', 'ui', 'mcp'].map(surface => ({
      surface, sourceSha, runUrl: `https://example.test/${surface}`,
    })),
  };
  const live = fixture();
  for (const [id, name] of p1ServiceComponents) {
    const image = live.images.find(image => image.name === name);
    const oldRepository = `sabbour/agentweaver/${name}`;
    const repository = `sabbour/${componentImageRepository(id)}`;
    live.items = JSON.parse(JSON.stringify(live.items).replaceAll(oldRepository, repository));
    live.registryBodies = new Map([...live.registryBodies].map(([url, bytes]) => [
      url.replace(oldRepository, repository), bytes,
    ]));
    image.image = image.image.replace(oldRepository, repository);
    data.manifest.components.find(component => component.id === id).imageDigest = image.image.split('@')[1];
    data.publication.published.find(row => row.id === id).image = image.image;
  }
  const host = data.publication.published.find(row => row.id === 'Agentweaver.AgentHost');
  host.preparedOciMeasurement.image.digest = host.image.split('@')[1];
  const build = reused ? reuseReleaseImage(data, host.id) : { sourceSha, sourceTree };
  const archive = Buffer.from('controlled archive bytes');
  host.ociArchiveSha256 = createHash('sha256').update(archive).digest('hex');
  const publicationPath = path.join(root, 'publication.json');
  const agentHostArchivePath = path.join(root, 'agenthost.tar');
  writeFileSync(publicationPath, JSON.stringify(data.publication));
  writeFileSync(agentHostArchivePath, archive);
  const tagSha = '1'.repeat(40);
  const resultSha = '2'.repeat(40);
  const releaseTag = 'v1.0.11';
  const resultTag = `agentweaver-publication/${sourceSha}/result`;
  const gitOutputs = new Map([
    ['rev-parse --verify HEAD', 'd'.repeat(40)],
    [`rev-parse --verify ${sourceSha}^{commit}`, sourceSha],
    [`rev-parse --verify ${sourceSha}^{tree}`, sourceTree],
    ['status --porcelain --untracked-files=normal', ''],
    [`show ${sourceSha}:manifest.json`, JSON.stringify(data.manifest)],
    [`show ${sourceSha}:apps/web/package.json`, JSON.stringify({ version: '1.0.0' })],
    [`show ${sourceSha}:apps/web/package-lock.json`,
      JSON.stringify({ version: '1.0.0', packages: { '': { version: '1.0.0' } } })],
    ...data.manifest.components.filter(component => component.id !== 'Agentweaver.Web')
      .map(component => [`show ${sourceSha}:${component.project}`, '<Project><Version>1.0.0</Version></Project>']),
  ]);
  const options = {
    repoRoot: root, expectedSourceSha: sourceSha, publicationPath,
    manifestPath: 'manifest.json', releaseTag, agentHostArchivePath, providerCatalog, context, namespace,
    readObjects: async () => live.items,
    fetchImpl: createRegistryFetch(live.registryBodies, []),
    spawnSyncImpl(executable, args) {
      let output;
      if (executable === 'git') {
        assert.ok(gitOutputs.has(args.join(' ')), `Unexpected Git query: ${args.join(' ')}`);
        output = gitOutputs.get(args.join(' '));
      } else {
        assert.equal(executable, 'gh');
        const endpoint = args[1];
        if (endpoint.endsWith(`/releases/tags/${releaseTag}`)) {
          output = { tag_name: releaseTag, draft: false, prerelease: false };
        } else if (endpoint.endsWith(`/git/ref/tags/${releaseTag}`)) {
          output = { object: { type: 'tag', sha: tagSha } };
        } else if (endpoint.endsWith(`/git/tags/${tagSha}`)) {
          output = { tag: releaseTag, object: { type: 'commit', sha: sourceSha } };
        } else if (endpoint.endsWith(`/git/ref/tags/${encodeURIComponent(resultTag)}`)) {
          output = { object: { type: 'tag', sha: resultSha } };
        } else {
          assert.ok(endpoint.endsWith(`/git/tags/${resultSha}`), 'Unexpected GitHub query');
          output = {
            tag: resultTag, object: { type: 'commit', sha: sourceSha },
            message: JSON.stringify(data.publication),
          };
        }
        output = JSON.stringify(output);
      }
      return { status: 0, stdout: output };
    },
    measureAgentHostArchive(_archive, _root, expectedSha) {
      assert.equal(expectedSha, build.sourceSha);
      return structuredClone(host.preparedOciMeasurement);
    },
  };
  const pins = {
    context, namespace, sourceReceipt: { sourceSha, sourceTree },
    providerReceipt: { ...providerCatalog, uid: 'catalog-uid', resourceVersion: '12' },
    imageReceipt: {
      registryImage: host.image, registryDigest: host.preparedOciMeasurement.image.digest,
      archiveSha256: host.ociArchiveSha256,
      manifestDigest: host.preparedOciMeasurement.image.digest,
      configDigest: host.preparedOciMeasurement.image.configDigest, compressedPullBytes: 2048,
    },
  };
  return { options, pins, build, host };
}

for (const reused of [false, true]) {
  test(`preparation and downstream evidence preserve ${reused ? 'reused' : 'new'} AgentHost build source without weakening release pins`, async t => {
    const { options, pins, build } = preparationFixture(t, reused);
    const evidence = await prepareP1DeploymentEvidence(options);
    const summary = summarizeP1SourceEvidence(evidence, pins);
    assert.equal(summary.agentHostImageMeasurement.sourceSha, build.sourceSha);
    assert.equal(summary.agentHostImageMeasurement.sourceTree, build.sourceTree);
    assert.deepEqual(evidence.source, pins.sourceReceipt);
    assert.equal(summary.releaseDeployment.sourceSha, sourceSha);
    assert.equal(summary.releaseDeployment.sourceTree, sourceTree);
    if (reused) {
      // Copying JSON cannot turn a supplied older source into a preparation-validated binding.
      assert.throws(() => summarizeP1SourceEvidence(structuredClone(evidence), pins), /exact input pin/);
      assert.throws(() => summarizeP1SourceEvidence({
        ...evidence, agentHostBuildSource: build, reusedFrom: build,
      }, pins), /exact input pin/);
    }
    for (const field of ['sourceSha', 'sourceTree']) {
      const original = evidence.agentHostImageMeasurement[field];
      evidence.agentHostImageMeasurement[field] = '9'.repeat(40);
      assert.throws(() => summarizeP1SourceEvidence(evidence, pins), /exact input pin/);
      evidence.agentHostImageMeasurement[field] = original;
      const measure = options.measureAgentHostArchive;
      await assert.rejects(prepareP1DeploymentEvidence({
        ...options,
        measureAgentHostArchive(...args) { return { ...measure(...args), [field]: '9'.repeat(40) }; },
      }), /archive does not match/);
    }
    for (const receipt of [evidence.source, evidence.releaseDeployment, evidence.services]) {
      receipt.sourceSha = build.sourceSha === sourceSha ? '9'.repeat(40) : build.sourceSha;
      assert.throws(() => summarizeP1SourceEvidence(evidence, pins), /exact input pin/);
      receipt.sourceSha = sourceSha;
    }
  });
}

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
    /Registry\/manifest read failed for https:\/\/ghcr\.io\/token after 4 attempt\(s\): HTTP 503/,
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

test('follows only the GHCR blob redirect without forwarding authorization and still checks bytes', async () => {
  const data = fixture();
  const calls = [];
  const redirected = await observeP1ServiceDeployments({
    context,
    namespace,
    sourceSha,
    sourceTree,
    images: data.images,
    providerCatalog,
    readObjects: async () => data.items,
    fetchImpl: createRegistryFetch(data.registryBodies, calls, {
      blobRedirectOrigin: 'https://pkg-containers.githubusercontent.com',
    }),
  });
  assert.equal(redirected.status, 'passed');
  const blobCalls = calls.filter(call =>
    new URL(call.url).origin === 'https://pkg-containers.githubusercontent.com');
  assert.equal(blobCalls.length, serviceNames.length);
  assert.ok(blobCalls.every(call => !new Headers(call.options.headers).has('authorization')));

  const changedBodies = new Map(data.registryBodies);
  const configUrl = [...changedBodies.keys()].find(url => url.includes('/identity-broker/blobs/'));
  changedBodies.set(configUrl, Buffer.concat([changedBodies.get(configUrl), Buffer.from(' ')]));
  await assert.rejects(
    observeP1ServiceDeployments({
      context, namespace, sourceSha, sourceTree, images: data.images, providerCatalog,
      readObjects: async () => data.items,
      fetchImpl: createRegistryFetch(changedBodies, [], {
        blobRedirectOrigin: 'https://pkg-containers.githubusercontent.com',
      }),
    }),
    /Registry response bytes do not match the immutable image descriptor/,
  );
});

test('rejects a GHCR blob redirect to a non-allowlisted host before making the redirected request', async () => {
  const data = fixture();
  const calls = [];
  await assert.rejects(
    observeP1ServiceDeployments({
      context, namespace, sourceSha, sourceTree, images: data.images, providerCatalog,
      readObjects: async () => data.items,
      fetchImpl: createRegistryFetch(data.registryBodies, calls, {
        blobRedirectOrigin: 'https://registry-redirect.invalid',
      }),
    }),
    /non-allowlisted HTTPS origin/,
  );
  assert.ok(calls.every(call => new URL(call.url).origin === 'https://ghcr.io'));
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
  await assert.rejects(observe({ namespace: 'default' }), /restricted to agentweaver/);
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

  const missingWebLabel = fixture({ omitWebTreeLabel: true });
  await assert.rejects(
    observeP1ServiceDeployments({
      context,
      namespace,
      sourceSha,
      sourceTree,
      images: missingWebLabel.images,
      providerCatalog,
      readObjects: async () => missingWebLabel.items,
      fetchImpl: createRegistryFetch(missingWebLabel.registryBodies),
    }),
    /not bound to the reviewed source/,
  );
});
