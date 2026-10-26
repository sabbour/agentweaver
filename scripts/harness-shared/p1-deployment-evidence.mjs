import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { createReadStream, readFileSync } from 'node:fs';
import path from 'node:path';
import { redact } from './redaction.mjs';
import { componentImageRepository, componentImageTag } from '../release/pack.mjs';
import {
  validateBootstrapManifest,
  validatePublicationReceipt as validateReleasePublicationReceipt,
  verifyBootstrapRelease,
  verifyPublishedSource,
} from '../release/deploy.mjs';
import { pullToken } from '../release/registry.mjs';
import { measureAgentHostArchiveSync } from '../release/measure-agenthost-image.mjs';
import { validateManifest } from '../release/validate.mjs';

/** Read-only checks that bind P1 runtime observations to the reviewed source and published images. */
const SHA = /^[a-f0-9]{40}$/;
const SHA256 = /^[a-f0-9]{64}$/;
const DIGEST = /^sha256:[a-f0-9]{64}$/;
const preparedHostSources = new WeakMap();

/** Only preparation may authorize an older build source; supplied evidence cannot opt into reuse. */
export function p1AgentHostBuildSource(sourceEvidence, releaseSource) {
  const binding = preparedHostSources.get(sourceEvidence);
  return binding?.release.sourceSha === releaseSource.sourceSha
    && binding.release.sourceTree === releaseSource.sourceTree
    ? { ...binding.build } : releaseSource;
}
const IMAGE_REFERENCE = /^ghcr\.io\/([a-z0-9]+(?:[._-][a-z0-9]+)*(?:\/[a-z0-9]+(?:[._-][a-z0-9]+)*)*)@(sha256:[a-f0-9]{64})$/;
const IMAGE_ACCEPT = [
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.oci.image.manifest.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
  'application/vnd.docker.distribution.manifest.v2+json',
].join(', ');
const GHCR_ORIGIN = 'https://ghcr.io';
const GHCR_BLOB_ORIGIN = 'https://pkg-containers.githubusercontent.com';
const MAX_REGISTRY_REDIRECTS = 3;
const SERVICE_NAMES = [
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
const P1_SERVICE_COMPONENTS = [
  { id: 'Agentweaver.Identity.Broker', name: 'identity-broker' },
  { id: 'Agentweaver.EventsAndSessions', name: 'events-and-sessions' },
  { id: 'Agentweaver.Gateway', name: 'gateway' },
  { id: 'Agentweaver.Mcp', name: 'mcp' },
  { id: 'Agentweaver.Web', name: 'web' },
  { id: 'Agentweaver.Orchestrator', name: 'orchestrator' },
  { id: 'Agentweaver.Environment', name: 'environment' },
  { id: 'Agentweaver.Knowledge', name: 'knowledge' },
  { id: 'Agentweaver.Projects.Config', name: 'projects-config' },
  { id: 'Agentweaver.AgentHost', name: 'agenthost' },
];
const IDENTITY_SERVICES = new Set([
  'identity-broker',
  'events-and-sessions',
  'environment',
  'knowledge',
  'orchestrator',
  'projects-config',
]);
const SOURCE_ANNOTATION = 'agentweaver.io/provider-catalog-source';
const PROVIDER_CATALOG_PREFIX = 'ProjectsConfig__ProviderCatalog__';

/** Hash a JSON-compatible value using the exact serialization used by deployment receipts. */
function hashJson(value) {
  return createHash('sha256').update(JSON.stringify(value)).digest('hex');
}

/** Validate the explicit Kubernetes context and namespace before any observation command is run. */
function requireTarget(context, namespace) {
  if (typeof context !== 'string' || !/^[A-Za-z0-9._:/@-]{1,128}$/.test(context)) {
    throw new Error('An exact Kubernetes context is required.');
  }
  if (typeof namespace !== 'string' || !/^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?$/.test(namespace)) {
    throw new Error('An exact Kubernetes namespace is required.');
  }
}

/** Read the selected namespace's workload objects without applying or changing Kubernetes resources. */
function runKubectl({ context, namespace, executable = 'kubectl', spawnSyncImpl = spawnSync }) {
  const args = [
    '--context', context,
    '--namespace', namespace,
    '--request-timeout=15s',
    'get', 'configmaps,services,deployments,replicasets,pods',
    '--output=json',
  ];
  const result = spawnSyncImpl(executable, args, {
    encoding: 'utf8',
    windowsHide: true,
    timeout: 20000,
    maxBuffer: 8 * 1024 * 1024,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  if (result.error || result.status !== 0) {
    throw new Error(`Read-only Kubernetes observation failed: ${redact(result.stderr ?? result.error ?? 'kubectl failed')}`);
  }
  let document;
  try {
    document = JSON.parse(result.stdout);
  } catch {
    throw new Error('Kubernetes returned malformed workload evidence.');
  }
  if (!Array.isArray(document?.items)) throw new Error('Kubernetes workload evidence has no item list.');
  return document.items;
}

/** Parse one immutable GHCR image reference into its repository and digest. */
function imageParts(reference) {
  const match = IMAGE_REFERENCE.exec(reference ?? '');
  if (!match) throw new Error('A reviewed immutable GHCR image reference is required.');
  return { repository: match[1], digest: match[2] };
}

/** Return a prefixed SHA-256 digest for registry response bytes. */
function sha256(bytes) {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}

/**
 * Read immutable GHCR metadata through bounded HTTPS redirects without forwarding credentials off-origin.
 * The final response body must still match the requested descriptor digest byte for byte.
 */
async function registryJson(url, expectedDigest, fetchImpl, token) {
  const currentOrigin = new URL(url).origin;
  if (currentOrigin !== GHCR_ORIGIN) {
    throw new Error('Registry metadata reads must start at HTTPS GHCR.');
  }
  const requestHeaders = new Headers({ Accept: IMAGE_ACCEPT, Authorization: `Bearer ${token}` });
  const signal = AbortSignal.timeout(15000);
  let currentUrl = new URL(url);
  let response;
  try {
    response = await fetchImpl(url, {
      headers: { Accept: IMAGE_ACCEPT, Authorization: `Bearer ${token}` },
      redirect: 'manual',
      signal,
    });
  } catch (error) {
    throw new Error(`Authenticated public registry read failed: ${redact(error).message}`);
  }
  for (let redirects = 0; [301, 302, 303, 307, 308].includes(response.status); redirects += 1) {
    if (redirects >= MAX_REGISTRY_REDIRECTS) {
      throw new Error('GHCR registry redirect limit exceeded.');
    }
    let nextUrl;
    try {
      const location = response.headers.get('location');
      if (!location) throw new Error('missing location');
      nextUrl = new URL(location, currentUrl);
    } catch {
      throw new Error('GHCR returned an invalid registry redirect.');
    }
    const allowedOrigins = currentUrl.origin === GHCR_ORIGIN
      ? [GHCR_ORIGIN, GHCR_BLOB_ORIGIN]
      : [GHCR_BLOB_ORIGIN];
    if (nextUrl.protocol !== 'https:' || nextUrl.username || nextUrl.password
      || nextUrl.hash || !allowedOrigins.includes(nextUrl.origin)) {
      throw new Error('GHCR redirected registry metadata to a non-allowlisted HTTPS origin.');
    }
    if (nextUrl.origin !== currentUrl.origin) requestHeaders.delete('authorization');
    currentUrl = nextUrl;
    try {
      response = await fetchImpl(currentUrl, { headers: requestHeaders, redirect: 'manual', signal });
    } catch (error) {
      throw new Error(`Authenticated public registry read failed: ${redact(error).message}`);
    }
  }
  if (response.status !== 200) {
    throw new Error(`Authenticated public registry read returned HTTP ${response.status}.`);
  }
  const bytes = Buffer.from(await response.arrayBuffer());
  if (sha256(bytes) !== expectedDigest
    || response.headers.get('docker-content-digest') !== null
      && response.headers.get('docker-content-digest') !== expectedDigest) {
    throw new Error('Registry response bytes do not match the immutable image descriptor.');
  }
  let value;
  try {
    value = JSON.parse(bytes.toString('utf8'));
  } catch {
    throw new Error('Registry returned malformed image metadata.');
  }
  return { bytes, value };
}

/** Verify registry manifest and image-config digests, platform, and exact source labels against the reviewed image. */
async function verifyRegistryImage(reference, imageId, sourceSha, sourceTree, fetchImpl) {
  const { repository, digest } = imageParts(reference);
  const base = `${GHCR_ORIGIN}/v2/${repository}`;
  const token = await pullToken(repository, fetchImpl);
  const first = await registryJson(`${base}/manifests/${digest}`, digest, fetchImpl, token);
  let manifest = first.value;
  let manifestDigest = digest;
  let platformDigest = digest;
  const mediaType = manifest.mediaType ?? '';
  if (mediaType === 'application/vnd.oci.image.index.v1+json'
    || mediaType === 'application/vnd.docker.distribution.manifest.list.v2+json') {
    const matches = manifest.manifests?.filter(item =>
      item.platform?.os === 'linux' && item.platform?.architecture === 'amd64') ?? [];
    if (matches.length !== 1 || !DIGEST.test(matches[0].digest ?? '')) {
      throw new Error('The immutable image index does not contain one linux/amd64 manifest.');
    }
    platformDigest = matches[0].digest;
    manifest = (await registryJson(`${base}/manifests/${platformDigest}`, platformDigest, fetchImpl, token)).value;
  }
  const imageIdDigest = imageId?.slice(imageId.lastIndexOf('@') + 1);
  if (imageIdDigest !== manifestDigest && imageIdDigest !== platformDigest) {
    throw new Error('The running container imageID does not match the reviewed registry manifest.');
  }
  const configDigest = manifest.config?.digest;
  if (!DIGEST.test(configDigest ?? '')) throw new Error('The registry manifest has no immutable config digest.');
  const config = (await registryJson(`${base}/blobs/${configDigest}`, configDigest, fetchImpl, token)).value;
  const labels = config.config?.Labels ?? config.config?.labels;
  if (config.os !== 'linux' || config.architecture !== 'amd64'
    || labels?.['org.opencontainers.image.revision'] !== sourceSha
    || labels?.['io.agentweaver.source-tree'] !== sourceTree) {
    throw new Error('The running image configuration is not bound to the reviewed source commit and tree.');
  }
  return { manifestDigest, platformDigest, configDigest, sourceSha, sourceTree, platform: 'linux/amd64' };
}

/** Return controller owner references matching the expected Kubernetes kind and UID. */
function ownerReference(resource, kind, uid) {
  return resource.metadata?.ownerReferences?.filter(reference =>
    reference.kind === kind && reference.uid === uid && reference.controller === true) ?? [];
}

/** Check that an object's labels satisfy every key/value in a nonempty selector. */
function matchesSelector(labels, selector) {
  return selector && Object.entries(selector).length > 0
    && Object.entries(selector).every(([key, value]) => labels?.[key] === value);
}

/** Require a Deployment template to run only the one reviewed application image. */
function deploymentContainers(deployment, image) {
  const containers = deployment.spec?.template?.spec?.containers;
  if (!Array.isArray(containers) || containers.length !== 1 || containers[0].image !== image) {
    throw new Error(`Deployment ${deployment.metadata?.name ?? '[unknown]'} does not have the single reviewed application image.`);
  }
  return containers;
}

/** Require a single mandatory prefixed ConfigMap source for the provider catalog. */
function hasExactProviderCatalogBinding(envFrom, configMapName) {
  if (!Array.isArray(envFrom)) return false;
  const bindings = envFrom.filter(source =>
    source?.prefix === PROVIDER_CATALOG_PREFIX || source?.configMapRef?.name === configMapName);
  return bindings.length === 1
    && bindings[0].prefix === PROVIDER_CATALOG_PREFIX
    && bindings[0].configMapRef?.name === configMapName
    && bindings[0].configMapRef?.optional === false;
}

/** Detect direct environment variables that could override provider catalog values. */
function hasProviderCatalogEnvironmentOverride(env) {
  return Array.isArray(env) && env.some(variable =>
    typeof variable?.name === 'string'
    && variable.name.toLowerCase().startsWith(PROVIDER_CATALOG_PREFIX.toLowerCase()));
}

/** Select namespaced Kubernetes objects of one kind from a readback list. */
function selectRuntimeObjects(items, kind, namespace) {
  return items.filter(item => item.kind === kind && item.metadata?.namespace === namespace);
}

/**
 * Observe the exact P1 Services, Deployments, ReplicaSets, Pods, provider catalog, and registry metadata.
 * Uses read-only Kubernetes and registry calls; incomplete rollouts or mismatched source pins throw.
 */
export async function observeP1ServiceDeployments({
  context,
  namespace,
  sourceSha,
  sourceTree,
  images,
  providerCatalog,
  fetchImpl = globalThis.fetch,
  readObjects = runKubectl,
  now = () => new Date().toISOString(),
}) {
  requireTarget(context, namespace);
  if (namespace !== 'agentweaver') {
    throw new Error('The source-bound P1 runtime list is restricted to agentweaver.');
  }
  if (!SHA.test(sourceSha ?? '') || !SHA.test(sourceTree ?? '')) {
    throw new Error('The reviewed full source commit and Git tree are required.');
  }
  if (typeof providerCatalog?.configMapName !== 'string' || !providerCatalog.configMapName
    || !SHA.test(providerCatalog.sourceCommit ?? '')) {
    throw new Error('The exact provider catalog ConfigMap and source commit are required.');
  }
  if (!Array.isArray(images) || images.length === 0) {
    throw new Error('Validated publication image receipts are required.');
  }
  const services = images.filter(image => image.id !== 'Agentweaver.AgentHost');
  const agentHost = images.filter(image => image.id === 'Agentweaver.AgentHost');
  if (services.length !== SERVICE_NAMES.length || agentHost.length !== 1) {
    throw new Error('The publication receipt must contain all nine services and one AgentHost image.');
  }
  const ids = new Set();
  const names = new Set();
  for (const image of services) {
    if (typeof image.id !== 'string' || !image.id || ids.has(image.id)
      || typeof image.name !== 'string' || !image.name || names.has(image.name)) {
      throw new Error('The publication receipt has a missing or duplicate service identity.');
    }
    ids.add(image.id);
    names.add(image.name);
    imageParts(image.image);
  }
  if (SERVICE_NAMES.some(name => !names.has(name))) {
    throw new Error('The publication receipt does not contain the exact P1 service set.');
  }
  const hostImage = agentHost[0];
  if (!hostImage.sandboxImage || !DIGEST.test(hostImage.sandboxImageDigest ?? '')
    || !Number.isSafeInteger(hostImage.compressedPullBytes) || hostImage.compressedPullBytes <= 0
    || imageParts(hostImage.sandboxImage).digest !== hostImage.sandboxImageDigest) {
    throw new Error('The validated AgentHost image receipt is incomplete or mutable.');
  }

  const items = await readObjects({ context, namespace });
  if (!Array.isArray(items)) throw new Error('Kubernetes workload evidence has no item list.');
  const configMaps = selectRuntimeObjects(items, 'ConfigMap', namespace)
    .filter(configMap => configMap.metadata?.name === providerCatalog.configMapName);
  if (configMaps.length !== 1 || typeof configMaps[0].metadata?.uid !== 'string'
    || !configMaps[0].metadata.uid || typeof configMaps[0].metadata?.resourceVersion !== 'string'
    || !configMaps[0].metadata.resourceVersion) {
    throw new Error('The exact provider catalog ConfigMap was not observed in the selected namespace.');
  }
  const configMapEvidence = {
    name: configMaps[0].metadata.name,
    uid: configMaps[0].metadata.uid,
    resourceVersion: configMaps[0].metadata.resourceVersion,
    sourceCommit: providerCatalog.sourceCommit,
    sha256: hashJson(configMaps[0]),
  };
  const kubernetesServices = selectRuntimeObjects(items, 'Service', namespace);
  const serviceReadbacks = new Map();
  for (const name of SERVICE_NAMES) {
    const serviceMatches = kubernetesServices.filter(service => service.metadata?.name === name);
    if (serviceMatches.length !== 1
      || serviceMatches[0].metadata?.labels?.['app.kubernetes.io/name'] !== name
      || serviceMatches[0].metadata?.labels?.['agentweaver.io/service'] !== name
      || typeof serviceMatches[0].metadata?.uid !== 'string' || !serviceMatches[0].metadata.uid
      || typeof serviceMatches[0].metadata?.resourceVersion !== 'string'
      || !serviceMatches[0].metadata.resourceVersion) {
      throw new Error(`Service ${name} is missing or its owner labels do not match.`);
    }
    serviceReadbacks.set(name, {
      name,
      uid: serviceMatches[0].metadata.uid,
      resourceVersion: serviceMatches[0].metadata.resourceVersion,
      sha256: hashJson(serviceMatches[0]),
    });
  }
  const deployments = selectRuntimeObjects(items, 'Deployment', namespace);
  const replicaSets = selectRuntimeObjects(items, 'ReplicaSet', namespace);
  const pods = selectRuntimeObjects(items, 'Pod', namespace);
  const observed = [];

  for (const image of services) {
    const matches = deployments.filter(deployment =>
      deployment.metadata?.name === image.name
      && deploymentContainers(deployment, image.image));
    if (matches.length !== 1) throw new Error(`Expected one live Deployment for ${image.id}.`);
    const deployment = matches[0];
    const deploymentUid = deployment.metadata?.uid;
    const generation = deployment.metadata?.generation;
    const desired = deployment.spec?.replicas ?? 1;
    if (typeof deploymentUid !== 'string' || !deploymentUid
      || !Number.isSafeInteger(generation) || generation < 1
      || !Number.isSafeInteger(desired) || desired < 1
      || deployment.status?.observedGeneration !== generation
      || deployment.status?.updatedReplicas !== desired
      || deployment.status?.readyReplicas !== desired
      || deployment.status?.availableReplicas !== desired
      || deployment.metadata?.labels?.['app.kubernetes.io/name'] !== image.name
      || deployment.metadata?.labels?.['agentweaver.io/service'] !== image.name
      || deployment.spec?.selector?.matchLabels?.['app.kubernetes.io/name'] !== image.name
      || deployment.spec?.template?.metadata?.labels?.['app.kubernetes.io/name'] !== image.name
      || deployment.spec?.template?.metadata?.labels?.['agentweaver.io/service'] !== image.name
      || deployment.spec?.template?.metadata?.labels?.['agentweaver.io/workload'] !== image.name) {
      throw new Error(`Deployment ${image.name} has no current, fully available rollout.`);
    }
    if (IDENTITY_SERVICES.has(image.name)
      && deployment.spec?.template?.metadata?.labels?.['azure.workload.identity/use'] !== 'true') {
      throw new Error(`Deployment ${image.name} is missing its admitted workload identity label.`);
    }
    if (image.name === 'projects-config') {
      const template = deployment.spec?.template;
      const catalogAnnotations = template?.metadata?.annotations ?? {};
      const templateContainer = deploymentContainers(deployment, image.image)[0];
      if (catalogAnnotations[SOURCE_ANNOTATION] !== providerCatalog.sourceCommit
        || !hasExactProviderCatalogBinding(templateContainer.envFrom, providerCatalog.configMapName)
        || hasProviderCatalogEnvironmentOverride(templateContainer.env)) {
        throw new Error('Projects does not bind the exact observed provider catalog source and ConfigMap.');
      }
    }
    const selector = deployment.spec?.selector?.matchLabels;
    const activeReplicaSets = replicaSets.filter(replicaSet =>
      ownerReference(replicaSet, 'Deployment', deploymentUid).length === 1
      && (replicaSet.spec?.replicas ?? 0) > 0);
    if (activeReplicaSets.length !== 1) {
      throw new Error(`Deployment ${image.name} does not have one current owner ReplicaSet.`);
    }
    const replicaSet = activeReplicaSets[0];
    if (!replicaSet.metadata?.uid || !matchesSelector(replicaSet.metadata?.labels, selector)
      || replicaSet.metadata?.labels?.['agentweaver.io/service'] !== image.name
      || replicaSet.metadata?.labels?.['agentweaver.io/workload'] !== image.name) {
      throw new Error(`ReplicaSet for ${image.name} does not match the current Deployment selector.`);
    }
    const ownedPods = pods.filter(pod =>
      ownerReference(pod, 'ReplicaSet', replicaSet.metadata.uid).length === 1
      && matchesSelector(pod.metadata?.labels, selector));
    if (ownedPods.length !== desired) {
      throw new Error(`Deployment ${image.name} has an unexpected number of current owner Pods.`);
    }
    const podEvidence = [];
    for (const pod of ownedPods) {
      const ready = pod.status?.conditions?.some(condition =>
        condition.type === 'Ready' && condition.status === 'True');
      const containers = deploymentContainers({ ...deployment, spec: { ...deployment.spec, template: { spec: pod.spec } } }, image.image);
      const applicationContainer = pod.spec?.containers?.[0];
      const status = pod.status?.containerStatuses;
      if (pod.status?.phase !== 'Running' || !ready || typeof pod.metadata?.uid !== 'string'
        || !pod.metadata.uid || status?.length !== containers.length
        || status.some(container => container.ready !== true || !container.imageID)
        || pod.spec?.containers?.[0]?.name !== image.name
        || pod.metadata?.labels?.['agentweaver.io/service'] !== image.name
        || pod.metadata?.labels?.['agentweaver.io/workload'] !== image.name
        || image.name === 'projects-config'
          && (!hasExactProviderCatalogBinding(applicationContainer?.envFrom, providerCatalog.configMapName)
            || hasProviderCatalogEnvironmentOverride(applicationContainer?.env))) {
        throw new Error(`Pod ${pod.metadata?.name ?? '[unknown]'} is not a ready owner of ${image.name}.`);
      }
      if (IDENTITY_SERVICES.has(image.name)
        && pod.metadata?.labels?.['azure.workload.identity/use'] !== 'true') {
        throw new Error(`Pod ${pod.metadata.name} is missing its admitted workload identity label.`);
      }
      if (image.name === 'projects-config'
        && pod.metadata?.annotations?.[SOURCE_ANNOTATION] !== providerCatalog.sourceCommit) {
        throw new Error('The running Projects Pod does not bind the validated provider catalog source commit.');
      }
      // A reused image is bound to the commit that built it, not to this release's source.
      const registry = await verifyRegistryImage(
        image.image,
        status[0].imageID,
        image.reusedFrom?.sourceSha ?? sourceSha,
        image.reusedFrom?.sourceTree ?? sourceTree,
        fetchImpl,
      );
      podEvidence.push({
        name: pod.metadata.name,
        uid: pod.metadata.uid,
        sha256: hashJson(pod),
        phase: pod.status.phase,
        ready: true,
        image: containers[0].image,
        imageID: status[0].imageID,
        nodeName: pod.spec?.nodeName ?? null,
        registry,
      });
    }
    observed.push({
      componentId: image.id,
      service: serviceReadbacks.get(image.name),
      deployment: {
        name: deployment.metadata.name,
        uid: deploymentUid,
        sha256: hashJson(deployment),
        generation,
        observedGeneration: deployment.status.observedGeneration,
        desiredReplicas: desired,
      },
      replicaSet: {
        name: replicaSet.metadata.name,
        uid: replicaSet.metadata.uid,
        sha256: hashJson(replicaSet),
      },
      pods: podEvidence,
    });
  }

  return {
    status: 'passed',
    observedAt: now(),
    context,
    namespace,
    sourceSha,
    sourceTree,
    providerCatalog: configMapEvidence,
    services: observed,
    agentHostImageReceipt: {
      status: 'receipt-validated',
      image: hostImage.sandboxImage,
      digest: hostImage.sandboxImageDigest,
      compressedPullBytes: hostImage.compressedPullBytes,
    },
    agentHostRuntime: {
      status: 'blocked',
      reason: 'No exact provider-owned Sandbox lease, AgentHost Pod UID, and phase receipt was observed by the service Deployment readback.',
    },
  };
}

/** Validate the publication receipt and require the full nine-service plus AgentHost image set. */
export async function loadValidatedPublicationImages({
  publication,
  manifest,
  sourceSha,
  validatePublicationReceipt,
}) {
  if (typeof validatePublicationReceipt !== 'function') {
    throw new Error('The source-bound publication receipt validator is unavailable.');
  }
  if (!SHA.test(sourceSha ?? '')) throw new Error('The exact reviewed source SHA is required.');
  const images = validatePublicationReceipt(publication, manifest, sourceSha);
  if (!Array.isArray(images) || images.length !== SERVICE_NAMES.length + 1) {
    throw new Error('The source-bound publication receipt did not validate the complete P1 product set.');
  }
  const services = images.filter(image => image.id !== 'Agentweaver.AgentHost');
  const agentHost = images.filter(image => image.id === 'Agentweaver.AgentHost');
  if (services.length !== SERVICE_NAMES.length || agentHost.length !== 1
    || SERVICE_NAMES.some(name => !services.some(image => image.name === name && image.image))
    || !agentHost[0].sandboxImage
    || !DIGEST.test(agentHost[0].sandboxImageDigest ?? '')
    || !Number.isSafeInteger(agentHost[0].compressedPullBytes)
    || agentHost[0].compressedPullBytes <= 0
    || imageParts(agentHost[0].sandboxImage).digest !== agentHost[0].sandboxImageDigest) {
    throw new Error('The source-bound publication receipt did not validate the exact service and AgentHost images.');
  }
  return images;
}

/**
 * Match release or bootstrap publication images to the exact source manifest and reviewed source.
 * Bootstrap drafts need their separately validated digest-pinned deployment manifest.
 */
export function validateP1PublicationReceipt(
  publication, manifest, sourceSha, sourceTree, deploymentManifest = manifest,
) {
  const integrationImagesOnly = publication?.mode === 'images-only';
  const bootstrapDraft = !integrationImagesOnly && manifest?.stage === 'draft'
    && manifest.evidence === undefined && deploymentManifest !== manifest
    && deploymentManifest?.stage === 'draft' && deploymentManifest.evidence === undefined;
  if (!publication || typeof publication !== 'object' || Array.isArray(publication)
    || publication.schemaVersion !== 1 || publication.status !== 'published'
    || publication.sourceSha !== sourceSha || !SHA.test(sourceTree ?? '')
    || publication.mode !== undefined && !integrationImagesOnly
    || integrationImagesOnly && publication.sourceTree !== sourceTree
    || integrationImagesOnly && (manifest?.stage !== 'draft' || manifest.evidence !== undefined
      || publication.planned?.length !== P1_SERVICE_COMPONENTS.length
      || publication.published?.length !== P1_SERVICE_COMPONENTS.length)
    || !integrationImagesOnly && !bootstrapDraft && (manifest?.stage !== 'release'
      || manifest.evidence?.sourceSha !== sourceSha || deploymentManifest !== manifest)
    || !SHA256.test(publication.provenanceSha256 ?? '')
    || !Array.isArray(publication.planned) || !Array.isArray(publication.published)
    || !Array.isArray(manifest?.components) || !Array.isArray(deploymentManifest?.components)) {
    throw new Error('The publication receipt is not a complete result for the exact reviewed source.');
  }
  const components = new Map();
  const deploymentComponents = new Map();
  for (const { id } of P1_SERVICE_COMPONENTS) {
    const matches = manifest.components.filter(component => component?.id === id);
    const deploymentMatches = deploymentManifest.components.filter(component => component?.id === id);
    if (matches.length !== 1 || matches[0].kind !== 'service'
      || typeof matches[0].version !== 'string' || !matches[0].version
      || deploymentMatches.length !== 1 || deploymentMatches[0].kind !== 'service'
      || deploymentMatches[0].version !== matches[0].version
      || deploymentMatches[0].project !== matches[0].project) {
      throw new Error(`The exact source manifest is missing service component ${id}.`);
    }
    components.set(id, matches[0]);
    deploymentComponents.set(id, deploymentMatches[0]);
  }

  if (!integrationImagesOnly) {
    if (!Array.isArray(publication.requiredPackageIds)
      || publication.sourceTree !== undefined && publication.sourceTree !== sourceTree) {
      throw new Error('The release publication receipt is not bound to the exact source commit and tree.');
    }
    const validated = validateReleasePublicationReceipt(publication, deploymentManifest, sourceSha);
    const validatedById = new Map(validated.map(image => [image.id, image]));
    if (validatedById.size !== P1_SERVICE_COMPONENTS.length
      || P1_SERVICE_COMPONENTS.some(({ id }) => !validatedById.has(id))) {
      throw new Error('The release publication receipt does not contain the exact P1 product image set.');
    }
    return P1_SERVICE_COMPONENTS.map(({ id, name }) => {
      const image = validatedById.get(id);
      const component = components.get(id);
      const deploymentComponent = deploymentComponents.get(id);
      const planned = publication.planned.find(record => record.id === id);
      const published = publication.published.find(record => record.id === id);
      const repository = `ghcr.io/sabbour/${componentImageRepository(id)}`;
      const expectedImage = `${repository}@${deploymentComponent.imageDigest}`;
      // A reused image keeps the tag of the commit that built it; never expect a retag to this source.
      if (image.image !== expectedImage || published.image !== expectedImage
        || planned.destination !== `${repository}:${componentImageTag(component.version, image.reusedFrom?.sourceSha ?? sourceSha)}`) {
        throw new Error(`The release image for ${id} does not match its exact component-version tag and digest.`);
      }
      // Keep the AgentHost archive pins so the later local measurement can match this publication.
      return {
        ...image,
        id,
        name,
        component,
        result: published,
        ...(id === 'Agentweaver.AgentHost' ? {
          ociArchiveSha256: published.ociArchiveSha256,
          imageConfigDigest: published.imageConfigDigest,
        } : {}),
      };
    });
  }

  const images = [];
  for (const { id, name } of P1_SERVICE_COMPONENTS) {
    const component = components.get(id);
    const planned = publication.planned.filter(record => record?.id === id);
    const published = publication.published.filter(record => record?.id === id);
    if (planned.length !== 1 || published.length !== 1
      || planned[0].kind !== 'image' || published[0].kind !== 'image'
      || planned[0].version !== component.version || published[0].version !== component.version
      || !SHA256.test(planned[0].sha256 ?? '')
      || published[0].archiveSha256 !== planned[0].sha256) {
      throw new Error(`The publication receipt does not contain one source-matched image for ${id}.`);
    }
    const tag = integrationImagesOnly ? sourceSha : componentImageTag(component.version, sourceSha);
    const versionSuffix = `:${tag}`;
    const destination = planned[0].destination;
    if (typeof destination !== 'string' || !destination.startsWith('ghcr.io/')
      || !destination.endsWith(versionSuffix) || destination.includes('@')) {
      throw new Error(`The planned image destination for ${id} is not an exact GHCR version tag.`);
    }
    const destinationRepository = destination.slice(0, -versionSuffix.length);
    const publishedImage = imageParts(published[0].image);
    if (publishedImage.repository !== destinationRepository.slice('ghcr.io/'.length)
      || component.imageDigest !== undefined && component.imageDigest !== publishedImage.digest
      || integrationImagesOnly && (
        destinationRepository !== `ghcr.io/sabbour/agentweaver/${name}`
        || published[0].sourceSha !== sourceSha
        || published[0].sourceTree !== sourceTree
        || published[0].tag !== sourceSha
        || !DIGEST.test(published[0].configDigest ?? '')
      )) {
      throw new Error(`The published immutable image for ${id} does not match its planned destination or manifest pin.`);
    }

    if (id === 'Agentweaver.AgentHost') {
      const measurement = published[0].preparedOciMeasurement;
      if (!SHA256.test(published[0].ociArchiveSha256 ?? '')
        || !DIGEST.test(published[0].imageConfigDigest ?? '')
        || integrationImagesOnly && published[0].configDigest !== published[0].imageConfigDigest
        || measurement?.kind !== 'agenthost-image' || measurement.schemaVersion !== 1
        || measurement.sourceSha !== sourceSha || measurement.sourceTree !== sourceTree
        || measurement.image?.platform !== 'linux/amd64'
        || !DIGEST.test(measurement.image?.digest ?? '')
        || !DIGEST.test(measurement.image?.configDigest ?? '')
        || measurement.image.configDigest !== published[0].imageConfigDigest
        || !Number.isSafeInteger(measurement.image.compressedPullBytes)
        || measurement.image.compressedPullBytes < 1) {
        throw new Error('The published AgentHost receipt has no source-bound OCI archive measurement.');
      }
      images.push({
        id,
        name,
        component,
        result: published[0],
        image: published[0].image,
        sandboxImage: published[0].image,
        sandboxImageDigest: publishedImage.digest,
        compressedPullBytes: measurement.image.compressedPullBytes,
        ociArchiveSha256: published[0].ociArchiveSha256,
        preparedOciMeasurement: measurement,
      });
    } else {
      images.push({ id, name, component, result: published[0], image: published[0].image });
    }
  }
  return images;
}

/**
 * Validate the recorded integration deployment against the exact manifest, publication, images, and target.
 * The readback digest must cover the required resource observations for every service.
 */
export function validateP1IntegrationDeploymentReceipt(receipt, {
  sourceSha,
  sourceTree,
  manifest,
  publication,
  namespace,
  images,
}) {
  if (!receipt || typeof receipt !== 'object' || Array.isArray(receipt)
    || receipt.schemaVersion !== 1 || receipt.status !== 'deployed'
    || !SHA.test(sourceSha ?? '') || !SHA.test(sourceTree ?? '')
    || receipt.sourceSha !== sourceSha || receipt.sourceTree !== sourceTree
    || !SHA256.test(receipt.manifestSha256 ?? '')
    || receipt.manifestSha256 !== hashJson(manifest)
    || !SHA256.test(receipt.provenanceSha256 ?? '')
    || receipt.provenanceSha256 !== publication?.provenanceSha256
    || !SHA256.test(receipt.publicationSha256 ?? '')
    || receipt.publicationSha256 !== hashJson(publication)
    || !SHA256.test(receipt.runtimeManifestSha256 ?? '')
    || !Number.isFinite(Date.parse(receipt.completedAt ?? ''))) {
    throw new Error('The integration deployment receipt is not bound to the exact source, manifest, and publication.');
  }
  if (publication?.mode !== 'images-only' || publication.sourceTree !== sourceTree) {
    throw new Error('The integration deployment receipt requires the source-matched images-only publication.');
  }

  const target = receipt.target;
  if (!target || typeof target !== 'object' || Array.isArray(target)
    || target.namespace !== namespace
    || typeof target.subscriptionId !== 'string' || !target.subscriptionId
    || typeof target.tenantId !== 'string' || !target.tenantId
    || typeof target.resourceGroup !== 'string' || !target.resourceGroup
    || typeof target.clusterName !== 'string' || !target.clusterName
    || !Number.isSafeInteger(target.nodeMin) || target.nodeMin < 1
    || !Number.isSafeInteger(target.nodeMax) || target.nodeMax < target.nodeMin) {
    throw new Error('The integration deployment receipt does not name the exact P1 namespace and deployment target.');
  }

  if (!Array.isArray(images)) {
    throw new Error('The integration deployment receipt has no validated source image set.');
  }
  const expectedImages = new Map(images.map(image => [image.id, image]));
  if (expectedImages.size !== P1_SERVICE_COMPONENTS.length
    || !Array.isArray(receipt.registryImages)
    || receipt.registryImages.length !== P1_SERVICE_COMPONENTS.length) {
    throw new Error('The integration deployment receipt does not contain the complete P1 image set.');
  }
  const registryImages = new Map();
  for (const image of receipt.registryImages) {
    if (!image || typeof image !== 'object' || Array.isArray(image)
      || typeof image.id !== 'string' || registryImages.has(image.id)) {
      throw new Error('The integration deployment receipt contains an invalid or duplicate image identity.');
    }
    registryImages.set(image.id, image);
  }
  for (const [id, expected] of expectedImages) {
    const image = registryImages.get(id);
    const expectedReference = id === 'Agentweaver.AgentHost' ? expected.sandboxImage : expected.image;
    if (!image || image.image !== expectedReference
      || image.configDigest !== expected.result?.configDigest
      || image.sourceSha !== sourceSha || image.sourceTree !== sourceTree
      || image.version !== expected.component?.version) {
      throw new Error(`The integration deployment receipt image for ${id} differs from its publication receipt.`);
    }
  }

  const readbacks = receipt.readbacks;
  if (!readbacks || typeof readbacks !== 'object' || Array.isArray(readbacks)
    || !Array.isArray(readbacks.resources) || readbacks.resources.length < SERVICE_NAMES.length * 3
    || !SHA256.test(readbacks.sha256 ?? '')
    || readbacks.brokerRunBinding !== undefined
      && (!readbacks.brokerRunBinding || typeof readbacks.brokerRunBinding !== 'object'
        || Array.isArray(readbacks.brokerRunBinding))) {
    throw new Error('The integration deployment receipt has no complete resource readback set.');
  }
  const identities = new Set();
  for (const resource of readbacks.resources) {
    if (!resource || typeof resource !== 'object' || Array.isArray(resource)
      || typeof resource.kind !== 'string' || !resource.kind
      || typeof resource.name !== 'string' || !resource.name
      || !SHA256.test(resource.sha256 ?? '')) {
      throw new Error('The integration deployment receipt contains an invalid resource readback.');
    }
    const identity = `${resource.kind}\u0000${resource.name}`;
    if (identities.has(identity)) throw new Error('The integration deployment receipt contains duplicate resource readbacks.');
    identities.add(identity);
  }
  for (const name of SERVICE_NAMES) {
    for (const kind of ['deployment', 'service', 'pods']) {
      if (!identities.has(`${kind}\u0000${name}`)) {
        throw new Error(`The integration deployment receipt is missing the ${kind} readback for ${name}.`);
      }
    }
  }
  const readbackDocument = readbacks.brokerRunBinding === undefined
    ? readbacks.resources
    : { resources: readbacks.resources, brokerRunBinding: readbacks.brokerRunBinding };
  if (hashJson(readbackDocument) !== readbacks.sha256) {
    throw new Error('The integration deployment receipt readback digest does not match its resource readbacks.');
  }

  return {
    status: receipt.status,
    sourceSha,
    sourceTree,
    manifestSha256: receipt.manifestSha256,
    publicationSha256: receipt.publicationSha256,
    readbackSha256: readbacks.sha256,
    completedAt: receipt.completedAt,
    target: { clusterName: target.clusterName, namespace: target.namespace },
  };
}

/**
 * Bind the release publication hash to fresh, exact Kubernetes and registry readbacks.
 * This path records observed deployment state instead of claiming a bootstrap receipt exists.
 */
export function createP1ReleaseDeploymentEvidence({
  sourceSha,
  sourceTree,
  manifest,
  deploymentManifest = manifest,
  publication,
  context,
  namespace,
  providerCatalog,
  images,
  serviceEvidence,
}) {
  requireTarget(context, namespace);
  const bootstrapDraft = manifest?.stage === 'draft' && manifest.evidence === undefined
    && deploymentManifest !== manifest && deploymentManifest?.stage === 'draft'
    && deploymentManifest.evidence === undefined;
  if (namespace !== 'agentweaver' || !SHA.test(sourceSha ?? '') || !SHA.test(sourceTree ?? '')
    || publication?.sourceSha !== sourceSha || publication?.mode !== undefined
    || !bootstrapDraft && (manifest?.stage !== 'release' || manifest.evidence?.sourceSha !== sourceSha
      || deploymentManifest !== manifest)
    || !Array.isArray(images) || images.length !== P1_SERVICE_COMPONENTS.length
    || serviceEvidence?.status !== 'passed' || serviceEvidence.context !== context
    || serviceEvidence.namespace !== namespace || serviceEvidence.sourceSha !== sourceSha
    || serviceEvidence.sourceTree !== sourceTree
    || !Number.isFinite(Date.parse(serviceEvidence.observedAt ?? ''))
    || !Array.isArray(serviceEvidence.services)
    || serviceEvidence.services.length !== SERVICE_NAMES.length) {
    throw new Error('The release deployment readbacks are not bound to the exact source and target.');
  }
  // Revalidate image identities here so callers cannot create a receipt from unchecked image rows.
  const validatedImages = validateP1PublicationReceipt(
    publication, manifest, sourceSha, sourceTree, deploymentManifest);
  if (hashJson(images) !== hashJson(validatedImages)) {
    throw new Error('The release deployment images differ from the validated publication.');
  }
  const expected = new Map(images.filter(image => image.id !== 'Agentweaver.AgentHost')
    .map(image => [image.id, image]));
  const agentHostImages = images.filter(image => image.id === 'Agentweaver.AgentHost');
  const observed = new Map();
  for (const service of serviceEvidence.services) {
    if (typeof service?.componentId !== 'string' || observed.has(service.componentId)) {
      throw new Error('The release deployment readbacks contain a missing or duplicate service identity.');
    }
    observed.set(service.componentId, service);
  }
  if (images.length !== P1_SERVICE_COMPONENTS.length || agentHostImages.length !== 1
    || expected.size !== SERVICE_NAMES.length || observed.size !== SERVICE_NAMES.length
    || P1_SERVICE_COMPONENTS.filter(image => image.id !== 'Agentweaver.AgentHost')
      .some(image => !expected.has(image.id) || !observed.has(image.id))) {
    throw new Error('The release deployment readbacks do not contain all nine published services.');
  }
  for (const [id, image] of expected) {
    const readback = observed.get(id);
    const expectedDigest = imageParts(image.image).digest;
    // A reused image carries the source labels of the commit that built it. The publication
    // validator already bound that build commit to this release, so expect its labels here.
    const expectedSourceSha = image.reusedFrom?.sourceSha ?? sourceSha;
    const expectedSourceTree = image.reusedFrom?.sourceTree ?? sourceTree;
    if (!readback || readback.service?.name !== image.name || !readback.service.uid
      || !readback.service.resourceVersion || !SHA256.test(readback.service.sha256 ?? '')
      || !readback.deployment?.uid || readback.deployment.name !== image.name
      || !SHA256.test(readback.deployment.sha256 ?? '')
      || !readback.replicaSet?.uid || !SHA256.test(readback.replicaSet.sha256 ?? '')
      || !Array.isArray(readback.pods) || readback.pods.length === 0) {
      throw new Error(`The release deployment readback for ${id} is incomplete.`);
    }
    for (const pod of readback.pods) {
      const podDigest = imageParts(pod.imageID).digest;
      const registry = pod.registry;
      if (pod.ready !== true || !pod.uid || !SHA256.test(pod.sha256 ?? '') || pod.image !== image.image
        || !registry || registry.manifestDigest !== expectedDigest
        || !DIGEST.test(registry.platformDigest ?? '')
        || podDigest !== registry.manifestDigest && podDigest !== registry.platformDigest
        || registry.sourceSha !== expectedSourceSha || registry.sourceTree !== expectedSourceTree
        || !DIGEST.test(registry.configDigest ?? '')) {
        throw new Error(`The release deployment image readback for ${id} differs from its source publication.`);
      }
    }
  }
  const readbacks = {
    context,
    namespace,
    sourceSha,
    sourceTree,
    providerCatalog: serviceEvidence.providerCatalog,
    services: serviceEvidence.services,
  };
  if (readbacks.providerCatalog?.name !== providerCatalog?.configMapName
    || readbacks.providerCatalog?.sourceCommit !== providerCatalog?.sourceCommit
    || typeof readbacks.providerCatalog?.uid !== 'string' || !readbacks.providerCatalog.uid
    || typeof readbacks.providerCatalog.resourceVersion !== 'string'
    || !readbacks.providerCatalog.resourceVersion
    || !SHA256.test(readbacks.providerCatalog.sha256 ?? '')) {
    throw new Error('The release deployment readbacks do not bind the provider catalog resource.');
  }
  return {
    status: 'deployed',
    resultType: bootstrapDraft ? 'pre-acceptance' : 'release',
    sourceSha,
    sourceTree,
    manifestSha256: hashJson(manifest),
    deploymentManifestSha256: hashJson(deploymentManifest),
    publicationSha256: hashJson(publication),
    readbacks,
    readbackSha256: hashJson(readbacks),
    completedAt: serviceEvidence.observedAt,
    target: { context, namespace },
  };
}

function runGit(args, repoRoot, spawnSyncImpl = spawnSync) {
  const result = spawnSyncImpl('git', args, {
    cwd: repoRoot,
    encoding: 'utf8',
    windowsHide: true,
    timeout: 15000,
    maxBuffer: 1024 * 1024,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  if (result.error || result.status !== 0) throw new Error('Cannot verify the exact reviewed Git source.');
  return result.stdout.trim();
}

/** Run bounded read-only GitHub or Git queries without exposing command output on failure. */
function runReadOnlyCommand(executable, args, { cwd }, spawnSyncImpl = spawnSync) {
  const result = spawnSyncImpl(executable, args, {
    cwd,
    encoding: 'utf8',
    windowsHide: true,
    timeout: 15000,
    maxBuffer: 1024 * 1024,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  if (result.error || result.status !== 0) {
    throw new Error('Cannot verify the exact release publication result.');
  }
  return result.stdout.trim();
}

function sha256File(file) {
  return new Promise((resolve, reject) => {
    const digest = createHash('sha256');
    const stream = createReadStream(file);
    stream.on('data', chunk => digest.update(chunk));
    stream.once('error', reject);
    stream.once('end', () => resolve(digest.digest('hex')));
  });
}

/**
 * Resolve the reviewed commit and tree from Git objects while requiring a clean helper worktree.
 * Integration evidence also requires HEAD to be the reviewed commit; release evidence may use a newer helper checkout.
 */
export function resolveReviewedSource({
  repoRoot,
  expectedSourceSha,
  requireHeadMatch = true,
  spawnSyncImpl = spawnSync,
}) {
  if (typeof repoRoot !== 'string' || !repoRoot || !SHA.test(expectedSourceSha ?? '')
    || typeof requireHeadMatch !== 'boolean') {
    throw new Error('A repository root and exact reviewed source SHA are required.');
  }
  const actualSourceSha = runGit(['rev-parse', '--verify', 'HEAD'], repoRoot, spawnSyncImpl);
  const resolvedSourceSha = runGit([
    'rev-parse', '--verify', `${expectedSourceSha}^{commit}`,
  ], repoRoot, spawnSyncImpl);
  const sourceTree = runGit([
    'rev-parse', '--verify', `${expectedSourceSha}^{tree}`,
  ], repoRoot, spawnSyncImpl);
  const dirty = runGit(['status', '--porcelain', '--untracked-files=normal'], repoRoot, spawnSyncImpl);
  if (resolvedSourceSha !== expectedSourceSha
    || requireHeadMatch && actualSourceSha !== expectedSourceSha
    || !SHA.test(sourceTree) || dirty) {
    throw new Error('The clean repository does not contain the exact reviewed source commit and tree.');
  }
  return { sourceSha: expectedSourceSha, sourceTree };
}

/**
 * Assemble exact-source publication, deployment, live workload, and AgentHost archive measurements.
 * Release evidence verifies the immutable GitHub result; the function makes read-only evidence queries only.
 */
export async function prepareP1DeploymentEvidence({
  repoRoot,
  expectedSourceSha,
  publicationPath,
  manifestPath,
  releaseTag,
  agentHostArchivePath,
  integrationDeploymentReceiptPath,
  providerCatalog,
  context,
  namespace,
  validatePublicationReceipt,
  fetchImpl = globalThis.fetch,
  readObjects = runKubectl,
  spawnSyncImpl = spawnSync,
  measureAgentHostArchive = measureAgentHostArchiveSync,
}) {
  let publication;
  let manifest;
  let manifestFile;
  let integrationDeploymentReceipt;
  try {
    publication = JSON.parse(readFileSync(publicationPath, 'utf8'));
    manifestFile = path.resolve(repoRoot, manifestPath);
    const relativeManifest = path.relative(path.resolve(repoRoot), manifestFile);
    if (!relativeManifest || relativeManifest === '..' || relativeManifest.startsWith(`..${path.sep}`)
      || path.isAbsolute(relativeManifest)) {
      throw new Error('The release manifest must be inside the exact source repository.');
    }
    if (publication?.mode === 'images-only') {
      if (!integrationDeploymentReceiptPath) {
        throw new Error('The images-only publication requires its integration deployment receipt.');
      }
      integrationDeploymentReceipt = JSON.parse(readFileSync(integrationDeploymentReceiptPath, 'utf8'));
    } else if (integrationDeploymentReceiptPath) {
      throw new Error('Release publication evidence uses fresh resource readbacks, not an integration receipt.');
    }
  } catch {
    throw new Error('The exact source-bound publication, required deployment receipt, or manifest is missing or malformed.');
  }
  const integrationImagesOnly = publication?.mode === 'images-only';
  if (integrationImagesOnly && releaseTag !== undefined
    || !integrationImagesOnly && (typeof releaseTag !== 'string'
      || !/^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(releaseTag))) {
    throw new Error('Release and bootstrap evidence requires an exact versioned releaseTag; integration evidence does not.');
  }
  const source = resolveReviewedSource({
    repoRoot,
    expectedSourceSha,
    requireHeadMatch: integrationImagesOnly,
    spawnSyncImpl,
  });
  const relativeManifest = path.relative(path.resolve(repoRoot), manifestFile).replaceAll('\\', '/');
  let committedManifest;
  try {
    committedManifest = JSON.parse(runGit(['show', `${source.sourceSha}:${relativeManifest}`], repoRoot, spawnSyncImpl));
  } catch {
    throw new Error('The P1 release manifest is not present in the exact reviewed Git source.');
  }
  if (integrationImagesOnly) {
    try {
      manifest = JSON.parse(readFileSync(manifestFile, 'utf8'));
    } catch {
      throw new Error('The integration manifest is missing or malformed.');
    }
    if (JSON.stringify(committedManifest) !== JSON.stringify(manifest)) {
      throw new Error('The P1 integration manifest differs from the exact reviewed Git source.');
    }
  } else {
    manifest = committedManifest;
  }
  const readSourceProject = (file) => {
    const relativePath = path.relative(repoRoot, file);
    if (relativePath === '..' || relativePath.startsWith(`..${path.sep}`)
      || path.isAbsolute(relativePath)) {
      throw new Error('A P1 manifest project path escapes the exact reviewed source tree.');
    }
    return runGit([
      'show',
      `${source.sourceSha}:${relativePath.split(path.sep).join('/')}`,
    ], repoRoot, spawnSyncImpl);
  };
  validateManifest(manifest, { root: repoRoot, readProject: readSourceProject });
  let deploymentManifest = manifest;
  if (!integrationImagesOnly) {
    const run = (executable, args, options) =>
      runReadOnlyCommand(executable, args, options, spawnSyncImpl);
    if (manifest.stage === 'draft') {
      deploymentManifest = validateBootstrapManifest(manifest, publication, {
        root: repoRoot,
        sourceSha: source.sourceSha,
        releaseTag,
        manifestPath: manifestFile,
        run,
      });
      const verifiedSource = await verifyBootstrapRelease(releaseTag, publication, {
        root: repoRoot,
        run,
        publicationSha256: await sha256File(publicationPath),
      });
      if (verifiedSource !== source.sourceSha) {
        throw new Error('The bootstrap publication result differs from the exact reviewed source commit.');
      }
    } else if (manifest.stage === 'release') {
      const verifiedSource = await verifyPublishedSource(releaseTag, publication, source.sourceSha, {
        root: repoRoot,
        run,
        sourceCommitDescription: 'published source',
      });
      if (verifiedSource !== source.sourceSha) {
        throw new Error('The release publication result differs from the exact reviewed source commit.');
      }
    } else {
      throw new Error('The release publication requires an exact release manifest or tracked bootstrap draft.');
    }
  }
  const images = await loadValidatedPublicationImages({
    publication,
    manifest: deploymentManifest,
    sourceSha: source.sourceSha,
    validatePublicationReceipt: (publication.mode === 'images-only' ? validatePublicationReceipt : undefined)
      ?? ((receipt, sourceManifest, sourceSha) =>
        validateP1PublicationReceipt(
          receipt,
          manifest,
          sourceSha,
          source.sourceTree,
          sourceManifest,
        )),
  });
  const integrationDeployment = publication.mode === 'images-only'
    ? validateP1IntegrationDeploymentReceipt(integrationDeploymentReceipt, {
      sourceSha: source.sourceSha,
      sourceTree: source.sourceTree,
      manifest,
      publication,
      namespace,
      images,
    })
    : undefined;
  const serviceEvidence = await observeP1ServiceDeployments({
    context,
    namespace,
    sourceSha: source.sourceSha,
    sourceTree: source.sourceTree,
    images,
    providerCatalog,
    fetchImpl,
    readObjects,
  });
  const releaseDeployment = integrationDeployment ? undefined : createP1ReleaseDeploymentEvidence({
    sourceSha: source.sourceSha,
    sourceTree: source.sourceTree,
    manifest,
    deploymentManifest,
    publication,
    context,
    namespace,
    providerCatalog,
    images,
    serviceEvidence,
  });
  const hostImage = images.find(image => image.id === 'Agentweaver.AgentHost');
  // A reused AgentHost archive was built from an earlier commit, and its labels name that commit.
  // Measure it against that build commit; the publication validator has already bound it to this release.
  const hostBuildSource = hostImage?.reusedFrom ?? source;
  let agentHostMeasurement;
  try {
    agentHostMeasurement = measureAgentHostArchive(agentHostArchivePath, repoRoot, hostBuildSource.sourceSha);
  } catch (error) {
    throw new Error(`The existing AgentHost image measurement failed: ${redact(error).message}`);
  }
  let archiveSha256;
  try {
    archiveSha256 = await sha256File(agentHostArchivePath);
  } catch {
    throw new Error('The exact AgentHost OCI archive could not be read for its pinned SHA-256.');
  }
  const publishedMeasurement = hostImage?.preparedOciMeasurement;
  if (agentHostMeasurement.kind !== 'agenthost-image'
    || agentHostMeasurement.sourceSha !== hostBuildSource.sourceSha
    || agentHostMeasurement.sourceTree !== hostBuildSource.sourceTree
    || agentHostMeasurement.image?.platform !== 'linux/amd64'
    || archiveSha256 !== hostImage.ociArchiveSha256
    || agentHostMeasurement.image?.digest !== publishedMeasurement?.image?.digest
    || agentHostMeasurement.image?.configDigest !== publishedMeasurement?.image?.configDigest
    || agentHostMeasurement.image?.compressedPullBytes !== hostImage.compressedPullBytes) {
    throw new Error('The measured AgentHost OCI archive does not match the exact source-bound publication receipt.');
  }
  const evidence = {
    source,
    ...(integrationDeployment ? { integrationDeployment } : { releaseDeployment }),
    providerCatalog: serviceEvidence.providerCatalog,
    services: serviceEvidence,
    agentHostImageMeasurement: {
      status: 'passed',
      ...agentHostMeasurement,
      registryImage: hostImage.sandboxImage,
      registryDigest: hostImage.sandboxImageDigest,
      archiveSha256,
      compressedPullBytes: hostImage.compressedPullBytes,
      hardByteCeiling: null,
    },
    agentHostRuntime: serviceEvidence.agentHostRuntime,
  };
  // Keep this binding in memory, not in caller-editable JSON. The measurements still record
  // the build source, while the source and deployment receipts keep the reviewed release source.
  preparedHostSources.set(evidence, {
    release: { sourceSha: source.sourceSha, sourceTree: source.sourceTree },
    build: { sourceSha: hostBuildSource.sourceSha, sourceTree: hostBuildSource.sourceTree },
  });
  return evidence;
}
