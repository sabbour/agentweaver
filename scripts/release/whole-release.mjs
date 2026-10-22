/**
 * Shared whole-release planning and verification for the P0 installation.
 * Component rollouts may succeed while the installation is still partial; only this
 * flow can write a deployed whole-release receipt.
 */
import { mkdirSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { AgentweaverClient } from '../api-harness/lib/client.mjs';
import { createBrokerAuthProvider } from '../harness-shared/broker-auth.mjs';
import {
  canonicalJson, deploymentInventory, imageDigest, planDeploymentInventory,
  verifyDeploymentPreservation, verifyDeploymentReady,
} from './image-rollout-common.mjs';
import { selectPublishedComponentImage } from './publication-image.mjs';
import { componentImageRepository } from './pack.mjs';
import { verifyServiceRegistryImage } from './registry.mjs';
import { readImageSchemaRange, STATE_NAME } from '../azure/lib/db-maintenance.mjs';

export const WHOLE_RELEASE_RECEIPT_DIRECTORY =
  path.join(homedir(), '.agentweaver', 'whole-release');
export const WHOLE_RELEASE_LAST_RECEIPT =
  path.join(WHOLE_RELEASE_RECEIPT_DIRECTORY, 'last.json');
export const WHOLE_RELEASE_SERVICES = Object.freeze([
  { componentId: 'Agentweaver.Web', deployment: 'web', container: 'web' },
  { componentId: 'Agentweaver.Identity.Broker', deployment: 'identity-broker', container: 'identity-broker' },
  { componentId: 'Agentweaver.EventsAndSessions', deployment: 'events-and-sessions', container: 'events-and-sessions' },
  { componentId: 'Agentweaver.Gateway', deployment: 'gateway', container: 'gateway' },
  { componentId: 'Agentweaver.Mcp', deployment: 'mcp', container: 'mcp' },
  { componentId: 'Agentweaver.Orchestrator', deployment: 'orchestrator', container: 'orchestrator' },
  { componentId: 'Agentweaver.Environment', deployment: 'environment', container: 'environment' },
  { componentId: 'Agentweaver.Knowledge', deployment: 'knowledge', container: 'knowledge' },
  { componentId: 'Agentweaver.Projects.Config', deployment: 'projects-config', container: 'projects-config' },
]);

const SHA = /^[a-f0-9]{40}$/;
const IMAGE_DIGEST = /^sha256:[a-f0-9]{64}$/;
const SERVICE_NAMES = new Set(WHOLE_RELEASE_SERVICES.map(service => service.deployment));

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

export function wholeReleaseFail(message) {
  throw new Error(`Whole-release rollout: ${message}`);
}

function parseJson(text, name) {
  try {
    return JSON.parse(text);
  } catch (error) {
    wholeReleaseFail(`${name} is not valid JSON: ${error.message}`);
  }
}

function requireAncestor(exec, root, sourceSha, target, description) {
  const result = exec('git', ['merge-base', '--is-ancestor', sourceSha, target], { cwd: root, check: false });
  if (result.status !== 0) wholeReleaseFail(`${description} ${sourceSha} is not an ancestor of ${target}.`);
}

function gitOutput(exec, root, args, action) {
  const result = exec('git', args, { cwd: root, check: false });
  if (result.status !== 0) wholeReleaseFail(`${action}: ${(result.stderr ?? '').trim()}`);
  return result.stdout;
}

/**
 * Resolve every service from the current receipt, or from the explicitly supplied
 * previous receipt when a patch publication omitted an unchanged image.
 */
export function resolveWholeReleaseImages(publication, manifest, previousPublication, {
  git, exec, root, verifyImage = verifyServiceRegistryImage,
}) {
  if (!isRecord(publication) || publication.status !== 'published' ||
      !SHA.test(publication.sourceSha ?? '') || !Array.isArray(publication.planned) ||
      !Array.isArray(publication.published)) {
    wholeReleaseFail('publication receipt is not a complete published receipt.');
  }
  if (!isRecord(manifest) || manifest.stage !== 'draft' || !Array.isArray(manifest.components)) {
    wholeReleaseFail('source manifest is not a tracked draft with a component list.');
  }
  const previous = previousPublication;
  if (previous !== undefined && (!isRecord(previous) || previous.status !== 'published' ||
      !SHA.test(previous.sourceSha ?? ''))) {
    wholeReleaseFail('the previous publication receipt is not a complete published receipt.');
  }
  const results = [];
  for (const service of WHOLE_RELEASE_SERVICES) {
    const currentComponents = manifest.components.filter(component => component?.id === service.componentId);
    if (currentComponents.length !== 1) {
      wholeReleaseFail(`source manifest must contain exactly one ${service.componentId} component.`);
    }
    const currentPlanned = publication.planned.filter(entry => entry?.id === service.componentId);
    const currentPublished = publication.published.filter(entry => entry?.id === service.componentId);
    const currentHasImage = currentPublished.length > 0;
    if (!currentHasImage && currentPlanned.length > 0) {
      wholeReleaseFail(`${service.deployment} was planned by this publication but has no published image.`);
    }
    const sourcePublication = currentHasImage ? publication : previous;
    if (!sourcePublication) {
      wholeReleaseFail(`${service.deployment} is absent from the publication and has no previous receipt.`);
    }
    const sourceManifest = currentHasImage ? manifest : manifestForPrevious(previous, service, exec, root);
    if (!currentHasImage) {
      const previousComponents = sourceManifest.components?.filter(component =>
        component?.id === service.componentId) ?? [];
      if (previousComponents.length !== 1 ||
          previousComponents[0].version !== currentComponents[0].version) {
        wholeReleaseFail(`${service.deployment} is missing from this publication but changed since the previous publication.`);
      }
      // A fallback must come from this release's history, never a newer publication.
      requireAncestor(exec, root, previous.sourceSha, publication.sourceSha, `${service.deployment} previous publication source`);
    }
    const selected = selectPublishedComponentImage(sourcePublication, sourceManifest, service.componentId, {
      git, exec, root, label: service.deployment,
    });
    requireAncestor(exec, root, selected.imageSourceSha, 'origin/v1', `${service.deployment} image source`);
    results.push({ ...service, ...selected, sourcePublicationSha: sourcePublication.sourceSha });
  }
  return results;
}

function manifestForPrevious(previous, service, exec, root) {
  const manifest = parseJson(gitOutput(exec, root, ['show', `${previous.sourceSha}:releases/foundation.json`],
    'Could not read the previous source-bound manifest'), 'previous source-bound release manifest');
  if (!manifest.components?.some(component => component?.id === service.componentId)) {
    wholeReleaseFail(`previous source-bound manifest has no ${service.deployment} component.`);
  }
  return manifest;
}

export async function verifyWholeReleaseImages(images, verifyImage) {
  const verified = [];
  for (const image of images) {
    const registry = await verifyImage(image.image);
    if (!registry || !IMAGE_DIGEST.test(registry.digest ?? '') ||
        !(registry.acceptedDigests instanceof Set) || !registry.acceptedDigests.has(image.digest) ||
        registry.sourceSha !== image.imageSourceSha || registry.sourceTree !== image.imageSourceTree ||
        registry.version !== image.version) {
      wholeReleaseFail(`${image.deployment} digest, source commit, source tree, or version differs from its publication receipt.`);
    }
    verified.push({ ...image, acceptedDigests: registry.acceptedDigests });
  }
  return verified;
}

/**
 * Reject unknown Deployments and validate the fixed service identity before a
 * patch is planned. This prevents a similarly named workload from being selected.
 */
export function compareWholeReleaseState(inventory, images) {
  const liveNames = new Set(inventory.byName.keys());
  const unknown = [...liveNames].filter(name => !SERVICE_NAMES.has(name));
  if (unknown.length) wholeReleaseFail(`unknown or unmapped Deployments: ${unknown.sort().join(', ')}.`);
  const missing = [...SERVICE_NAMES].filter(name => !liveNames.has(name));
  if (missing.length) wholeReleaseFail(`required Deployments are missing: ${missing.sort().join(', ')}.`);

  return images.map(image => {
    const deployment = inventory.byName.get(image.deployment);
    const labels = deployment.metadata?.labels ?? {};
    const podLabels = deployment.spec?.template?.metadata?.labels ?? {};
    const selector = { 'app.kubernetes.io/name': image.deployment };
    if (labels['app.kubernetes.io/name'] !== image.deployment ||
        labels['agentweaver.io/service'] !== image.deployment ||
        podLabels['app.kubernetes.io/name'] !== image.deployment ||
        podLabels['agentweaver.io/service'] !== image.deployment ||
        podLabels['agentweaver.io/workload'] !== image.deployment ||
        canonicalJson(deployment.spec?.selector?.matchLabels) !== canonicalJson(selector)) {
      wholeReleaseFail(`${image.deployment} Deployment identity and selector do not match the reviewed workload.`);
    }
    const containers = deployment.spec?.template?.spec?.containers ?? [];
    const matches = containers.map((container, index) => ({ container, index }))
      .filter(entry => entry.container?.name === image.container);
    if (matches.length !== 1 || (deployment.spec.replicas ?? 1) < 1 ||
        !Number.isSafeInteger(deployment.spec.replicas ?? 1)) {
      wholeReleaseFail(`${image.deployment} must have one named application container and at least one replica.`);
    }
    const selected = matches[0];
    const repository = `ghcr.io/sabbour/${componentImageRepository(image.componentId)}`;
    if (typeof deployment.metadata?.uid !== 'string' ||
        typeof selected.container.image !== 'string' ||
        !selected.container.image.startsWith(`${repository}@`)) {
      wholeReleaseFail(`${image.deployment} has no stable identity or digest-pinned service image.`);
    }
    return {
      ...image,
      deploymentObject: deployment,
      containerIndex: selected.index,
      currentImage: selected.container.image,
      currentDigest: imageDigest(selected.container.image, `${image.deployment} container ${image.container}`, wholeReleaseFail),
      replicas: deployment.spec.replicas ?? 1,
      changed: selected.container.image !== image.image,
    };
  });
}

export function wholeReleaseComparison(services) {
  return services.map(service => ({
    service: service.deployment,
    intended: service.targetImage ?? service.image,
    live: service.currentImage,
    change: Boolean(service.changed),
  }));
}

/**
 * Check the two schema-owning services against the recorded migration state.
 * This is read-only: schema changes remain the responsibility of db:maintenance.
 */
export async function checkSchemaCompatibility({ services, kubectl, deps, root, exec }) {
  const stateResult = kubectl(['get', 'configmap', STATE_NAME, '-o', 'json']);
  if (stateResult.status !== 0) {
    wholeReleaseFail(`could not read ${STATE_NAME}; run db:maintenance before the whole release.`);
  }
  const data = parseJson(stateResult.stdout, 'schema state ConfigMap').data ?? {};
  const checks = [];
  for (const service of services.filter(item =>
    ['Agentweaver.Orchestrator', 'Agentweaver.EventsAndSessions'].includes(item.componentId))) {
    const key = service.deployment === 'orchestrator' ? 'orchestrator.schema' : 'events-and-sessions.schema';
    const currentSchema = Number(data[key]);
    if (!Number.isSafeInteger(currentSchema) || currentSchema < 1) {
      wholeReleaseFail(`${service.deployment} has no recorded migration schema version.`);
    }
    const range = await readImageSchemaRange({
      image: service.image,
      component: service.componentId,
      readImage: deps.readImage ?? (reference => deps.verifyImage(reference)),
      exec,
      repoRoot: root,
    });
    if (range.unknown || currentSchema < range.minSchema || currentSchema > range.maxSchema) {
      wholeReleaseFail(`${service.deployment} target image is incompatible with recorded schema ${currentSchema}.`);
    }
    checks.push({ deployment: service.deployment, schema: currentSchema, accepted: range });
  }
  return checks;
}

/**
 * Use the existing authenticated Gateway client for non-mutating route smoke
 * checks. Every operation is discovered from the live OpenAPI document.
 */
export async function runGatewaySmokeChecks({
  gatewayUrl, tenantId, token, issuer, audience, actorId, projectId, fetchImpl,
}) {
  if (![gatewayUrl, tenantId, token, issuer, audience, actorId, projectId].every(value =>
    typeof value === 'string' && value.length > 0)) {
    wholeReleaseFail('authenticated Gateway smoke check inputs are incomplete.');
  }
  const target = new URL(gatewayUrl);
  const authProvider = createBrokerAuthProvider({
    target, token, issuer, audience, actorId,
  });
  const client = new AgentweaverClient({
    baseUrl: gatewayUrl, authProvider, tenantId, fetchImpl,
  });
  await client.discover();
  const calls = [
    ['listProjects', {}],
    ['listChiefOfStaffPriorities', {}],
    ['listProjectRuns', { pathParameters: { projectId } }],
    ['readProjectMetrics', { pathParameters: { projectId } }],
  ];
  const checks = [];
  for (const [operationId, input] of calls) {
    if (!client.operations.has(operationId)) wholeReleaseFail(`Gateway does not advertise ${operationId}.`);
    const call = await client.invoke(operationId, input);
    if (call.status < 200 || call.status >= 300) wholeReleaseFail(`${operationId} returned HTTP ${call.status}.`);
    checks.push({ operationId, status: call.status, path: call.path });
  }
  return checks;
}

export function writeWholeReleaseReceipt(file, receipt, write = writeFileSync, lastFile = WHOLE_RELEASE_LAST_RECEIPT) {
  const contents = `${JSON.stringify(receipt, null, 2)}\n`;
  mkdirSync(path.dirname(file), { recursive: true });
  mkdirSync(path.dirname(lastFile), { recursive: true });
  write(file, contents, { flag: 'wx' });
  write(lastFile, contents);
}

export function wholeReleaseUpdates(services) {
  return new Map(services.map(service => [service.deployment, [{
    containerIndex: service.containerIndex,
    image: service.image,
  }]]));
}

export function verifyWholeReleaseReadback(before, after, services, updates, fail = wholeReleaseFail) {
  verifyDeploymentPreservation(before, after, updates, {
    namespace: 'agentweaver',
    label: 'whole release',
    ignoredAnnotations: new Map(services.map(service =>
      [service.deployment, ['deployment.kubernetes.io/revision']])),
    fail,
  });
  return services.map(service => {
    const deployment = after.byName.get(service.deployment);
    if (deployment.spec.template.spec.containers[service.containerIndex]?.image !== service.image) {
      fail(`${service.deployment} Deployment digest does not match the intended image.`);
    }
    return {
      deployment: service.deployment,
      ...verifyDeploymentReady(deployment, service.replicas, service.deployment, fail),
    };
  });
}

export function plannedWholeReleaseInventory(before, services) {
  return planDeploymentInventory(before, wholeReleaseUpdates(services), 'agentweaver', wholeReleaseFail).records;
}
