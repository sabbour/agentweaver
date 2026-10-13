import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { componentImageRepository, selectIntegrationImageComponents } from './pack.mjs';
import { createRuntimeList } from './runtime-list.mjs';
import { validateManifest } from './validate.mjs';
import { verifyServiceRegistryImage } from './registry.mjs';
import {
  SERVICE_NAMES, validateRuntimeManifest, validateTarget, verifyAgentHostRegistryImage,
  verifyAzureTarget, verifyExistingP0BrokerRouting, verifyLiveDeployment, verifyRuntimePrerequisites,
} from './deploy.mjs';
import { run as runProcess } from '../azure/lib/exec.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const REPOSITORY = 'sabbour/agentweaver';
const REGISTRY = 'ghcr.io/sabbour';
const SHA = /^[a-f0-9]{40}$/;
const DIGEST = /^sha256:[a-f0-9]{64}$/;
const hash = value => createHash('sha256').update(value).digest('hex');
const fail = message => { throw new Error(`integration deployment: ${message}`); };
const exactTarget = {
  subscriptionId: '90ab3701-83f0-4ba1-a90a-f2e68683adab',
  tenantId: '72f988bf-86f1-41af-91ab-2d7cd011db47',
  resourceGroup: 'aw-v1-p0',
  clusterName: 'aw-v1-p0-aks',
  namespace: 'agentweaver-v1-p0',
  nodeMin: 2,
  nodeMax: 3,
};

function requiredString(value, name) {
  if (typeof value !== 'string' || value.trim() !== value || value.length === 0) {
    fail(`${name} is required and must be an exact nonempty value`);
  }
}

export function parseIntegrationArgs(argv = []) {
  const values = {};
  let execute = false;
  const options = new Map([
    ['--source-sha', 'sourceSha'],
    ['--manifest', 'manifestPath'],
    ['--provenance', 'provenancePath'],
    ['--publication', 'publicationPath'],
    ['--runtime-manifest', 'runtimeManifestPath'],
    ['--provider-catalog-configmap', 'providerCatalogConfigMapName'],
    ['--provider-catalog-source-commit', 'providerCatalogSourceCommit'],
    ['--subscription-id', 'subscriptionId'],
    ['--tenant-id', 'tenantId'],
    ['--resource-group', 'resourceGroup'],
    ['--cluster', 'clusterName'],
    ['--namespace', 'namespace'],
    ['--node-min', 'nodeMin'],
    ['--node-max', 'nodeMax'],
    ['--result', 'resultPath'],
  ]);
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    if (arg === '--execute') {
      execute = true;
      continue;
    }
    const name = [...options.keys()].find(option => arg === option || arg.startsWith(`${option}=`));
    if (!name) fail(`unknown argument "${arg}"`);
    const key = options.get(name);
    const value = arg.startsWith(`${name}=`) ? arg.slice(name.length + 1) : argv[++index];
    if (value === undefined || value === '' || value.startsWith('--')) fail(`${name} requires an explicit value`);
    values[key] = value;
  }
  for (const key of ['sourceSha', 'manifestPath', 'provenancePath', 'publicationPath',
    'subscriptionId', 'tenantId', 'resourceGroup', 'clusterName', 'namespace', 'nodeMin', 'nodeMax']) {
    requiredString(values[key], key);
  }
  if (!values.runtimeManifestPath) {
    requiredString(values.providerCatalogConfigMapName, '--provider-catalog-configmap');
    requiredString(values.providerCatalogSourceCommit, '--provider-catalog-source-commit');
  }
  if (!SHA.test(values.sourceSha)) fail('--source-sha must be a lowercase 40-character Git commit SHA');
  const target = {
    subscriptionId: values.subscriptionId,
    tenantId: values.tenantId,
    resourceGroup: values.resourceGroup,
    clusterName: values.clusterName,
    namespace: values.namespace,
    nodeMin: Number(values.nodeMin),
    nodeMax: Number(values.nodeMax),
  };
  validateTarget(target);
  return { ...values, target, execute, dryRun: !execute };
}

function parseJson(value, description) {
  try {
    return JSON.parse(value);
  } catch (error) {
    fail(`${description} is not valid JSON: ${error.message}`);
  }
}

function readInput(value, root, readFile, description) {
  try {
    return parseJson(readFile(path.resolve(root, value), 'utf8'), description);
  } catch (error) {
    if (error.message.startsWith('integration deployment:')) throw error;
    fail(`cannot read ${description}: ${error.message}`);
  }
}

function manifestHash(manifest) {
  return hash(JSON.stringify(manifest));
}

export function validateIntegrationPublication(receipt, manifest, provenance, sourceSha, sourceTree,
  preparedProvenanceSha256 = hash(JSON.stringify(provenance))) {
  if (!receipt || typeof receipt !== 'object' || Array.isArray(receipt) ||
      receipt.schemaVersion !== 1 || receipt.mode !== 'images-only' ||
      receipt.status !== 'published' || receipt.sourceSha !== sourceSha ||
      receipt.sourceTree !== sourceTree || !SHA.test(receipt.claimSha ?? '') ||
      !/^[a-f0-9]{64}$/.test(receipt.provenanceSha256 ?? '') ||
      !Array.isArray(receipt.planned) || !Array.isArray(receipt.published)) {
    fail('images-only publication receipt is partial or not bound to the exact source and tree');
  }
  const components = selectIntegrationImageComponents(manifest);
  if (manifest.stage !== 'draft' || manifest.evidence !== undefined ||
      provenance.schemaVersion !== 1 || provenance.stage !== 'draft' ||
      provenance.sourceSha !== sourceSha || provenance.manifestSha256 !== manifestHash(manifest) ||
      receipt.provenanceSha256 !== preparedProvenanceSha256 ||
      JSON.stringify(provenance.integration) !== JSON.stringify({
        mode: 'images-only', sourceTree, imageTag: sourceSha,
      })) {
    fail('preparation provenance, draft manifest, and images-only receipt do not match');
  }
  if (!Array.isArray(provenance.components) || provenance.components.length !== components.length ||
      !Array.isArray(provenance.artifacts) || provenance.artifacts.length !== components.length ||
      receipt.planned.length !== components.length || receipt.published.length !== components.length) {
    fail('integration preparation and publication must contain exactly ten service images');
  }

  const prepared = new Map();
  for (const component of components) {
    const record = provenance.components.find(item => item?.id === component.id);
    const artifact = provenance.artifacts.find(item => item?.componentId === component.id);
    const expectedLabels = {
      'org.opencontainers.image.revision': sourceSha,
      'io.agentweaver.source-tree': sourceTree,
      'org.opencontainers.image.version': component.version,
    };
    const expectedArtifactPath = component.id === 'Agentweaver.AgentHost' || component.id === 'Agentweaver.Web'
      ? `${component.id}.${component.version}.tar`
      : `${component.id}.${component.version}.tar.gz`;
    if (record?.kind !== 'service' || record.version !== component.version ||
        record.project !== component.project || artifact?.kind !== 'image' ||
        artifact.path !== expectedArtifactPath ||
        artifact?.tag !== sourceSha ||
        artifact.imageReference !== `${componentImageRepository(component.id)}:${sourceSha}` ||
        (component.id === 'Agentweaver.AgentHost' &&
          artifact.imageConfigDigest !== record.build?.measurement?.image?.configDigest) ||
        !DIGEST.test(artifact.imageConfigDigest ?? '') ||
        JSON.stringify(artifact.integrationLabels) !== JSON.stringify(expectedLabels) ||
        JSON.stringify(artifact.labels) !== JSON.stringify(expectedLabels) ||
        !/^[a-f0-9]{64}$/.test(artifact.sha256 ?? '')) {
      fail(`prepared image provenance for ${component.id} is incomplete or changed`);
    }
    if (component.id === 'Agentweaver.AgentHost') {
      const measurement = record.build?.measurement;
      if (measurement?.kind !== 'agenthost-image' || measurement.schemaVersion !== 1 ||
          measurement.sourceSha !== sourceSha || measurement.sourceTree !== sourceTree ||
          measurement.image?.configDigest !== artifact.imageConfigDigest ||
          measurement.image?.version !== component.version || measurement.image?.platform !== 'linux/amd64' ||
          !Number.isSafeInteger(measurement.image.compressedPullBytes) ||
          measurement.image.compressedPullBytes <= 0) {
        fail('prepared AgentHost OCI measurement does not match the source-bound image config');
      }
    }
    prepared.set(component.id, { component, artifact, measurement: record.build?.measurement });
  }

  const planned = new Map();
  for (const entry of receipt.planned) {
    if (!entry || typeof entry !== 'object' || planned.has(entry.id)) fail('integration publication plan has duplicate or invalid entries');
    planned.set(entry.id, entry);
  }
  const published = new Map();
  for (const entry of receipt.published) {
    if (!entry || typeof entry !== 'object' || published.has(entry.id)) fail('integration publication result has duplicate or invalid entries');
    published.set(entry.id, entry);
  }

  return components.map(component => {
    const preparedImage = prepared.get(component.id);
    const plan = planned.get(component.id);
    const result = published.get(component.id);
    const repository = `${REGISTRY}/${componentImageRepository(component.id)}`;
    if (!plan || !result || plan.version !== component.version || plan.kind !== 'image' ||
        plan.sha256 !== preparedImage.artifact.sha256 ||
        plan.destination !== `${repository}:${sourceSha}` ||
        result.version !== component.version || result.kind !== 'image' ||
        result.archiveSha256 !== plan.sha256 || result.sourceSha !== sourceSha ||
        result.sourceTree !== sourceTree || result.tag !== sourceSha ||
        result.configDigest !== preparedImage.artifact.imageConfigDigest ||
        !DIGEST.test(result.configDigest ?? '') || typeof result.image !== 'string' ||
        !result.image.startsWith(`${repository}@`) ||
        !DIGEST.test(result.image.slice(result.image.lastIndexOf('@') + 1)) ||
        /^sha256:0+$/.test(result.image.slice(result.image.lastIndexOf('@') + 1))) {
      fail(`images-only receipt does not prove the prepared immutable image for ${component.id}`);
    }
    if (component.id === 'Agentweaver.AgentHost') {
      const measurement = result.preparedOciMeasurement;
      if (measurement?.kind !== 'agenthost-image' || measurement.schemaVersion !== 1 ||
          measurement.sourceSha !== sourceSha || measurement.sourceTree !== sourceTree ||
          measurement.image?.configDigest !== result.configDigest ||
          measurement.image?.version !== component.version ||
          measurement.image?.platform !== 'linux/amd64' ||
          !Number.isSafeInteger(measurement.image.compressedPullBytes) ||
          measurement.image.compressedPullBytes <= 0) {
        fail('published AgentHost receipt does not retain the exact prepared OCI measurement');
      }
    } else if (result.preparedOciMeasurement !== undefined) {
      fail(`${component.id} publication includes unsupported AgentHost measurement data`);
    }
    return { ...preparedImage, image: result.image, result };
  });
}

export async function verifyIntegrationPublicationRemote(receipt, sourceSha, { root, run }) {
  const namespace = `agentweaver-integration/${sourceSha}`;
  const repository = `repos/${REPOSITORY}`;
  const readRef = name => parseJson(run('gh', ['api',
    `${repository}/git/ref/tags/${encodeURIComponent(`${namespace}/${name}`)}`], { cwd: root }),
  `integration ${name} ref`);
  const readTag = (sha, name) => parseJson(run('gh', ['api',
    `${repository}/git/tags/${sha}`], { cwd: root }), `integration ${name} tag`);
  const claimRef = readRef('claim');
  const resultRef = readRef('result');
  for (const [name, ref, expectedSha] of [
    ['claim', claimRef, receipt.claimSha],
    ['result', resultRef, undefined],
  ]) {
    if (ref.object?.type !== 'tag' || !SHA.test(ref.object.sha ?? '') ||
        expectedSha !== undefined && ref.object.sha !== expectedSha) {
      fail(`immutable integration ${name} ref is missing or differs from the local receipt`);
    }
  }
  const claim = readTag(claimRef.object.sha, 'claim');
  const expectedClaim = {
    schemaVersion: 1,
    sourceSha,
    mode: 'images-only',
    sourceTree: receipt.sourceTree,
    provenanceSha256: receipt.provenanceSha256,
    planned: receipt.planned,
    status: 'claimed',
    published: [],
  };
  if (claim.tag !== `${namespace}/claim` || claim.object?.type !== 'commit' ||
      claim.object.sha !== sourceSha ||
      JSON.stringify(parseJson(claim.message, 'integration claim')) !== JSON.stringify(expectedClaim)) {
    fail('immutable integration claim is not bound to the exact source and publication plan');
  }
  const result = readTag(resultRef.object.sha, 'result');
  if (result.tag !== `${namespace}/result` || result.object?.type !== 'commit' ||
      result.object.sha !== sourceSha ||
      JSON.stringify(parseJson(result.message, 'integration publication result')) !== JSON.stringify(receipt)) {
    fail('local images-only receipt differs from the immutable GitHub publication result');
  }
  return true;
}

function readbackResources(runtimeManifest, images, acceptedDigests, target, root, run) {
  const imageByName = new Map(images.map(image => [image.name, image]));
  const readbacks = [];
  const add = (kind, name, raw) => {
    const resource = parseJson(raw, `${kind} ${name} readback`);
    if (resource.metadata?.name !== name || resource.metadata?.namespace !== target.namespace) {
      fail(`${kind} ${name} readback is not in the exact P0 namespace`);
    }
    readbacks.push({ kind, name, sha256: hash(raw) });
    return resource;
  };
  for (const item of runtimeManifest.items.filter(resource => resource.kind === 'Deployment')) {
    const name = item.metadata.name;
    const expected = imageByName.get(name);
    const deployment = add('deployment', name, run('kubectl', [
      'get', 'deployment', name, '--namespace', target.namespace, '--output', 'json',
    ], { cwd: root }));
    if (!expected || deployment.spec?.template?.spec?.containers?.length !== 1 ||
        deployment.spec.template.spec.containers[0].image !== expected.image) {
      fail(`${name} deployment readback does not match the verified integration image`);
    }
    const service = add('service', name, run('kubectl', [
      'get', 'service', name, '--namespace', target.namespace, '--output', 'json',
    ], { cwd: root }));
    const expectedService = runtimeManifest.items.find(resource =>
      resource.kind === 'Service' && resource.metadata.name === name);
    const projectService = value => ({
      type: value?.spec?.type,
      selector: value?.spec?.selector,
      ports: value?.spec?.ports,
    });
    if (!expectedService ||
        JSON.stringify(projectService(service)) !== JSON.stringify(projectService(expectedService))) {
      fail(`${name} Service readback differs from the exact ClusterIP runtime manifest`);
    }
    const podsRaw = run('kubectl', [
      'get', 'pods', '--namespace', target.namespace,
      '--selector', `app.kubernetes.io/name=${name}`, '--output', 'json',
    ], { cwd: root });
    const pods = parseJson(podsRaw, `${name} pod readback`);
    const ready = (pods.items ?? []).filter(pod => pod.status?.conditions?.some(condition =>
      condition.type === 'Ready' && condition.status === 'True'));
    if (!Array.isArray(pods.items) || pods.items.some(pod => pod.metadata?.namespace !== target.namespace) ||
        ready.length !== 1 || ready[0].spec?.containers?.[0]?.image !== expected.image ||
        ready[0].status?.containerStatuses?.[0]?.ready !== true) {
      fail(`${name} pod readback does not contain one ready pod with the exact integration image`);
    }
    const imageId = ready[0].status.containerStatuses[0].imageID ?? '';
    const liveDigest = imageId.slice(imageId.lastIndexOf('@') + 1);
    if (!(acceptedDigests.get(name) ?? new Set([expected.image.slice(expected.image.lastIndexOf('@') + 1)]))
      .has(liveDigest)) {
      fail(`${name} pod readback image ID is not one of the verified registry digests`);
    }
    readbacks.push({ kind: 'pods', name, sha256: hash(podsRaw) });
  }
  readbacks.sort((left, right) => `${left.kind}/${left.name}`.localeCompare(`${right.kind}/${right.name}`));
  return {
    resources: readbacks,
    sha256: hash(JSON.stringify(readbacks)),
  };
}

export async function deployIntegration(options = {}, dependencies = {}) {
  const parsed = options.argv ? parseIntegrationArgs(options.argv) : options;
  if (!SHA.test(parsed.sourceSha ?? '')) fail('an exact lowercase source SHA is required');
  validateTarget(parsed.target);
  const root = path.resolve(dependencies.root ?? repositoryRoot);
  const run = dependencies.run ?? ((file, args, opts) => runProcess(file, args, opts).stdout.trim());
  const readFile = dependencies.readFile ?? readFileSync;
  const git = dependencies.git ?? ((...args) => run('git', args, { cwd: root }));
  const manifest = options.manifest ?? readInput(parsed.manifestPath, root, readFile, 'draft manifest');
  const provenanceBytes = options.provenanceBytes ??
    (parsed.provenancePath
      ? readFile(path.resolve(root, parsed.provenancePath))
      : Buffer.from(JSON.stringify(options.provenance)));
  const provenance = options.provenance ?? parseJson(provenanceBytes.toString(), 'integration provenance');
  const publication = options.publication ?? readInput(parsed.publicationPath, root, readFile, 'integration publication receipt');
  const runtimeDocument = options.runtimeManifest ?? (parsed.runtimeManifestPath
    ? readInput(parsed.runtimeManifestPath, root, readFile, 'runtime manifest')
    : undefined);

  if (git('rev-parse', 'HEAD') !== parsed.sourceSha ||
      git('status', '--porcelain') !== '') {
    fail('deployment requires a clean local checkout at the exact integration source SHA');
  }
  const sourceTree = git('rev-parse', `${parsed.sourceSha}^{tree}`);
  if (!SHA.test(sourceTree)) fail('cannot resolve the exact source tree for integration deployment');
  validateManifest(manifest, { root });
  if (manifest.stage !== 'draft' || manifest.evidence !== undefined) {
    fail('integration deployment accepts only the tracked draft manifest without deployment evidence');
  }
  const manifestFullPath = path.resolve(root, parsed.manifestPath);
  const manifestRelativePath = path.relative(root, manifestFullPath).replaceAll('\\', '/');
  if (manifestRelativePath.startsWith('../') || path.isAbsolute(manifestRelativePath)) {
    fail('manifest must be inside the exact source checkout');
  }
  if (JSON.stringify(parseJson(git('show', `HEAD:${manifestRelativePath}`), 'tracked draft manifest')) !==
      JSON.stringify(manifest)) {
    fail('draft manifest differs from the exact source SHA');
  }
  const images = validateIntegrationPublication(publication, manifest, provenance, parsed.sourceSha, sourceTree,
    hash(provenanceBytes));
  await (dependencies.verifyPublicationRemote ?? verifyIntegrationPublicationRemote)(publication, parsed.sourceSha, { root, run });

  const verifyServiceImage = dependencies.verifyServiceImage ?? verifyServiceRegistryImage;
  const verifyAgentHostImage = dependencies.verifyAgentHostImage ?? verifyAgentHostRegistryImage;
  const acceptedDigests = new Map();
  for (const entry of images) {
    const observation = await verifyServiceImage(entry.result.image, dependencies.fetch ?? globalThis.fetch);
    const expectedTopDigest = entry.result.image.slice(entry.result.image.lastIndexOf('@') + 1);
    if (observation.sourceSha !== parsed.sourceSha || observation.sourceTree !== sourceTree ||
        observation.version !== entry.component.version ||
        observation.configDigest !== entry.result.configDigest ||
        !observation.acceptedDigests?.has(expectedTopDigest)) {
      fail(`anonymous GHCR config for ${entry.component.id} differs from preparation, source, tree, or draft manifest`);
    }
    const image = {
      id: entry.component.id,
      name: SERVICE_NAMES[entry.component.id],
      image: entry.result.image,
    };
    if (!image.name) fail(`no runtime service name is defined for ${entry.component.id}`);
    acceptedDigests.set(image.name, observation.acceptedDigests);
    if (entry.component.id === 'Agentweaver.AgentHost') {
      const published = await verifyAgentHostImage({
        ...image,
        preparedOciMeasurement: entry.result.preparedOciMeasurement,
      }, dependencies.fetch ?? globalThis.fetch);
      if (published.digest !== observation.digest ||
          published.image !== observation.image ||
          !published.acceptedDigests.has(published.digest)) {
        fail('AgentHost registry measurement differs from its independently verified source-bound config');
      }
      image.sandboxImage = published.image;
      image.sandboxImageDigest = published.digest;
      image.compressedPullBytes = published.compressedPullBytes;
      acceptedDigests.set(image.name, published.acceptedDigests);
    }
    entry.runtimeImage = image;
  }
  const runtimeImages = images.map(entry => entry.runtimeImage);
  const runtimeList = runtimeDocument ?? createRuntimeList(runtimeImages, parsed.target.namespace, {
    configMapName: parsed.providerCatalogConfigMapName,
    sourceCommit: parsed.providerCatalogSourceCommit,
  });
  const runtimeManifest = validateRuntimeManifest(runtimeList, runtimeImages, parsed.target.namespace);
  const targetInfo = await (dependencies.verifyTarget ?? verifyAzureTarget)(parsed.target, { root, run });
  (dependencies.verifyPrerequisites ?? verifyRuntimePrerequisites)(runtimeManifest, parsed.target, { root, run });
  await (dependencies.verifyRouting ?? verifyExistingP0BrokerRouting)(parsed.target, { root, run });
  run('kubectl', ['apply', '--dry-run=client', '--filename', '-'], {
    cwd: root, input: `${JSON.stringify(runtimeManifest)}\n`,
  });

  if (parsed.dryRun || !parsed.execute) {
    return {
      ok: true, dryRun: true, sourceSha: parsed.sourceSha, sourceTree,
      target: parsed.target, services: runtimeImages.filter(image => image.id !== 'Agentweaver.AgentHost')
        .map(({ id, image }) => ({ id, image })),
      perSandboxAgentHost: runtimeImages.find(image => image.id === 'Agentweaver.AgentHost').sandboxImage,
      renderedResources: runtimeManifest.items.length, targetInfo,
    };
  }

  run('kubectl', ['apply', '--filename', '-'], {
    cwd: root, input: `${JSON.stringify(runtimeManifest)}\n`,
  });
  const freshImages = [];
  for (const entry of images) {
    const observation = await verifyServiceImage(entry.result.image, dependencies.fetch ?? globalThis.fetch);
    const expectedTopDigest = entry.result.image.slice(entry.result.image.lastIndexOf('@') + 1);
    if (observation.sourceSha !== parsed.sourceSha || observation.sourceTree !== sourceTree ||
        observation.version !== entry.component.version ||
        observation.configDigest !== entry.result.configDigest ||
        !observation.acceptedDigests?.has(expectedTopDigest)) {
      fail(`registry image for ${entry.component.id} changed or failed exact-source readback after apply`);
    }
    freshImages.push(observation);
  }
  for (const [index, image] of runtimeImages.entries()) {
    const name = image.name;
    acceptedDigests.set(name, freshImages[index].acceptedDigests);
  }
  const freshHost = images.find(entry => entry.component.id === 'Agentweaver.AgentHost');
  const hostObservation = await verifyAgentHostImage({
    ...freshHost.runtimeImage,
    preparedOciMeasurement: freshHost.result.preparedOciMeasurement,
  }, dependencies.fetch ?? globalThis.fetch);
  if (hostObservation.digest !== freshImages.find((_, index) =>
    images[index].component.id === 'Agentweaver.AgentHost').digest) {
    fail('AgentHost registry measurement changed after apply');
  }
  const environmentImage = runtimeImages.find(image => image.name === 'environment');
  const environmentEntry = runtimeManifest.items.find(item =>
    item.kind === 'Deployment' && item.metadata.name === 'environment');
  const hostPin = new Map(environmentEntry.spec.template.spec.containers[0].env
    .map(({ name, value }) => [name, value]));
  if (hostPin.get('Environment__Sandbox__AgentSandbox__ContainerImage') !== hostObservation.image ||
      hostPin.get('Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes') !==
        String(hostObservation.compressedPullBytes)) {
    fail('AgentHost registry readback no longer matches the Environment deployment pin');
  }
  runtimeImages.find(image => image.id === 'Agentweaver.AgentHost').sandboxImage = hostObservation.image;
  runtimeImages.find(image => image.id === 'Agentweaver.AgentHost').compressedPullBytes = hostObservation.compressedPullBytes;
  await (dependencies.verifyTarget ?? verifyAzureTarget)(parsed.target, { root, run });
  await (dependencies.verifyLive ?? ((refs, target, digests) =>
    verifyLiveDeployment(refs, target, digests, { root, run })))(runtimeImages, parsed.target, acceptedDigests);
  const readbacks = readbackResources(runtimeManifest, runtimeImages, acceptedDigests, parsed.target, root, run);
  const manifestSha256 = manifestHash(manifest);
  const provenanceSha256 = hash(provenanceBytes);
  const publicationSha256 = hash(JSON.stringify(publication));
  const runtimeManifestSha256 = hash(JSON.stringify(runtimeManifest));
  const result = {
    schemaVersion: 1,
    status: 'deployed',
    sourceSha: parsed.sourceSha,
    sourceTree,
    manifestSha256,
    provenanceSha256,
    publicationSha256,
    runtimeManifestSha256,
    target: parsed.target,
    registryImages: images.map(entry => ({
      id: entry.component.id,
      image: entry.result.image,
      configDigest: entry.result.configDigest,
      sourceSha: parsed.sourceSha,
      sourceTree,
      version: entry.component.version,
    })),
    readbacks,
    completedAt: (dependencies.now ?? (() => new Date()))().toISOString(),
  };
  const resultPath = path.resolve(root, parsed.resultPath ?? `artifacts/release/integration/${parsed.sourceSha}-deployment.json`);
  if (!resultPath.startsWith(`${root}${path.sep}`)) fail('deployment result path must remain inside the source checkout');
  mkdirSync(path.dirname(resultPath), { recursive: true });
  try {
    writeFileSync(resultPath, `${JSON.stringify(result, null, 2)}\n`, { flag: 'wx' });
  } catch (error) {
    fail(`cannot create deployment result without overwriting an earlier result: ${error.message}`);
  }
  return {
    ok: true, dryRun: false, sourceSha: parsed.sourceSha, sourceTree,
    services: runtimeImages.filter(image => image.id !== 'Agentweaver.AgentHost').map(({ id }) => id),
    resultPath, readbackSha256: readbacks.sha256,
  };
}

function helpText() {
  return `release:deploy-integration -- validate and deploy source-bound integration images\n\nUsage:\n  npm run release:deploy-integration -- --source-sha <40-character-sha> --manifest <draft-manifest.json> --provenance <provenance.json> --publication <integration-publication.json> --subscription-id <id> --tenant-id <id> --resource-group <name> --cluster <name> --namespace <name> --node-min 2 --node-max 3 --provider-catalog-configmap <name> --provider-catalog-source-commit <sha> [--runtime-manifest <runtime-list.json>] [--result <file>] [--execute]\n\nThe default is read-only. --execute is required to apply the nine shared P0 services. This command does not use a GitHub Release or final-release manifest.`;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    if (process.argv.includes('--help') || process.argv.includes('-h')) {
      console.log(helpText());
    } else {
      const result = await deployIntegration({ argv: process.argv.slice(2) });
      console.log(result.dryRun
        ? `Read-only integration preflight verified ${result.services.length} service images at ${result.sourceSha}.`
        : `Integration deployment verified ${result.services.length} services at ${result.sourceSha}; readback ${result.readbackSha256}.`);
    }
  } catch (error) {
    console.error(`Integration deployment failed: ${error.message}`);
    process.exitCode = 1;
  }
}
