import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { createReadStream, readFileSync } from 'node:fs';
import path from 'node:path';
import { redact } from './redaction.mjs';
import { pullToken } from '../release/registry.mjs';
import { measureAgentHostArchiveSync } from '../release/measure-agenthost-image.mjs';
import { validateManifest } from '../release/validate.mjs';

/** Read-only checks that bind P1 runtime observations to the reviewed source and published images. */
const SHA = /^[a-f0-9]{40}$/;
const SHA256 = /^[a-f0-9]{64}$/;
const DIGEST = /^sha256:[a-f0-9]{64}$/;
const IMAGE_REFERENCE = /^ghcr\.io\/([a-z0-9]+(?:[._-][a-z0-9]+)*(?:\/[a-z0-9]+(?:[._-][a-z0-9]+)*)*)@(sha256:[a-f0-9]{64})$/;
const IMAGE_ACCEPT = [
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.oci.image.manifest.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
  'application/vnd.docker.distribution.manifest.v2+json',
].join(', ');
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

/** Fetch registry metadata and require its exact bytes to match the requested immutable digest. */
async function registryJson(url, expectedDigest, fetchImpl, token) {
  let response;
  try {
    response = await fetchImpl(url, {
      headers: { Accept: IMAGE_ACCEPT, Authorization: `Bearer ${token}` },
      redirect: 'error',
      signal: AbortSignal.timeout(15000),
    });
  } catch (error) {
    throw new Error(`Authenticated public registry read failed: ${redact(error).message}`);
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

/** Verify registry manifest and image-config digests, platform, and source labels against the reviewed image. */
async function verifyRegistryImage(reference, imageId, sourceSha, sourceTree, fetchImpl, { allowMissingSourceTree = false } = {}) {
  const { repository, digest } = imageParts(reference);
  const base = `https://ghcr.io/v2/${repository}`;
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
    || labels?.['io.agentweaver.source-tree'] !== sourceTree
      && !(allowMissingSourceTree && labels?.['io.agentweaver.source-tree'] === undefined)) {
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
 * Observe the exact P1 Deployments, ReplicaSets, Pods, provider catalog, and registry image metadata.
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
  if (namespace !== 'agentweaver-v1-p0') {
    throw new Error('The source-bound P1 runtime list is restricted to agentweaver-v1-p0.');
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
  };
  const kubernetesServices = selectRuntimeObjects(items, 'Service', namespace);
  for (const name of SERVICE_NAMES) {
    const serviceMatches = kubernetesServices.filter(service => service.metadata?.name === name);
    if (serviceMatches.length !== 1
      || serviceMatches[0].metadata?.labels?.['app.kubernetes.io/name'] !== name
      || serviceMatches[0].metadata?.labels?.['agentweaver.io/service'] !== name) {
      throw new Error(`Service ${name} is missing or its owner labels do not match.`);
    }
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
      const registry = await verifyRegistryImage(
        image.image,
        status[0].imageID,
        sourceSha,
        sourceTree,
        fetchImpl,
        { allowMissingSourceTree: image.name === 'web' },
      );
      podEvidence.push({
        name: pod.metadata.name,
        uid: pod.metadata.uid,
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
      deployment: {
        name: deployment.metadata.name,
        uid: deploymentUid,
        generation,
        observedGeneration: deployment.status.observedGeneration,
        desiredReplicas: desired,
      },
      replicaSet: { name: replicaSet.metadata.name, uid: replicaSet.metadata.uid },
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
 * Match each planned and published immutable image to the release manifest and reviewed source.
 * Returns normalized image evidence or throws when component, archive, digest, or source receipts disagree.
 */
export function validateP1PublicationReceipt(publication, manifest, sourceSha, sourceTree) {
  const integrationImagesOnly = publication?.mode === 'images-only';
  if (!publication || typeof publication !== 'object' || Array.isArray(publication)
    || publication.schemaVersion !== 1 || publication.status !== 'published'
    || publication.sourceSha !== sourceSha || !SHA.test(sourceTree ?? '')
    || publication.mode !== undefined && !integrationImagesOnly
    || integrationImagesOnly && publication.sourceTree !== sourceTree
    || integrationImagesOnly && (manifest?.stage !== 'draft' || manifest.evidence !== undefined
      || publication.planned?.length !== P1_SERVICE_COMPONENTS.length
      || publication.published?.length !== P1_SERVICE_COMPONENTS.length)
    || !SHA256.test(publication.provenanceSha256 ?? '')
    || !Array.isArray(publication.planned) || !Array.isArray(publication.published)
    || !Array.isArray(manifest?.components)) {
    throw new Error('The publication receipt is not a complete result for the exact reviewed source.');
  }
  const components = new Map();
  for (const { id } of P1_SERVICE_COMPONENTS) {
    const matches = manifest.components.filter(component => component?.id === id);
    if (matches.length !== 1 || matches[0].kind !== 'service'
      || typeof matches[0].version !== 'string' || !matches[0].version) {
      throw new Error(`The exact source manifest is missing service component ${id}.`);
    }
    components.set(id, matches[0]);
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
    const tag = integrationImagesOnly ? sourceSha : component.version;
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

function sha256File(file) {
  return new Promise((resolve, reject) => {
    const digest = createHash('sha256');
    const stream = createReadStream(file);
    stream.on('data', chunk => digest.update(chunk));
    stream.once('error', reject);
    stream.once('end', () => resolve(digest.digest('hex')));
  });
}

/** Require the repository HEAD to equal the reviewed commit and the worktree to be clean. */
export function resolveReviewedSource({
  repoRoot,
  expectedSourceSha,
  spawnSyncImpl = spawnSync,
}) {
  if (typeof repoRoot !== 'string' || !repoRoot || !SHA.test(expectedSourceSha ?? '')) {
    throw new Error('A repository root and exact reviewed source SHA are required.');
  }
  const actualSourceSha = runGit(['rev-parse', '--verify', 'HEAD'], repoRoot, spawnSyncImpl);
  const sourceTree = runGit(['rev-parse', 'HEAD^{tree}'], repoRoot, spawnSyncImpl);
  const dirty = runGit(['status', '--porcelain', '--untracked-files=normal'], repoRoot, spawnSyncImpl);
  if (actualSourceSha !== expectedSourceSha || !SHA.test(sourceTree) || dirty) {
    throw new Error('The current clean repository does not match the exact reviewed source commit.');
  }
  return { sourceSha: actualSourceSha, sourceTree };
}

/**
 * Assemble source, publication, deployment, live workload, and AgentHost archive measurements.
 * This function reads evidence and live state only; it does not publish images or change infrastructure.
 */
export async function prepareP1DeploymentEvidence({
  repoRoot,
  expectedSourceSha,
  publicationPath,
  manifestPath,
  agentHostArchivePath,
  integrationDeploymentReceiptPath,
  providerCatalog,
  context,
  namespace,
  validatePublicationReceipt,
  fetchImpl = globalThis.fetch,
  readObjects = runKubectl,
  spawnSyncImpl = spawnSync,
}) {
  const source = resolveReviewedSource({ repoRoot, expectedSourceSha, spawnSyncImpl });
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
    manifest = JSON.parse(readFileSync(manifestFile, 'utf8'));
    integrationDeploymentReceipt = JSON.parse(readFileSync(integrationDeploymentReceiptPath, 'utf8'));
  } catch {
    throw new Error('The exact source-bound publication, integration deployment receipt, or manifest is missing or malformed.');
  }
  const relativeManifest = path.relative(path.resolve(repoRoot), manifestFile).replaceAll('\\', '/');
  let committedManifest;
  try {
    committedManifest = JSON.parse(runGit(['show', `${source.sourceSha}:${relativeManifest}`], repoRoot, spawnSyncImpl));
  } catch {
    throw new Error('The P1 release manifest is not present in the exact reviewed Git source.');
  }
  if (JSON.stringify(committedManifest) !== JSON.stringify(manifest)) {
    throw new Error('The P1 release manifest differs from the exact reviewed Git source.');
  }
  validateManifest(manifest, { root: repoRoot });
  const images = await loadValidatedPublicationImages({
    publication,
    manifest,
    sourceSha: source.sourceSha,
    validatePublicationReceipt: validatePublicationReceipt
      ?? ((receipt, sourceManifest, sourceSha) =>
        validateP1PublicationReceipt(receipt, sourceManifest, sourceSha, source.sourceTree)),
  });
  const integrationDeployment = validateP1IntegrationDeploymentReceipt(integrationDeploymentReceipt, {
    sourceSha: source.sourceSha,
    sourceTree: source.sourceTree,
    manifest,
    publication,
    namespace,
    images,
  });
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
  let agentHostMeasurement;
  try {
    agentHostMeasurement = measureAgentHostArchiveSync(agentHostArchivePath, repoRoot);
  } catch (error) {
    throw new Error(`The existing AgentHost image measurement failed: ${redact(error).message}`);
  }
  const hostImage = images.find(image => image.id === 'Agentweaver.AgentHost');
  let archiveSha256;
  try {
    archiveSha256 = await sha256File(agentHostArchivePath);
  } catch {
    throw new Error('The exact AgentHost OCI archive could not be read for its pinned SHA-256.');
  }
  const publishedMeasurement = hostImage?.preparedOciMeasurement;
  if (agentHostMeasurement.kind !== 'agenthost-image'
    || agentHostMeasurement.sourceSha !== source.sourceSha
    || agentHostMeasurement.sourceTree !== source.sourceTree
    || agentHostMeasurement.image?.platform !== 'linux/amd64'
    || archiveSha256 !== hostImage.ociArchiveSha256
    || agentHostMeasurement.image?.digest !== publishedMeasurement?.image?.digest
    || agentHostMeasurement.image?.configDigest !== publishedMeasurement?.image?.configDigest
    || agentHostMeasurement.image?.compressedPullBytes !== hostImage.compressedPullBytes) {
    throw new Error('The measured AgentHost OCI archive does not match the exact source-bound publication receipt.');
  }
  return {
    source,
    integrationDeployment,
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
}
