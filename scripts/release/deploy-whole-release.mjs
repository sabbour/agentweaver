/**
 * Reconciles the complete P0 installation to one source-bound publication.
 * This is the only release script that may write a deployed whole-release receipt.
 */
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { withP0UserKubeconfig } from '../azure/lib/namespace-bootstrap.mjs';
import { run, redact } from '../azure/lib/exec.mjs';
import {
  deploymentInventory,
} from './image-rollout-common.mjs';
import {
  WHOLE_RELEASE_RECEIPT_DIRECTORY, compareWholeReleaseState, checkSchemaCompatibility,
  plannedWholeReleaseInventory, resolveWholeReleaseImages,
  runGatewaySmokeChecks, verifyWholeReleaseImages, verifyWholeReleaseReadback,
  wholeReleaseFail, wholeReleaseUpdates, writeWholeReleaseReceipt, wholeReleaseComparison,
} from './whole-release.mjs';
import { componentImageRepository } from './pack.mjs';
import { verifyServiceRegistryImage } from './registry.mjs';
import { LOCK_NAME } from '../azure/lib/db-maintenance.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const NAMESPACE = 'agentweaver';
const SHA = /^[a-f0-9]{40}$/;
const IMAGE_DIGEST = /^sha256:[a-f0-9]{64}$/;
const SAFE_ROLLOUT_ORDER = [
  'identity-broker', 'events-and-sessions', 'orchestrator', 'environment',
  'knowledge', 'projects-config', 'gateway', 'mcp', 'web',
];

export const WHOLE_RELEASE_USAGE = `Usage: npm run release:deploy-whole -- --publication <publication.json> --subscription-id <id> --resource-group <name> --cluster <name> [options]

Without --execute the command reads live state and writes a complete rollout plan.

  --publication <file>                Current source-bound publication receipt. Required.
  --previous-publication <file>        Prior published receipt for unchanged images.
  --subscription-id <id>               Azure subscription containing the P0 cluster. Required.
  --resource-group <name>              Resource group containing the P0 cluster. Required.
  --cluster <name>                     P0 AKS cluster name. Required.
  --execute                            Roll out every differing service and run smoke checks.
  --gateway-url <url>                  Authenticated Gateway origin for execute smoke checks.
  --tenant-id <id>                     Tenant selector for Gateway smoke checks.
  --gateway-token-env <name>           Environment variable containing the broker token.
  --issuer <url>                       Broker token issuer.
  --audience <value>                   Broker token audience.
  --actor-id <id>                      Broker token subject.
  --project-id <id>                    Project used for run and metrics smoke checks.
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
    subscriptionId: undefined, resourceGroup: undefined, clusterName: undefined,
    gatewayUrl: undefined, tenantId: undefined, gatewayTokenEnv: 'AGENTWEAVER_GATEWAY_TOKEN',
    issuer: undefined, audience: undefined, actorId: undefined, projectId: undefined,
    receiptDir: WHOLE_RELEASE_RECEIPT_DIRECTORY, readinessTimeoutSeconds: 600, help: false,
  };
  const valued = new Map([
    ['--publication', 'publicationPath'], ['--previous-publication', 'previousPublicationPath'],
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
  if (options.execute && ![options.gatewayUrl, options.tenantId, options.issuer, options.audience,
    options.actorId, options.projectId].every(value => typeof value === 'string' && value.length > 0)) {
    wholeReleaseFail('execute mode requires Gateway smoke-check identity and project options.');
  }
  return options;
}

function gitOutput(exec, root, args, action) {
  const result = exec('git', args, { cwd: root, check: false });
  if (result.status !== 0) wholeReleaseFail(`${action}: ${(result.stderr ?? '').trim()}`);
  return result.stdout;
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
  if (!/NotFound/i.test(result.stderr ?? '')) {
    wholeReleaseFail(`could not verify the db:maintenance lock: ${(result.stderr ?? '').trim()}`);
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
    receipt.images = images.map(image => ({
      componentId: image.componentId, deployment: image.deployment, image: image.image,
      digest: image.digest, sourceSha: image.imageSourceSha, sourceTree: image.imageSourceTree,
      version: image.version, sourcePublicationSha: image.sourcePublicationSha,
    }));

    const action = async baseArgs => {
      const kubectl = args => deps.exec('kubectl', [...baseArgs, '--namespace', NAMESPACE, ...args],
        { cwd: root, check: false });
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
      receipt.schema.checks = await (deps.schemaCheck
        ? deps.schemaCheck({ services, kubectl, root, exec: deps.exec })
        : checkSchemaCompatibility({ services, kubectl, deps, root, exec: deps.exec }));
      receipt.schema.status = 'passed';
      if (!options.execute) {
        receipt.result = 'planned';
        deps.log('Dry run passed. The whole installation remains partial until execute verification succeeds.');
        return;
      }

      executionStarted = true;
      const updates = wholeReleaseUpdates(services);
      for (const deployment of SAFE_ROLLOUT_ORDER) {
        const service = services.find(item => item.deployment === deployment);
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

      const after = inventory();
      receipt.deployments.after = after.records;
      receipt.readiness = { status: 'passed', services: verifyWholeReleaseReadback(
        before, after, services, updates, wholeReleaseFail),
      };
      receipt.pods = { status: 'waiting', services: [] };
      receipt.pods.services = verifyPods(parseKubernetesObjects(
        kubectl(['get', 'replicasets,pods', '--output', 'json'])), services);
      receipt.pods.status = 'passed';
      receipt.smoke = { status: 'waiting', checks: [] };
      receipt.smoke.checks = await deps.smoke({
        gatewayUrl: options.gatewayUrl, tenantId: options.tenantId,
        token: process.env[options.gatewayTokenEnv], issuer: options.issuer,
        audience: options.audience, actorId: options.actorId, projectId: options.projectId,
        fetchImpl: deps.fetchImpl,
      });
      receipt.smoke.status = 'passed';
      receipt.status = 'deployed';
      receipt.installation = { status: 'deployed' };
      receipt.result = 'succeeded';
    };

    await deps.withKubeconfig({
      resourceGroup: options.resourceGroup, subscriptionId: options.subscriptionId,
      clusterName: options.clusterName,
    }, action, {
      requiredPermissions: [
        ['get', 'namespace/agentweaver'], ['get', 'leases', NAMESPACE], ['get', 'deployments', NAMESPACE],
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
    receipt.status = 'partial';
    receipt.installation = { status: 'partial' };
    receipt.result = 'failed';
    receipt.error = redact(error instanceof Error ? error.message : String(error));
    if (receipt.schema.status === 'not-run') receipt.schema = { status: 'failed', error: receipt.error, checks: [] };
    if (receipt.readiness.status === 'waiting') receipt.readiness = { status: 'failed', error: receipt.error };
    if (receipt.pods.status === 'waiting') receipt.pods = { status: 'failed', error: receipt.error, services: [] };
    if (receipt.smoke.status === 'waiting') receipt.smoke = { status: 'failed', error: receipt.error, checks: [] };
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
