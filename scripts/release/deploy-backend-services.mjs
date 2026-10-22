/**
 * Rolls out the six named backend images from a source-bound publication receipt.
 * The default mode is read-only and all cluster access uses a temporary P0 user kubeconfig.
 */
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { withP0UserKubeconfig } from '../azure/lib/namespace-bootstrap.mjs';
import { run, redact } from '../azure/lib/exec.mjs';
import {
  canonicalJson, deploymentInventory, deploymentPodDrainReason, imageDigest, planDeploymentInventory,
  waitForDeploymentPods,
  verifyDeploymentPreservation, verifyDeploymentReady, writeRolloutReceipt,
} from './image-rollout-common.mjs';
import { selectPublishedComponentImage } from './publication-image.mjs';
import { componentImageRepository } from './pack.mjs';
import { verifyServiceRegistryImage } from './registry.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const NAMESPACE = 'agentweaver';
const SHA = /^[a-f0-9]{40}$/;
const IMAGE_DIGEST = /^sha256:[a-f0-9]{64}$/;
const BACKEND_SERVICES = Object.freeze([
  { componentId: 'Agentweaver.Gateway', deployment: 'gateway', container: 'gateway' },
  { componentId: 'Agentweaver.Identity.Broker', deployment: 'identity-broker', container: 'identity-broker' },
  { componentId: 'Agentweaver.Mcp', deployment: 'mcp', container: 'mcp' },
  { componentId: 'Agentweaver.Environment', deployment: 'environment', container: 'environment' },
  { componentId: 'Agentweaver.Knowledge', deployment: 'knowledge', container: 'knowledge' },
  { componentId: 'Agentweaver.Projects.Config', deployment: 'projects-config', container: 'projects-config' },
]);

export const BACKEND_ROLLOUT_USAGE = `Usage: npm run release:deploy-backend-services -- --publication <publication.json> --subscription-id <id> --resource-group <name> --cluster <name> [options]

Without --execute the command verifies the publication and plans a rollout of the six fixed backend services.

  --publication <file>                Source-bound release publication receipt. Required.
  --subscription-id <id>              Azure subscription containing the P0 cluster. Required.
  --resource-group <name>             Resource group containing the P0 cluster. Required.
  --cluster <name>                    P0 AKS cluster name. Required.
  --execute                           Patch only the six selected backend container images.
  --receipt-dir <directory>           Receipt directory. Default: ~/.agentweaver/backend-image-rollouts.
  --readiness-timeout-seconds <n>     Rollout timeout. Default: 600.
  --help                              Show this help.
`;

function fail(message) {
  throw new Error(`Backend image rollout: ${message}`);
}

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function parseJson(text, name) {
  try {
    return JSON.parse(text);
  } catch (error) {
    fail(`${name} is not valid JSON: ${error.message}`);
  }
}

/** Parses only explicit target arguments so no CLI option can select another workload or namespace. */
export function parseBackendRolloutArgs(argv = []) {
  const options = {
    execute: false,
    publicationPath: undefined,
    subscriptionId: undefined,
    resourceGroup: undefined,
    clusterName: undefined,
    receiptDir: path.join(homedir(), '.agentweaver', 'backend-image-rollouts'),
    readinessTimeoutSeconds: 600,
    help: false,
  };
  const valued = new Map([
    ['--publication', 'publicationPath'],
    ['--subscription-id', 'subscriptionId'],
    ['--resource-group', 'resourceGroup'],
    ['--cluster', 'clusterName'],
    ['--receipt-dir', 'receiptDir'],
    ['--readiness-timeout-seconds', 'readinessTimeoutSeconds'],
  ]);
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === '--help' || arg === '-h') {
      options.help = true;
    } else if (arg === '--execute') {
      options.execute = true;
    } else {
      const separator = arg.indexOf('=');
      const name = separator < 0 ? arg : arg.slice(0, separator);
      if (!valued.has(name)) fail(`unknown argument "${arg}".`);
      const value = separator < 0 ? argv[++index] : arg.slice(separator + 1);
      if (typeof value !== 'string' || value.length === 0 || value.startsWith('--')) {
        fail(`${name} requires a value.`);
      }
      options[valued.get(name)] = value;
    }
  }
  if (options.help) return options;
  for (const [key, option] of [
    ['publicationPath', '--publication'],
    ['subscriptionId', '--subscription-id'],
    ['resourceGroup', '--resource-group'],
    ['clusterName', '--cluster'],
  ]) {
    if (typeof options[key] !== 'string' || options[key].trim() !== options[key] || options[key].length === 0) {
      fail(`${option} is required and must not contain surrounding whitespace.`);
    }
  }
  options.readinessTimeoutSeconds = Number(options.readinessTimeoutSeconds);
  if (!Number.isInteger(options.readinessTimeoutSeconds) ||
      options.readinessTimeoutSeconds < 60 || options.readinessTimeoutSeconds > 3600) {
    fail('--readiness-timeout-seconds must be an integer from 60 to 3600.');
  }
  return options;
}

function gitOutput(exec, root, args, action) {
  const result = exec('git', args, { cwd: root, check: false });
  if (result.status !== 0) fail(`${action}${result.stderr ? `: ${result.stderr.trim()}` : '.'}`);
  return result.stdout;
}

function requireAncestor(exec, root, sourceSha, target, description) {
  // An image source is eligible only when its exact commit belongs to current v1 history.
  const result = exec('git', ['merge-base', '--is-ancestor', sourceSha, target], { cwd: root, check: false });
  if (result.status !== 0) fail(`${description} ${sourceSha} is not an ancestor of ${target}.`);
}

function validateBackendDeployment(inventory, service, image) {
  const deployment = inventory.byName.get(service.deployment);
  // Require one explicitly labeled workload so a similarly named Deployment cannot be selected by accident.
  const selected = [...inventory.byName.values()].filter(item =>
    item.metadata?.labels?.['agentweaver.io/service'] === service.deployment ||
    item.spec?.template?.metadata?.labels?.['agentweaver.io/workload'] === service.deployment);
  const selector = { 'app.kubernetes.io/name': service.deployment };
  if (!deployment || selected.length !== 1 || selected[0] !== deployment ||
      deployment.metadata?.labels?.['app.kubernetes.io/name'] !== service.deployment ||
      deployment.metadata?.labels?.['agentweaver.io/service'] !== service.deployment ||
      canonicalJson(deployment.spec?.selector?.matchLabels) !== canonicalJson(selector) ||
      deployment.spec?.template?.metadata?.labels?.['app.kubernetes.io/name'] !== service.deployment ||
      deployment.spec?.template?.metadata?.labels?.['agentweaver.io/service'] !== service.deployment ||
      deployment.spec?.template?.metadata?.labels?.['agentweaver.io/workload'] !== service.deployment) {
    fail(`current ${service.deployment} Deployment is missing or its identity and selector do not match the reviewed workload.`);
  }
  const containers = deployment.spec.template.spec.containers;
  const matches = containers.map((container, index) => ({ container, index }))
    .filter(entry => entry.container.name === service.container);
  if (matches.length !== 1 || !Number.isSafeInteger(deployment.spec.replicas ?? 1) ||
      (deployment.spec.replicas ?? 1) < 1) {
    fail(`current ${service.deployment} Deployment must have one ${service.container} container and at least one replica.`);
  }
  const { container, index } = matches[0];
  const repository = `ghcr.io/sabbour/${componentImageRepository(service.componentId)}`;
  // The current image must already belong to this service's digest-pinned repository before it can be replaced.
  if (typeof deployment.metadata.uid !== 'string' || deployment.metadata.uid.length === 0 ||
      typeof container.image !== 'string' || !container.image.startsWith(`${repository}@`)) {
    fail(`current ${service.deployment} Deployment has no stable identity or digest-pinned service image.`);
  }
  const currentDigest = imageDigest(container.image, `${service.deployment} container ${service.container}`, fail);
  if ((deployment.spec.template.spec.initContainers ?? []).some(entry => entry.name === service.container)) {
    fail(`the ${service.deployment} workload has an ambiguous init container named ${service.container}.`);
  }
  return {
    ...service,
    deploymentObject: deployment,
    containerIndex: index,
    currentImage: container.image,
    currentDigest,
    replicas: deployment.spec.replicas ?? 1,
    targetImage: image.image,
    targetDigest: image.digest,
    acceptedDigests: image.acceptedDigests,
    changed: container.image !== image.image,
  };
}

function parseKubernetesObjects(result, fail) {
  // kubectl returns a generic List here; accept only namespaced ReplicaSets and Pods for ownership checks.
  if (result.status !== 0) fail(`could not read ReplicaSets and Pods: ${result.stderr?.trim() ?? ''}`);
  const list = parseJson(result.stdout, 'Kubernetes ReplicaSet and Pod list');
  if (!isRecord(list) || list.kind !== 'List' || !Array.isArray(list.items) || list.items.length === 0) {
    fail('Kubernetes ReplicaSet and Pod inventory is not a nonempty List.');
  }
  for (const item of list.items) {
    if (!isRecord(item) || item.metadata?.namespace !== NAMESPACE ||
        !((item.apiVersion === 'apps/v1' && item.kind === 'ReplicaSet') ||
          (item.apiVersion === 'v1' && item.kind === 'Pod'))) {
      fail('ReplicaSet and Pod inventory contains an unsupported object or namespace.');
    }
  }
  return list.items;
}

function matchesSelector(labels, selector) {
  return isRecord(labels) && isRecord(selector) && Object.keys(selector).length > 0 &&
    Object.entries(selector).every(([name, value]) => labels[name] === value);
}

function verifyBackendPods(items, services, after, fail) {
  const replicaSets = items.filter(item => item.kind === 'ReplicaSet');
  const pods = items.filter(item => item.kind === 'Pod');
  const serviceReceipts = [];
  for (const service of services) {
    const deployment = after.byName.get(service.deployment);
    const ownedReplicaSets = replicaSets.filter(replicaSet =>
      Array.isArray(replicaSet.metadata?.ownerReferences) &&
      replicaSet.metadata.ownerReferences.some(owner => owner.kind === 'Deployment' &&
        owner.name === service.deployment && owner.uid === deployment.metadata.uid));
    const replicaSetUids = new Set(ownedReplicaSets.map(replicaSet => replicaSet.metadata.uid));
    if (ownedReplicaSets.length === 0 || replicaSetUids.size !== ownedReplicaSets.length ||
        replicaSetUids.has(undefined)) {
      fail(`${service.deployment} has no uniquely identified Deployment-owned ReplicaSets.`);
    }

    // Follow Pod -> ReplicaSet -> Deployment UIDs instead of trusting labels another workload could share.
    const ownedPods = pods.filter(pod =>
      Array.isArray(pod.metadata?.ownerReferences) && pod.metadata.ownerReferences.some(owner =>
        owner.kind === 'ReplicaSet' && replicaSetUids.has(owner.uid)));
    // Require the settled inventory to contain exactly the requested replicas; extras and gaps both fail closed.
    if (ownedPods.length !== service.replicas) {
      fail(`${service.deployment} has ${ownedPods.length} active Pods; expected ${service.replicas}.`);
    }
    const selector = deployment.spec.selector.matchLabels;
    const podReceipts = ownedPods.map(pod => {
      // A counted Pod must be live, Running, selected by this Deployment, and ready before its image is checked.
      if (!matchesSelector(pod.metadata.labels, selector) || pod.metadata.deletionTimestamp ||
          pod.status?.phase !== 'Running') {
        fail(`${service.deployment} Pod ${pod.metadata.name ?? '(unnamed)'} is not an active selected Pod.`);
      }
      const containers = pod.spec?.containers;
      const containerMatches = Array.isArray(containers)
        ? containers.filter(container => container.name === service.container) : [];
      const statuses = pod.status?.containerStatuses;
      const statusMatches = Array.isArray(statuses)
        ? statuses.filter(status => status.name === service.container) : [];
      if (containerMatches.length !== 1 || containerMatches[0].image !== service.targetImage ||
          statusMatches.length !== 1 || statusMatches[0].ready !== true) {
        fail(`${service.deployment} Pod ${pod.metadata.name} is not running the ready target container.`);
      }
      const imageID = statusMatches[0].imageID;
      const imageIdMatch = /^(?:docker-pullable:\/\/)?(.+)@(sha256:[a-f0-9]{64})$/.exec(imageID ?? '');
      const expectedRepository = service.targetImage.slice(0, service.targetImage.lastIndexOf('@'));
      // A platform-specific manifest digest is accepted only because the registry verifier proved it.
      if (!imageIdMatch || imageIdMatch[1] !== expectedRepository ||
          !service.acceptedDigests.has(imageIdMatch[2])) {
        fail(`${service.deployment} Pod ${pod.metadata.name} imageID does not match its verified publication image.`);
      }
      return {
        name: pod.metadata.name,
        uid: pod.metadata.uid ?? null,
        container: service.container,
        imageID,
        digest: imageIdMatch[2],
        ready: true,
      };
    }).sort((left, right) => left.name.localeCompare(right.name));
    serviceReceipts.push({ deployment: service.deployment, replicas: service.replicas, pods: podReceipts });
  }
  return serviceReceipts;
}

function requiredPermissions(execute) {
  // Planning is read-only; execution adds only the patch, watch, and Pod/ReplicaSet reads it needs.
  return [
    ['get', 'namespace/agentweaver'],
    ['get', 'deployments', NAMESPACE],
    ...(execute ? [
      ['patch', 'deployments', NAMESPACE],
      ['watch', 'deployments', NAMESPACE],
      ['get', 'replicasets', NAMESPACE],
      ['get', 'pods', NAMESPACE],
    ] : []),
  ];
}

/**
 * Verifies all six receipt images before reading the cluster, then plans or applies guarded image patches.
 * The temporary user kubeconfig is scoped to this operation and is cleaned before the receipt is written.
 */
export async function runBackendImageRollout(options, overrides = {}) {
  const root = path.resolve(overrides.root ?? repositoryRoot);
  const deps = {
    exec: run,
    readFile: readFileSync,
    verifyImage: verifyServiceRegistryImage,
    withKubeconfig: withP0UserKubeconfig,
    writeReceipt: writeRolloutReceipt,
    now: () => new Date(),
    newRunId: randomUUID,
    log: message => console.log(message),
    ...overrides,
  };
  const runId = deps.newRunId();
  const receiptPath = path.resolve(root, options.receiptDir, `backend-image-rollout-${runId}.json`);
  const receipt = {
    schemaVersion: 1,
    runId,
    mode: options.execute ? 'execute' : 'dry-run',
    scope: BACKEND_SERVICES.map(service => service.componentId),
    target: {
      subscriptionId: options.subscriptionId,
      resourceGroup: options.resourceGroup,
      clusterName: options.clusterName,
      namespace: NAMESPACE,
    },
    publicationPath: options.publicationPath,
    receiptPath,
    startedAt: deps.now().toISOString(),
    publicationSourceSha: null,
    images: [],
    changed: false,
    deployments: { before: [], plannedAfter: [], after: [] },
    readiness: { status: 'not-run' },
    pods: { status: 'not-run', services: [] },
    installation: { status: 'partial' },
    result: 'running',
  };
  let primaryError;
  let executionStarted = false;
  let before;

  const git = (...args) => gitOutput(deps.exec, root, args, `Git ${args.join(' ')}`);

  try {
    if (!options.publicationPath || !options.subscriptionId || !options.resourceGroup ||
        !options.clusterName || !Number.isInteger(options.readinessTimeoutSeconds)) {
      fail('parsed publication, Azure target, and readiness timeout options are required.');
    }
    const status = git('status', '--porcelain', '--untracked-files=all');
    if (status.trim().length > 0) fail('the source checkout must be clean.');
    const head = git('rev-parse', 'HEAD').trim();
    if (!SHA.test(head)) fail('the deployment-tool checkout HEAD is not a full Git commit SHA.');
    // Refresh v1 before checking that both this tool and the publication source are on its history.
    const fetch = deps.exec('git', ['fetch', '--quiet', 'origin', 'v1'], { cwd: root, check: false });
    if (fetch.status !== 0) fail('could not refresh origin/v1 before source verification.');
    requireAncestor(deps.exec, root, head, 'origin/v1', 'deployment-tool source');

    const publication = parseJson(
      deps.readFile(path.resolve(root, options.publicationPath), 'utf8'), 'publication receipt',
    );
    receipt.publicationSourceSha = publication.sourceSha ?? null;
    if (!SHA.test(publication.sourceSha ?? '')) fail('publication receipt has no full source commit SHA.');
    requireAncestor(deps.exec, root, publication.sourceSha, 'origin/v1', 'publication source');
    const manifest = parseJson(git('show', `${publication.sourceSha}:releases/foundation.json`),
      'source-bound release manifest');

    const images = [];
    for (const service of BACKEND_SERVICES) {
      const selected = selectPublishedComponentImage(publication, manifest, service.componentId, {
        git, exec: deps.exec, root, label: service.deployment,
      });
      requireAncestor(deps.exec, root, selected.imageSourceSha, 'origin/v1',
        `${service.deployment} image source`);
      // Registry metadata must independently match the receipt's source, version, and accepted digest.
      const verified = await deps.verifyImage(selected.image);
      if (!verified || !IMAGE_DIGEST.test(verified.digest ?? '') ||
          !(verified.acceptedDigests instanceof Set) || !verified.acceptedDigests.has(selected.digest) ||
          verified.sourceSha !== selected.imageSourceSha || verified.sourceTree !== selected.imageSourceTree ||
          verified.version !== selected.version) {
        fail(`${service.deployment} digest, OCI revision, source tree, or version differs from its publication receipt.`);
      }
      images.push({ ...service, ...selected, acceptedDigests: verified.acceptedDigests });
    }
    receipt.images = images.map(image => ({
      componentId: image.componentId,
      deployment: image.deployment,
      image: image.image,
      digest: image.digest,
      sourceSha: image.imageSourceSha,
      sourceTree: image.imageSourceTree,
      version: image.version,
    }));

    const action = async baseArgs => {
      const kubectl = (args, settings = {}) => deps.exec('kubectl', [
        ...baseArgs, '--namespace', NAMESPACE, ...args,
      ], { cwd: root, check: false, ...settings });
      const inventory = () => deploymentInventory(
        (args, settings) => deps.exec('kubectl', [...baseArgs, ...args], settings),
        NAMESPACE, root, fail,
      );
      try {
        before = inventory();
        const services = images.map(image => validateBackendDeployment(before, image, image));
        const updates = new Map(services.map(service => [service.deployment, [{
          containerIndex: service.containerIndex,
          image: service.targetImage,
        }]]));
        receipt.deployments.before = before.records;
        receipt.deployments.plannedAfter = planDeploymentInventory(before, updates, NAMESPACE, fail).records;
        receipt.changed = services.some(service => service.changed);
        receipt.images = receipt.images.map(image => ({
          ...image,
          changed: services.find(service => service.deployment === image.deployment).changed,
        }));

        if (!options.execute) {
          // Keep dry run read-only after validating every publication image and live Deployment.
          receipt.result = 'planned';
          deps.log(`Dry run passed. Only the six named backend Deployment images would change in namespace ${NAMESPACE}.`);
          return;
        }

        executionStarted = true;
        // Patch only planned images first; then wait for every changed Deployment in the readiness pass.
        for (const service of services) {
          if (!service.changed) continue;
          const resourceVersion = service.deploymentObject.metadata?.resourceVersion;
          if (typeof resourceVersion !== 'string' || resourceVersion.length === 0) {
            fail(`${service.deployment} Deployment has no resourceVersion for a guarded update.`);
          }
          const imagePath = `/spec/template/spec/containers/${service.containerIndex}/image`;
          // The JSON Patch tests both the snapshot version and prior image before changing one field.
          const patch = [
            { op: 'test', path: '/metadata/resourceVersion', value: resourceVersion },
            { op: 'test', path: imagePath, value: service.currentImage },
            { op: 'replace', path: imagePath, value: service.targetImage },
          ];
          const updated = kubectl([
            'patch', 'deployment', service.deployment, '--type=json', '--patch', JSON.stringify(patch),
          ]);
          if (updated.status !== 0) {
            fail(`could not update only deployment/${service.deployment}'s ${service.container} image: ${updated.stderr.trim()}`);
          }
        }

        receipt.readiness = {
          status: 'waiting',
          timeoutSeconds: options.readinessTimeoutSeconds,
          services: [],
        };
        for (const service of services) {
          const rollout = kubectl([
            'rollout', 'status', `deployment/${service.deployment}`,
            `--timeout=${options.readinessTimeoutSeconds}s`,
          ]);
          if (rollout.status !== 0) fail(`${service.deployment} Deployment did not become ready: ${rollout.stderr.trim()}`);
        }

        const after = inventory();
        receipt.deployments.after = after.records;
        // Permit only the controller's revision annotation to vary during image rollouts.
        verifyDeploymentPreservation(before, after, updates, {
          namespace: NAMESPACE,
          label: 'backend service',
          ignoredAnnotations: new Map(services.map(service => [
            service.deployment, ['deployment.kubernetes.io/revision'],
          ])),
          fail,
        });
        const readiness = services.map(service => {
          const current = after.byName.get(service.deployment);
          const currentContainer = current.spec.template.spec.containers[service.containerIndex];
          if (currentContainer?.image !== service.targetImage) {
            fail(`${service.deployment} Deployment readback does not match the publication digest.`);
          }
          return {
            deployment: service.deployment,
            ...verifyDeploymentReady(current, service.replicas, service.deployment, fail),
          };
        });
        receipt.readiness = {
          status: 'passed',
          timeoutSeconds: options.readinessTimeoutSeconds,
          services: readiness,
        };

        receipt.pods = { status: 'waiting', services: [] };
        const podObjects = await waitForDeploymentPods(
          timeout => parseKubernetesObjects(
            kubectl(['get', 'replicasets,pods', '--output', 'json'], { timeout }), fail,
          ),
          items => deploymentPodDrainReason(items, services, after, fail),
          {
            label: 'backend-service', timeoutMs: deps.podDrainTimeoutMs,
            intervalMs: deps.podDrainPollIntervalMs, now: deps.nowMs, sleep: deps.sleep,
          },
        );
        receipt.pods = {
          status: 'passed',
          services: verifyBackendPods(podObjects, services, after, fail),
        };
        receipt.result = 'succeeded';
      } catch (error) {
        // Preserve a failed receipt even when an earlier patch in this action already changed the cluster.
        if (receipt.readiness.status === 'waiting') {
          receipt.readiness = {
            status: 'failed',
            timeoutSeconds: options.readinessTimeoutSeconds,
            error: redact(error instanceof Error ? error.message : String(error)),
            services: receipt.readiness.services,
          };
        }
        if (receipt.pods.status === 'waiting') {
          receipt.pods.status = 'failed';
          receipt.pods.error = redact(error instanceof Error ? error.message : String(error));
        }
        if (executionStarted && before && receipt.deployments.after.length === 0) {
          try {
            receipt.deployments.after = inventory().records;
          } catch (afterError) {
            receipt.afterReadError = redact(afterError instanceof Error ? afterError.message : String(afterError));
          }
        }
        throw error;
      }
    };

    await deps.withKubeconfig({
      resourceGroup: options.resourceGroup,
      subscriptionId: options.subscriptionId,
      clusterName: options.clusterName,
    }, action, {
      requiredPermissions: requiredPermissions(options.execute),
      permissionContext: 'backend service image rollout',
      execKubectl: (args, settings) => deps.exec('kubectl', args, settings),
    });
  } catch (error) {
    primaryError = error;
    receipt.result = 'failed';
    receipt.error = redact(error instanceof Error ? error.message : String(error));
  }

  // This command covers six images only, so it must never certify the whole installation as deployed.
  receipt.installation = { status: 'partial' };
  receipt.completedAt = deps.now().toISOString();
  try {
    deps.writeReceipt(receiptPath, receipt);
  } catch (writeError) {
    if (primaryError) {
      throw new AggregateError([primaryError, writeError],
        `Backend rollout failed and its receipt could not be written: ${redact(writeError.message)}`);
    }
    throw new Error(`Backend rollout completed but its receipt could not be written: ${redact(writeError.message)}`,
      { cause: writeError });
  }
  deps.log(`Wrote backend image rollout receipt: ${receiptPath}; installation remains ${receipt.installation.status}.`);
  if (primaryError) {
    throw new Error(`Backend rollout ${receipt.result}; receipt: ${receiptPath}; ${receipt.error}`, { cause: primaryError });
  }
  return receipt;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const options = parseBackendRolloutArgs(process.argv.slice(2));
    if (options.help) {
      console.log(BACKEND_ROLLOUT_USAGE);
    } else {
      const receipt = await runBackendImageRollout(options);
      console.log(`Backend image rollout ${receipt.result} for ${receipt.publicationSourceSha}.`);
      if (options.execute) console.log(`Backend readiness: ${receipt.readiness.status}; Pod image IDs: ${receipt.pods.status}.`);
    }
  } catch (error) {
    console.error(`Backend image rollout failed: ${redact(error.message)}`);
    process.exitCode = 1;
  }
}
