/**
 * Reconciles the complete P0 installation to one source-bound publication.
 * This is the only release script that may write a deployed whole-release receipt.
 */
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { isDeepStrictEqual } from 'node:util';
import { withP0UserKubeconfig } from '../azure/lib/namespace-bootstrap.mjs';
import { run, redact } from '../azure/lib/exec.mjs';
import {
  deploymentInventory, deploymentPodDrainReason, waitForDeploymentPods,
} from './image-rollout-common.mjs';
import { parseKustomizeResources } from './kustomize-runtime.mjs';
import {
  WHOLE_RELEASE_RECEIPT_DIRECTORY, compareWholeReleaseState, checkSchemaCompatibility,
  plannedWholeReleaseInventory, prepareGatewaySmokeContext, resolveWholeReleaseImages,
  runGatewaySmokeChecks, verifyWholeReleaseImages, verifyWholeReleaseReadback,
  wholeReleaseFail, wholeReleaseUpdates, writeWholeReleaseReceipt, wholeReleaseComparison,
} from './whole-release.mjs';
import { componentImageRepository } from './pack.mjs';
import { verifyServiceRegistryImage } from './registry.mjs';
import { LOCK_NAME } from '../azure/lib/db-maintenance.mjs';
import {
  findImmutableRuntimeConfigMapReplacements,
  IMMUTABLE_RUNTIME_CONFIG_MAP_ROLLOUT_DEPLOYMENTS,
  replaceImmutableRuntimeConfigMaps,
  verifyImmutableRuntimeConfigMapReplacementPermissions,
} from './immutable-runtime-config-maps.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const NAMESPACE = 'agentweaver';
const SHA = /^[a-f0-9]{40}$/;
const IMAGE_DIGEST = /^sha256:[a-f0-9]{64}$/;
const CONFIG_MAP_CACHE_EXPIRY_WAIT_MS = 70_000;
const SAFE_ROLLOUT_ORDER = [
  'identity-broker', 'events-and-sessions', 'orchestrator', 'environment',
  'knowledge', 'projects-config', 'gateway', 'mcp', 'web',
];

export const WHOLE_RELEASE_USAGE = `Usage: npm run release:deploy-whole -- --publication <publication.json> --subscription-id <id> --resource-group <name> --cluster <name> [options]

Without --execute the command reads live state and writes a complete rollout plan.

  --publication <file>                Current source-bound publication receipt. Required.
  --previous-publication <file>        Prior published receipt for unchanged images.
  --projects-config-migration-receipt <file>  Successful Projects & Config migration receipt. Required with --execute.
  --subscription-id <id>               Azure subscription containing the P0 cluster. Required.
  --resource-group <name>              Resource group containing the P0 cluster. Required.
  --cluster <name>                     P0 AKS cluster name. Required.
  --execute                            Roll out every differing service and run smoke checks.
  --gateway-url <url>                  Override the deployed Gateway origin for smoke checks.
  --tenant-id <id>                     Override the signed-in actor's Gateway tenant.
  --gateway-token-env <name>           Environment variable containing a broker token.
  --issuer <url>                       Override the deployed Broker token issuer.
  --audience <value>                   Override the deployed Broker token audience.
  --actor-id <id>                      Require this Broker token subject.
  --project-id <id>                    Require this active project for run and metrics checks.
  --receipt-dir <directory>            Receipt directory. Default: ~/.agentweaver/whole-release.
  --readiness-timeout-seconds <n>      Rollout timeout. Default: 600.
  --help                               Show this help.
`;

function parseJson(text, name) {
  try {
    return JSON.parse(text);
  } catch (error) {
    wholeReleaseFail(`${name} is not valid JSON: ${error.message}`);
  }
}

export function parseWholeReleaseArgs(argv = []) {
  const options = {
    execute: false, publicationPath: undefined, previousPublicationPath: undefined,
    projectsConfigMigrationReceiptPath: undefined,
    subscriptionId: undefined, resourceGroup: undefined, clusterName: undefined,
    gatewayUrl: undefined, tenantId: undefined, gatewayTokenEnv: 'AGENTWEAVER_GATEWAY_TOKEN',
    issuer: undefined, audience: undefined, actorId: undefined, projectId: undefined,
    receiptDir: WHOLE_RELEASE_RECEIPT_DIRECTORY, readinessTimeoutSeconds: 600, help: false,
  };
  const valued = new Map([
    ['--publication', 'publicationPath'], ['--previous-publication', 'previousPublicationPath'],
    ['--projects-config-migration-receipt', 'projectsConfigMigrationReceiptPath'],
    ['--subscription-id', 'subscriptionId'], ['--resource-group', 'resourceGroup'],
    ['--cluster', 'clusterName'], ['--gateway-url', 'gatewayUrl'], ['--tenant-id', 'tenantId'],
    ['--gateway-token-env', 'gatewayTokenEnv'], ['--issuer', 'issuer'], ['--audience', 'audience'],
    ['--actor-id', 'actorId'], ['--project-id', 'projectId'], ['--receipt-dir', 'receiptDir'],
    ['--readiness-timeout-seconds', 'readinessTimeoutSeconds'],
  ]);
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--help' || arg === '-h') options.help = true;
    else if (arg === '--execute') options.execute = true;
    else {
      const separator = arg.indexOf('=');
      const name = separator < 0 ? arg : arg.slice(0, separator);
      if (!valued.has(name)) wholeReleaseFail(`unknown argument "${arg}".`);
      const value = separator < 0 ? argv[++index] : arg.slice(separator + 1);
      if (typeof value !== 'string' || value.length === 0 || value.startsWith('--')) {
        wholeReleaseFail(`${name} requires a value.`);
      }
      options[valued.get(name)] = value;
    }
  }
  if (options.help) return options;
  for (const [key, flag] of [
    ['publicationPath', '--publication'], ['subscriptionId', '--subscription-id'],
    ['resourceGroup', '--resource-group'], ['clusterName', '--cluster'],
  ]) {
    if (typeof options[key] !== 'string' || options[key].trim() !== options[key] || !options[key]) {
      wholeReleaseFail(`${flag} is required and must not contain surrounding whitespace.`);
    }
  }
  options.readinessTimeoutSeconds = Number(options.readinessTimeoutSeconds);
  if (!Number.isInteger(options.readinessTimeoutSeconds) ||
      options.readinessTimeoutSeconds < 60 || options.readinessTimeoutSeconds > 3600) {
    wholeReleaseFail('--readiness-timeout-seconds must be an integer from 60 to 3600.');
  }
  for (const key of [
    'projectsConfigMigrationReceiptPath', 'gatewayUrl', 'tenantId', 'gatewayTokenEnv',
    'issuer', 'audience', 'actorId', 'projectId',
  ]) {
    const value = options[key];
    if (value !== undefined && (typeof value !== 'string' || !value || value.trim() !== value)) {
      wholeReleaseFail(`--${key.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)} must not be empty or contain surrounding whitespace.`);
    }
  }
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(options.gatewayTokenEnv)) {
    wholeReleaseFail('--gateway-token-env must be a valid environment variable name.');
  }
  return options;
}

function gitOutput(exec, root, args, action) {
  const result = exec('git', args, { cwd: root, check: false });
  if (result.status !== 0) wholeReleaseFail(`${action}: ${(result.stderr ?? '').trim()}`);
  return result.stdout;
}

function requireAncestor(exec, root, sourceSha, target, description) {
  const result = exec('git', ['merge-base', '--is-ancestor', sourceSha, target], {
    cwd: root, check: false,
  });
  if (result.status !== 0) wholeReleaseFail(`${description} ${sourceSha} is not an ancestor of ${target}.`);
}

function parseKubernetesObjects(result) {
  if (result.status !== 0) wholeReleaseFail(`could not read ReplicaSets and Pods: ${(result.stderr ?? '').trim()}`);
  const list = parseJson(result.stdout, 'Kubernetes ReplicaSet and Pod list');
  if (!list || list.kind !== 'List' || !Array.isArray(list.items) || list.items.length === 0) {
    wholeReleaseFail('Kubernetes ReplicaSet and Pod inventory is not a nonempty List.');
  }
  if (list.items.some(item => item?.metadata?.namespace !== NAMESPACE ||
      !((item.apiVersion === 'apps/v1' && item.kind === 'ReplicaSet') ||
        (item.apiVersion === 'v1' && item.kind === 'Pod')))) {
    wholeReleaseFail('ReplicaSet and Pod inventory contains an unsupported object or namespace.');
  }
  return list.items;
}

function requireNoMaintenanceLock(kubectl) {
  // Schema checks are only current while db:maintenance is not changing migration state.
  const result = kubectl(['get', 'lease', LOCK_NAME, '-o', 'json']);
  if (result.status === 0) {
    const lease = parseJson(result.stdout, 'database maintenance lock');
    wholeReleaseFail(`db:maintenance lock is held by ${lease.spec?.holderIdentity ?? 'an unknown run'}.`);
  }
  // NotFound is the only failure that proves no maintenance lease is present.
  if (!/NotFound/i.test(result.stderr ?? '')) {
    wholeReleaseFail(`could not verify the db:maintenance lock: ${(result.stderr ?? '').trim()}`);
  }
}

function readGatewayRuntimeConfig(kubectl) {
  const result = kubectl(['get', 'configmap', 'gateway-runtime-config', '-o', 'json']);
  if (result.status !== 0) {
    wholeReleaseFail(`could not read gateway-runtime-config: ${(result.stderr ?? '').trim()}`);
  }
  const configMap = parseJson(result.stdout, 'Gateway runtime ConfigMap');
  if (configMap.kind !== 'ConfigMap' || configMap.metadata?.name !== 'gateway-runtime-config'
    || configMap.metadata?.namespace !== NAMESPACE || !configMap.data || typeof configMap.data !== 'object') {
    wholeReleaseFail('gateway-runtime-config is not the expected ConfigMap in the agentweaver namespace.');
  }
  return configMap.data;
}

function parsePodList(result) {
  if (result.status !== 0) wholeReleaseFail(`could not read Pods: ${(result.stderr ?? '').trim()}`);
  const list = parseJson(result.stdout, 'Kubernetes Pod list');
  if (!list || list.kind !== 'List' || !Array.isArray(list.items) ||
      list.items.some(pod => pod?.apiVersion !== 'v1' || pod.kind !== 'Pod' ||
        pod.metadata?.namespace !== NAMESPACE)) {
    wholeReleaseFail('Kubernetes Pod inventory is not a namespace-scoped Pod List.');
  }
  return list.items;
}

function podReferencesConfigMap(pod, name) {
  const containers = [
    ...(pod.spec?.initContainers ?? []), ...(pod.spec?.containers ?? []),
    ...(pod.spec?.ephemeralContainers ?? []),
  ];
  return (pod.spec?.volumes ?? []).some(volume =>
    volume.configMap?.name === name || (volume.projected?.sources ?? [])
      .some(source => source.configMap?.name === name)) ||
    containers.some(container =>
      (container.envFrom ?? []).some(source => source.configMapRef?.name === name) ||
      (container.env ?? []).some(entry => entry.valueFrom?.configMapKeyRef?.name === name));
}

function readSourceRuntimeConfigMaps(root, deps) {
  const rendered = deps.exec('kubectl', [
    'kustomize', path.join(root, 'deploy', 'k8s'),
  ], { cwd: root, check: false });
  if (rendered.status !== 0) {
    wholeReleaseFail(`could not render source runtime ConfigMaps: ${(rendered.stderr ?? '').trim()}`);
  }
  return parseKustomizeResources(rendered.stdout).items;
}

function immutableConfigMapCommand(baseArgs, deps, root, args, settings = {}) {
  const { projectJson, preserveProjectedJson: _preserveProjectedJson, ...execSettings } = settings;
  const result = deps.exec('kubectl', [...baseArgs, ...args], {
    cwd: root, check: false, ...execSettings,
  });
  if (result.status !== 0) {
    wholeReleaseFail(`kubectl ${args.join(' ')} failed: ${(result.stderr ?? '').trim()}`);
  }
  if (typeof projectJson === 'function') {
    const value = parseJson(result.stdout, 'Kubernetes ConfigMap response');
    return JSON.stringify(projectJson(value));
  }
  return (result.stdout ?? '').trim();
}

function plannedConfigMapConsumers(replacements, pods, before) {
  const deployments = new Map();
  for (const resource of replacements) {
    const name = resource.metadata.name;
    const consumers = IMMUTABLE_RUNTIME_CONFIG_MAP_ROLLOUT_DEPLOYMENTS.get(name) ?? [];
    const activePods = pods.filter(pod => podReferencesConfigMap(pod, name));
    // Refuse any consumer outside the known rollout set because this command cannot safely restart it.
    if (consumers.length === 0 && activePods.length > 0) {
      wholeReleaseFail(`ConfigMap/${name} has active Pod consumers outside the whole-release rollout set; ` +
        'stop those consumers through their owning lifecycle before retrying.');
    }
    for (const pod of activePods) {
      const deploymentName = pod.metadata.labels?.['app.kubernetes.io/name'];
      if (!consumers.includes(deploymentName)) {
        wholeReleaseFail(`Pod ${pod.metadata.name} references ConfigMap/${name} but is not owned by its approved Deployment.`);
      }
    }
    for (const deploymentName of consumers) {
      const deployment = before.byName.get(deploymentName);
      const replicas = deployment?.spec?.replicas;
      if (!deployment || !Number.isSafeInteger(replicas) || replicas < 1 ||
          typeof deployment.metadata?.resourceVersion !== 'string' || !deployment.metadata.resourceVersion) {
        wholeReleaseFail(`Deployment ${deploymentName} cannot be safely drained for ConfigMap/${name}.`);
      }
      deployments.set(deploymentName, replicas);
    }
  }
  return deployments;
}

async function drainConfigMapConsumers(replacements, consumers, before, kubectl, options, deps, drained) {
  // Stop every approved Deployment before waiting, so no old Pod can keep using cached ConfigMap data.
  for (const [name, replicas] of consumers) {
    const deployment = before.byName.get(name);
    const patch = [
      { op: 'test', path: '/metadata/resourceVersion', value: deployment.metadata.resourceVersion },
      { op: 'test', path: '/spec/replicas', value: replicas },
      { op: 'replace', path: '/spec/replicas', value: 0 },
    ];
    const result = kubectl(['patch', 'deployment', name, '--type=json', '--patch', JSON.stringify(patch)]);
    if (result.status !== 0) {
      wholeReleaseFail(`could not stop Deployment/${name} before replacing its immutable ConfigMap: ${(result.stderr ?? '').trim()}`);
    }
    drained.set(name, replicas);
  }

  await waitForDeploymentPods(
    timeout => parsePodList(kubectl(['get', 'pods', '--output', 'json'], { timeout })),
    pods => {
      const remaining = replacements.find(resource =>
        pods.some(pod => podReferencesConfigMap(pod, resource.metadata.name)));
      return remaining ? `Pods still reference ConfigMap/${remaining.metadata.name}` : null;
    },
    {
      label: 'immutable ConfigMap consumer', timeoutMs: options.readinessTimeoutSeconds * 1000,
      intervalMs: 2_000, now: deps.nowMs, sleep: deps.sleep,
    },
  );
}

function restoreConfigMapConsumer(name, replicas, service, kubectl, options, onReplicasRestored) {
  const readbackResult = kubectl(['get', 'deployment', name, '--output', 'json']);
  if (readbackResult.status !== 0) {
    wholeReleaseFail(`could not read drained Deployment/${name}: ${(readbackResult.stderr ?? '').trim()}`);
  }
  const current = parseJson(readbackResult.stdout, `Deployment/${name} after ConfigMap replacement`);
  const original = service.deploymentObject;
  // Restore only the Deployment generation this run drained; never overwrite another operator's edits.
  if (current.kind !== 'Deployment' || current.metadata?.namespace !== NAMESPACE ||
      current.metadata?.uid !== original.metadata.uid || current.spec?.replicas !== 0 ||
      !isDeepStrictEqual(current.spec?.template, original.spec?.template)) {
    wholeReleaseFail(`Deployment/${name} changed while its ConfigMap consumers were drained.`);
  }
  const patch = [
    { op: 'test', path: '/metadata/resourceVersion', value: current.metadata.resourceVersion },
    { op: 'test', path: '/spec/replicas', value: 0 },
  ];
  if (service.changed) {
    patch.push(
      { op: 'test', path: `/spec/template/spec/containers/${service.containerIndex}/image`, value: service.currentImage },
      { op: 'replace', path: `/spec/template/spec/containers/${service.containerIndex}/image`, value: service.image },
    );
  }
  patch.push({ op: 'replace', path: '/spec/replicas', value: replicas });
  const updated = kubectl(['patch', 'deployment', name, '--type=json', '--patch', JSON.stringify(patch)]);
  if (updated.status !== 0) wholeReleaseFail(`could not restore Deployment/${name}: ${(updated.stderr ?? '').trim()}`);
  onReplicasRestored?.();
  const rollout = kubectl(['rollout', 'status', `deployment/${name}`,
    `--timeout=${options.readinessTimeoutSeconds}s`]);
  if (rollout.status !== 0) {
    wholeReleaseFail(`${name} did not become ready after its ConfigMap replacement: ${(rollout.stderr ?? '').trim()}`);
  }
}

function verifyPods(items, services) {
  const replicaSets = items.filter(item => item.kind === 'ReplicaSet');
  const pods = items.filter(item => item.kind === 'Pod');
  return services.map(service => {
    const ownedReplicaSets = replicaSets.filter(item =>
      item.metadata?.ownerReferences?.some(owner =>
        owner.kind === 'Deployment' && owner.name === service.deployment &&
        owner.uid === service.deploymentObject.metadata.uid));
    const rsUids = new Set(ownedReplicaSets.map(item => item.metadata?.uid).filter(Boolean));
    // Follow ReplicaSet UIDs so a Pod with a coincidental label cannot satisfy this Deployment's check.
    const ownedPods = pods.filter(item =>
      item.metadata?.ownerReferences?.some(owner => owner.kind === 'ReplicaSet' && rsUids.has(owner.uid)));
    if (ownedPods.length !== service.replicas) {
      wholeReleaseFail(`${service.deployment} has ${ownedPods.length} Pods; expected ${service.replicas}.`);
    }
    const podReceipts = ownedPods.map(pod => {
      const status = (pod.status?.containerStatuses ?? []).filter(item => item.name === service.container);
      const container = (pod.spec?.containers ?? []).filter(item => item.name === service.container);
      const imageId = status[0]?.imageID;
      const expectedRepository = service.image.slice(0, service.image.lastIndexOf('@'));
      const match = /^(?:docker-pullable:\/\/)?(.+)@(sha256:[a-f0-9]{64})$/.exec(imageId ?? '');
      // A counted Pod must be live, Running, Ready, on the target image, and report a verified registry digest.
      if (pod.metadata?.deletionTimestamp || pod.status?.phase !== 'Running' ||
          container.length !== 1 || container[0].image !== service.image ||
          status.length !== 1 || status[0].ready !== true ||
          !match || match[1] !== expectedRepository || !service.acceptedDigests.has(match[2])) {
        wholeReleaseFail(`${service.deployment} Pod ${pod.metadata?.name ?? '(unnamed)'} is not a ready target Pod.`);
      }
      return { name: pod.metadata.name, imageID: imageId, digest: match[2], ready: true };
    }).sort((left, right) => left.name.localeCompare(right.name));
    return { deployment: service.deployment, replicas: service.replicas, pods: podReceipts };
  });
}

/**
 * Execute one complete reconciliation inside the short-lived user kubeconfig.
 * All mutation commands are JSON patches against the readback resource version.
 */
export async function runWholeRelease(options, overrides = {}) {
  const root = path.resolve(overrides.root ?? repositoryRoot);
  const deps = {
    exec: run, readFile: readFileSync, verifyImage: verifyServiceRegistryImage,
    withKubeconfig: withP0UserKubeconfig, writeReceipt: writeWholeReleaseReceipt,
    now: () => new Date(), newRunId: randomUUID, log: message => console.log(message),
    nowMs: Date.now,
    sleep: milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)),
    readRuntimeConfigMaps: (sourceRoot) => readSourceRuntimeConfigMaps(sourceRoot, deps),
    fetchImpl: globalThis.fetch, prepareSmoke: prepareGatewaySmokeContext,
    smoke: runGatewaySmokeChecks, ...overrides,
  };
  const runId = deps.newRunId();
  const receiptPath = path.resolve(options.receiptDir, `whole-release-${runId}.json`);
  const receipt = {
    schemaVersion: 1, runId, mode: options.execute ? 'execute' : 'dry-run',
    status: 'partial', result: 'running', namespace: NAMESPACE,
    publicationPath: options.publicationPath, receiptPath,
    startedAt: deps.now().toISOString(), publicationSourceSha: null,
    comparison: [], schema: { status: 'not-run', checks: [] },
    deployments: { before: [], plannedAfter: [], after: [] },
    migrations: { status: 'not-run' },
    configMaps: { status: 'not-run', replacements: [] },
    readiness: { status: 'not-run' }, pods: { status: 'not-run', services: [] },
    smoke: { status: 'not-run', checks: [] }, installation: { status: 'partial' },
  };
  let primaryError;
  let executionStarted = false;
  let before;
  let liveInventory;
  try {
    if (!options.publicationPath || !options.subscriptionId || !options.resourceGroup ||
        !options.clusterName) wholeReleaseFail('parsed publication and P0 target options are required.');
    if (gitOutput(deps.exec, root, ['status', '--porcelain', '--untracked-files=all'], 'Git status').trim()) {
      wholeReleaseFail('the source checkout must be clean.');
    }
    const head = gitOutput(deps.exec, root, ['rev-parse', 'HEAD'], 'Git HEAD').trim();
    if (!SHA.test(head)) wholeReleaseFail('the deployment-tool checkout HEAD is not a full Git commit SHA.');
    // Refresh and bind both the deployment tool and publication source to the approved v1 line.
    const fetch = deps.exec('git', ['fetch', '--quiet', 'origin', 'v1'], { cwd: root, check: false });
    if (fetch.status !== 0) wholeReleaseFail('could not refresh origin/v1 before source verification.');
    if (deps.exec('git', ['merge-base', '--is-ancestor', head, 'origin/v1'], { cwd: root, check: false }).status !== 0) {
      wholeReleaseFail(`deployment-tool source ${head} is not an ancestor of origin/v1.`);
    }
    const publication = parseJson(deps.readFile(path.resolve(root, options.publicationPath), 'utf8'),
      'publication receipt');
    receipt.publicationSourceSha = publication.sourceSha ?? null;
    if (!SHA.test(publication.sourceSha ?? '')) wholeReleaseFail('publication receipt has no full source commit SHA.');
    if (deps.exec('git', ['merge-base', '--is-ancestor', publication.sourceSha, 'origin/v1'],
      { cwd: root, check: false }).status !== 0) {
      wholeReleaseFail(`publication source ${publication.sourceSha} is not an ancestor of origin/v1.`);
    }
    const manifest = parseJson(gitOutput(deps.exec, root, ['show', `${publication.sourceSha}:releases/foundation.json`],
      'Could not read source-bound release manifest'), 'source-bound release manifest');
    const previous = options.previousPublicationPath
      ? parseJson(deps.readFile(path.resolve(root, options.previousPublicationPath), 'utf8'),
        'previous publication receipt') : undefined;
    const resolved = resolveWholeReleaseImages(publication, manifest, previous, {
      git: (...args) => gitOutput(deps.exec, root, args, `Git ${args.join(' ')}`),
      exec: deps.exec, root,
    });
    const images = await verifyWholeReleaseImages(resolved, deps.verifyImage);
    const projectsConfigImage = images.find(image => image.componentId === 'Agentweaver.Projects.Config');
    if (!projectsConfigImage) wholeReleaseFail('Projects & Config is missing from the verified publication images.');
    if (options.execute && !options.projectsConfigMigrationReceiptPath) {
      wholeReleaseFail('--projects-config-migration-receipt is required with --execute.');
    }
    if (options.projectsConfigMigrationReceiptPath) {
      const migration = parseJson(deps.readFile(
        path.resolve(root, options.projectsConfigMigrationReceiptPath), 'utf8',
      ), 'Projects & Config migration receipt');
      if (migration.schemaVersion !== 1 || migration.mode !== 'execute' || migration.result !== 'succeeded' ||
          !/^[a-f0-9]{12}$/.test(migration.runId ?? '') || !SHA.test(migration.sourceSha ?? '') ||
          migration.publicationSourceSha !== publication.sourceSha ||
          migration.image !== projectsConfigImage.image ||
          migration.imageSourceSha !== projectsConfigImage.imageSourceSha ||
          migration.target?.clusterName !== options.clusterName ||
          migration.target?.namespace !== NAMESPACE ||
          migration.target?.resourceGroup !== options.resourceGroup ||
          migration.target?.subscriptionId !== options.subscriptionId) {
        wholeReleaseFail('Projects & Config migration receipt does not prove a successful migration of this publication for this target.');
      }
      // The migration runner may be newer than the publication, but its source must still be on approved v1.
      requireAncestor(deps.exec, root, migration.sourceSha, 'origin/v1', 'Projects & Config migration source');
      receipt.migrations = {
        status: 'passed', runId: migration.runId, image: migration.image,
        receiptPath: path.resolve(root, options.projectsConfigMigrationReceiptPath),
      };
    } else if (!options.execute) {
      receipt.migrations = { status: 'required-before-execute', image: projectsConfigImage.image };
    }
    receipt.images = images.map(image => ({
      componentId: image.componentId, deployment: image.deployment, image: image.image,
      digest: image.digest, sourceSha: image.imageSourceSha, sourceTree: image.imageSourceTree,
      version: image.version, sourcePublicationSha: image.sourcePublicationSha,
    }));

    const action = async baseArgs => {
      const kubectl = (args, settings = {}) => deps.exec('kubectl', [
        ...baseArgs, '--namespace', NAMESPACE, ...args,
      ], { cwd: root, check: false, ...settings });
      requireNoMaintenanceLock(kubectl);
      const inventory = () => deploymentInventory(
        (args, settings) => deps.exec('kubectl', [...baseArgs, ...args], settings),
        NAMESPACE, root, wholeReleaseFail);
      liveInventory = inventory;
      before = inventory();
      const services = compareWholeReleaseState(before, images);
      receipt.comparison = wholeReleaseComparison(services);
      receipt.deployments.before = before.records;
      receipt.deployments.plannedAfter = plannedWholeReleaseInventory(before, services);
      const sourceResources = deps.readRuntimeConfigMaps(root);
      const immutableMapRun = (command, args, settings = {}) =>
        immutableConfigMapCommand(baseArgs, deps, root, args, settings);
      const replacements = findImmutableRuntimeConfigMapReplacements(
        { items: sourceResources }, NAMESPACE, { root, run: immutableMapRun, fail: wholeReleaseFail },
      );
      receipt.configMaps = {
        status: options.execute ? 'ready-to-replace' : 'planned',
        replacements: replacements.map(resource => resource.metadata.name),
        cacheDrainDeployments: [...new Set(replacements.flatMap(resource =>
          IMMUTABLE_RUNTIME_CONFIG_MAP_ROLLOUT_DEPLOYMENTS.get(resource.metadata.name) ?? []))].sort(),
        cacheExpiryWaitSeconds: replacements.length > 0
          ? Math.ceil(CONFIG_MAP_CACHE_EXPIRY_WAIT_MS / 1000) : 0,
      };
      receipt.schema.checks = await (deps.schemaCheck
        ? deps.schemaCheck({ services, kubectl, root, exec: deps.exec })
        : checkSchemaCompatibility({ services, kubectl, deps, root, exec: deps.exec }));
      receipt.schema.status = 'passed';
      if (!options.execute) {
        receipt.result = 'planned';
        deps.log('Dry run passed. The whole installation remains partial until execute verification succeeds.');
        return;
      }

      receipt.smoke = { status: 'preparing', checks: [] };
      const smokeContext = await deps.prepareSmoke({
        gatewayRuntimeConfig: readGatewayRuntimeConfig(kubectl),
        options,
        fetchImpl: deps.fetchImpl,
      });
      receipt.smoke = { status: 'not-run', checks: [] };
      executionStarted = true;
      const drainedConsumers = new Map();
      const updates = wholeReleaseUpdates(services);
      try {
        if (replacements.length > 0) {
          const pods = parsePodList(kubectl(['get', 'pods', '--output', 'json']));
          const consumers = plannedConfigMapConsumers(replacements, pods, before);
          verifyImmutableRuntimeConfigMapReplacementPermissions(replacements, NAMESPACE, {
            root,
            run: immutableMapRun,
            fail: wholeReleaseFail,
          });
          await drainConfigMapConsumers(
            replacements, consumers, before, kubectl, options, deps, drainedConsumers,
          );
          // Let each kubelet's default one-minute immutable ConfigMap cache expire before recreating the name.
          await deps.sleep(CONFIG_MAP_CACHE_EXPIRY_WAIT_MS);
          replaceImmutableRuntimeConfigMaps(replacements, NAMESPACE, {
            root,
            run: immutableMapRun,
            fail: wholeReleaseFail,
          });
          receipt.configMaps.status = 'replaced';
        }
        // Roll out identity and data services first, then Gateway, MCP, and Web.
        for (const deployment of SAFE_ROLLOUT_ORDER) {
          const service = services.find(item => item.deployment === deployment);
          const drainedReplicas = drainedConsumers.get(deployment);
          if (drainedReplicas !== undefined) {
            restoreConfigMapConsumer(deployment, drainedReplicas, service, kubectl, options,
              () => drainedConsumers.delete(deployment));
            continue;
          }
          if (!service?.changed) continue;
          const resourceVersion = service.deploymentObject.metadata?.resourceVersion;
          if (typeof resourceVersion !== 'string' || !resourceVersion) {
            wholeReleaseFail(`${deployment} Deployment has no resourceVersion for a guarded update.`);
          }
          const imagePath = `/spec/template/spec/containers/${service.containerIndex}/image`;
          const patch = [
            { op: 'test', path: '/metadata/resourceVersion', value: resourceVersion },
            { op: 'test', path: imagePath, value: service.currentImage },
            { op: 'replace', path: imagePath, value: service.image },
          ];
          const result = kubectl(['patch', 'deployment', deployment, '--type=json', '--patch', JSON.stringify(patch)]);
          if (result.status !== 0) wholeReleaseFail(`could not patch deployment/${deployment}: ${(result.stderr ?? '').trim()}`);
          const rollout = kubectl(['rollout', 'status', `deployment/${deployment}`,
            `--timeout=${options.readinessTimeoutSeconds}s`]);
          if (rollout.status !== 0) wholeReleaseFail(`${deployment} did not become ready: ${(rollout.stderr ?? '').trim()}`);
        }
      } catch (error) {
        const restoreErrors = [];
        // Restore consumers drained earlier even when another service's rollout fails first.
        for (const deployment of SAFE_ROLLOUT_ORDER) {
          const replicas = drainedConsumers.get(deployment);
          if (replicas === undefined) continue;
          try {
            restoreConfigMapConsumer(
              deployment, replicas, services.find(item => item.deployment === deployment), kubectl, options,
              () => drainedConsumers.delete(deployment),
            );
          } catch (restoreError) {
            restoreErrors.push(restoreError);
          }
        }
        if (restoreErrors.length > 0) {
          throw new AggregateError([error, ...restoreErrors],
            'Whole-release execution failed and one or more drained Deployments could not be restored and verified.');
        }
        throw error;
      }

      const after = inventory();
      receipt.deployments.after = after.records;
      receipt.readiness = { status: 'passed', services: verifyWholeReleaseReadback(
        before, after, services, updates, wholeReleaseFail),
      };
      receipt.pods = { status: 'waiting', services: [] };
      const podInventory = await waitForDeploymentPods(
        timeout => parseKubernetesObjects(
          kubectl(['get', 'replicasets,pods', '--output', 'json'], { timeout }),
        ),
        items => deploymentPodDrainReason(items, services, after, wholeReleaseFail),
        {
          label: 'whole-release', timeoutMs: deps.podDrainTimeoutMs,
          intervalMs: deps.podDrainPollIntervalMs, now: deps.nowMs, sleep: deps.sleep,
        },
      );
      receipt.pods.services = verifyPods(podInventory, services);
      receipt.pods.status = 'passed';
      receipt.smoke = { status: 'waiting', checks: [] };
      receipt.smoke.checks = await deps.smoke(smokeContext);
      receipt.smoke.status = 'passed';
      // Only all-passing readback, Pod, and Gateway checks justify a deployed receipt.
      receipt.status = 'deployed';
      receipt.installation = { status: 'deployed' };
      receipt.result = 'succeeded';
    };

    await deps.withKubeconfig({
      resourceGroup: options.resourceGroup, subscriptionId: options.subscriptionId,
      clusterName: options.clusterName,
    }, action, {
      requiredPermissions: [
        ['get', 'namespace/agentweaver'], ['get', 'leases', NAMESPACE],
        ['get', 'configmaps', NAMESPACE], ['get', 'deployments', NAMESPACE],
        ...(options.execute ? [
          ['patch', 'deployments', NAMESPACE], ['watch', 'deployments', NAMESPACE],
          ['get', 'replicasets', NAMESPACE], ['get', 'pods', NAMESPACE],
        ] : []),
      ],
      permissionContext: 'whole-release image rollout',
      execKubectl: (args, settings) => deps.exec('kubectl', args, settings),
    });
  } catch (error) {
    primaryError = error;
    // Any failed execute remains partial, even when some Deployments already reached their target.
    receipt.status = 'partial';
    receipt.installation = { status: 'partial' };
    receipt.result = 'failed';
    receipt.error = redact(error instanceof Error ? error.message : String(error));
    if (receipt.schema.status === 'not-run') receipt.schema = { status: 'failed', error: receipt.error, checks: [] };
    if (receipt.readiness.status === 'waiting') receipt.readiness = { status: 'failed', error: receipt.error };
    if (receipt.pods.status === 'waiting') receipt.pods = { status: 'failed', error: receipt.error, services: [] };
    if (['preparing', 'waiting'].includes(receipt.smoke.status)) {
      receipt.smoke = { status: 'failed', error: receipt.error, checks: [] };
    }
    if (executionStarted && before && receipt.deployments.after.length === 0) {
      try {
        // Capture the live partial state so the failed receipt is actionable without another write.
        if (!liveInventory) throw new Error('the temporary kubeconfig is no longer available');
        receipt.deployments.after = liveInventory().records;
      } catch (afterError) {
        receipt.afterReadError = redact(afterError instanceof Error ? afterError.message : String(afterError));
      }
    }
  }
  receipt.completedAt = deps.now().toISOString();
  try {
    deps.writeReceipt(receiptPath, receipt);
  } catch (writeError) {
    if (primaryError) throw new AggregateError([primaryError, writeError],
      `Whole-release rollout failed and its receipt could not be written.`);
    throw writeError;
  }
  deps.log(`Wrote whole-release receipt: ${receiptPath} (${receipt.status}).`);
  if (primaryError) throw new Error(`Whole-release rollout ${receipt.status}; receipt: ${receiptPath}; ${receipt.error}`,
    { cause: primaryError });
  return receipt;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const options = parseWholeReleaseArgs(process.argv.slice(2));
    if (options.help) console.log(WHOLE_RELEASE_USAGE);
    else {
      const receipt = await runWholeRelease(options);
      console.log(`Whole-release rollout ${receipt.status} for ${receipt.publicationSourceSha}.`);
    }
  } catch (error) {
    console.error(`Whole-release rollout failed: ${redact(error.message)}`);
    process.exitCode = 1;
  }
}
