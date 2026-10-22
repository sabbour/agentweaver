/**
 * Rolls out only the published Web image in an existing v1 namespace.
 * It verifies the source-bound publication before reading or changing Deployments.
 */
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { run, redact } from '../azure/lib/exec.mjs';
import { componentImageRepository } from './pack.mjs';
import {
  canonicalJson, deploymentInventory, imageDigest, planDeploymentInventory,
  verifyDeploymentPreservation, verifyDeploymentReady, writeRolloutReceipt,
} from './image-rollout-common.mjs';
import { selectPublishedComponentImage } from './publication-image.mjs';
import { verifyServiceRegistryImage } from './registry.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const WEB_COMPONENT = 'Agentweaver.Web';
const WEB_DEPLOYMENT = 'web';
const WEB_REVISION_ANNOTATION = 'deployment.kubernetes.io/revision';
const WEB_IMAGE_REPOSITORY = `ghcr.io/sabbour/${componentImageRepository(WEB_COMPONENT)}`;
const NAMESPACE = 'agentweaver';
const SHA = /^[a-f0-9]{40}$/;
const IMAGE_DIGEST = /^sha256:[a-f0-9]{64}$/;

export const WEB_ROLLOUT_USAGE = `Usage: npm run release:deploy-web -- --publication <publication.json> --context <approved-kube-context> [options]

Without --execute the command verifies the publication and plans a Web-only image rollout.

  --publication <file>                Source-bound release publication receipt. Required.
  --context <name>                    Explicit Kubernetes context. Required.
  --execute                           Change only deployment/web's web container image.
  --receipt-dir <directory>           Receipt output directory. Default: ~/.agentweaver/web-image-rollouts.
  --readiness-timeout-seconds <n>     Web rollout timeout. Default: 600.
  --help                              Show this help.
`;

function fail(message) {
  throw new Error(`Web-only image rollout: ${message}`);
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

/** Parses the deliberately narrow CLI so no option can select another workload or namespace. */
export function parseWebRolloutArgs(argv = []) {
  const options = {
    execute: false,
    publicationPath: undefined,
    context: undefined,
    receiptDir: path.join(homedir(), '.agentweaver', 'web-image-rollouts'),
    readinessTimeoutSeconds: 600,
    help: false,
  };
  const valued = new Map([
    ['--publication', 'publicationPath'],
    ['--context', 'context'],
    ['--receipt-dir', 'receiptDir'],
    ['--readiness-timeout-seconds', 'readinessTimeoutSeconds'],
  ]);
  for (let index = 0; index < argv.length; index++) {
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
  if (typeof options.publicationPath !== 'string') fail('--publication is required.');
  if (typeof options.context !== 'string' || options.context.trim() !== options.context || options.context.length === 0) {
    fail('--context must name the approved Kubernetes context.');
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
  const result = exec('git', ['merge-base', '--is-ancestor', sourceSha, target], { cwd: root, check: false });
  if (result.status !== 0) fail(`${description} ${sourceSha} is not an ancestor of ${target}.`);
}

function selectPublishedWeb(publication, manifest, git, exec, root) {
  return selectPublishedComponentImage(publication, manifest, WEB_COMPONENT,
    { git, exec, root, label: 'Web' });
}

function webRevisionValue(deployment) {
  const revision = deployment.metadata?.annotations?.[WEB_REVISION_ANNOTATION];
  return typeof revision === 'string' ? revision : null;
}

function parseWebRevision(deployment, where) {
  const revision = webRevisionValue(deployment);
  if (revision === null || !/^(0|[1-9]\d*)$/.test(revision) || !Number.isSafeInteger(Number(revision))) {
    fail(`${where} has no valid ${WEB_REVISION_ANNOTATION} annotation.`);
  }
  return Number(revision);
}

/**
 * Validates the live Web selector and its single named app container before any write.
 * The repository workload labels and selector are fixed so a similarly named Deployment cannot be guessed.
 */
function validateWebDeployment(inventory) {
  const deployment = inventory.byName.get(WEB_DEPLOYMENT);
  // Require one workload explicitly labeled as Web; a similarly named Deployment is not enough.
  const selected = [...inventory.byName.values()].filter(item =>
    item.metadata?.labels?.['agentweaver.io/service'] === WEB_DEPLOYMENT ||
    item.spec?.template?.metadata?.labels?.['agentweaver.io/workload'] === WEB_DEPLOYMENT);
  if (!deployment || selected.length !== 1 || selected[0] !== deployment ||
      deployment.metadata?.labels?.['app.kubernetes.io/name'] !== WEB_DEPLOYMENT ||
      deployment.metadata?.labels?.['agentweaver.io/service'] !== WEB_DEPLOYMENT ||
      canonicalJson(deployment.spec?.selector?.matchLabels) !== canonicalJson({
        'app.kubernetes.io/name': WEB_DEPLOYMENT,
      }) ||
      deployment.spec?.template?.metadata?.labels?.['app.kubernetes.io/name'] !== WEB_DEPLOYMENT ||
      deployment.spec?.template?.metadata?.labels?.['agentweaver.io/service'] !== WEB_DEPLOYMENT ||
      deployment.spec?.template?.metadata?.labels?.['agentweaver.io/workload'] !== WEB_DEPLOYMENT) {
    fail('current Web Deployment identity and selector do not match the reviewed workload.');
  }
  const containers = deployment.spec.template.spec.containers;
  const matches = containers.map((container, index) => ({ container, index }))
    .filter(entry => entry.container.name === WEB_DEPLOYMENT);
  if (matches.length !== 1 || !Number.isSafeInteger(deployment.spec.replicas ?? 1) ||
      (deployment.spec.replicas ?? 1) < 1) {
    fail('current Web Deployment must have one web container and at least one replica.');
  }
  const { container, index } = matches[0];
  const digestPinnedWebImage = new RegExp(
    `^${WEB_IMAGE_REPOSITORY.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?::[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})?@sha256:[a-f0-9]{64}$`,
  );
  if (!digestPinnedWebImage.test(container.image ?? '')) {
    fail('current Web container image is not a digest-pinned Agentweaver.Web image.');
  }
  if ((deployment.spec.template.spec.initContainers ?? []).some(entry => entry.name === WEB_DEPLOYMENT)) {
    fail('the Web workload has an ambiguous init container named web.');
  }
  return {
    deployment,
    containerIndex: index,
    currentImage: container.image,
    replicas: deployment.spec.replicas ?? 1,
  };
}

/**
 * Verifies the source and published image, snapshots every Deployment, and optionally patches one Web image.
 * A JSON Patch resourceVersion test prevents a stale plan from overwriting a concurrent Deployment update.
 */
export async function runWebImageRollout(options, overrides = {}) {
  const root = path.resolve(overrides.root ?? repositoryRoot);
  const deps = {
    exec: run,
    readFile: readFileSync,
    verifyImage: verifyServiceRegistryImage,
    writeReceipt: writeRolloutReceipt,
    now: () => new Date(),
    newRunId: randomUUID,
    log: message => console.log(message),
    ...overrides,
  };
  const runId = deps.newRunId();
  const receiptPath = path.resolve(options.receiptDir, `web-image-rollout-${runId}.json`);
  const receipt = {
    schemaVersion: 1,
    runId,
    mode: options.execute ? 'execute' : 'dry-run',
    scope: WEB_COMPONENT,
    target: { context: options.context, namespace: NAMESPACE },
    publicationPath: options.publicationPath,
    receiptPath,
    startedAt: deps.now().toISOString(),
    publicationSourceSha: null,
    webImage: null,
    imageRevision: null,
    imageVersion: null,
    webDeploymentRevision: { before: null, after: null },
    changed: false,
    deployments: { before: [], plannedAfter: [], after: [] },
    readiness: { status: 'not-run' },
    installation: { status: 'partial' },
    result: 'running',
  };
  let primaryError;
  let executionStarted = false;
  let before;
  let web;

  const git = (...args) => gitOutput(deps.exec, root, args, `Git ${args.join(' ')}`);
  const kubectl = args => deps.exec('kubectl', [
    '--context', options.context, '--namespace', NAMESPACE, ...args,
  ], { cwd: root, check: false });
  const inventory = () => deploymentInventory(
    (args, settings) => deps.exec('kubectl', ['--context', options.context, ...args], settings),
    NAMESPACE, root, fail,
  );

  try {
    if (!options.context || !options.publicationPath || !Number.isInteger(options.readinessTimeoutSeconds)) {
      fail('parsed --publication, --context, and readiness timeout options are required.');
    }
    const status = git('status', '--porcelain', '--untracked-files=all');
    if (status.trim().length > 0) fail('the source checkout must be clean.');
    const head = git('rev-parse', 'HEAD').trim();
    if (!SHA.test(head)) fail('the deployment-tool checkout HEAD is not a full Git commit SHA.');
    const fetch = deps.exec('git', ['fetch', '--quiet', 'origin', 'v1'], { cwd: root, check: false });
    if (fetch.status !== 0) fail('could not refresh origin/v1 before source verification.');
    requireAncestor(deps.exec, root, head, 'origin/v1', 'deployment-tool source');

    const publicationText = deps.readFile(path.resolve(root, options.publicationPath), 'utf8');
    const publication = parseJson(publicationText, 'publication receipt');
    receipt.publicationSourceSha = publication.sourceSha ?? null;
    if (!SHA.test(publication.sourceSha ?? '')) fail('publication receipt has no full source commit SHA.');
    requireAncestor(deps.exec, root, publication.sourceSha, 'origin/v1', 'publication source');
    const manifest = parseJson(git('show', `${publication.sourceSha}:releases/foundation.json`),
      'source-bound release manifest');
    const selected = selectPublishedWeb(publication, manifest, git, deps.exec, root);
    requireAncestor(deps.exec, root, selected.imageSourceSha, 'origin/v1', 'Web image source');
    const verified = await deps.verifyImage(selected.image);
    if (!verified || !IMAGE_DIGEST.test(verified.digest ?? '') ||
        !(verified.acceptedDigests instanceof Set) || !verified.acceptedDigests.has(selected.digest) ||
        verified.sourceSha !== selected.imageSourceSha || verified.sourceTree !== selected.imageSourceTree ||
        verified.version !== selected.version) {
      fail('published Web image digest, OCI revision, source tree, or version differs from its receipt.');
    }
    receipt.webImage = selected.image;
    receipt.imageRevision = verified.sourceSha;
    receipt.imageVersion = verified.version;

    before = inventory();
    web = validateWebDeployment(before);
    const updates = new Map([[WEB_DEPLOYMENT, [{
      containerIndex: web.containerIndex,
      image: selected.image,
    }]]]);
    receipt.deployments.before = before.records;
    receipt.webDeploymentRevision.before = webRevisionValue(web.deployment);
    receipt.changed = web.currentImage !== selected.image;
    receipt.deployments.plannedAfter = planDeploymentInventory(before, updates, NAMESPACE, fail).records;

    if (!options.execute) {
      receipt.result = 'planned';
      deps.log(`Dry run passed. Only deployment/${WEB_DEPLOYMENT} container ${WEB_DEPLOYMENT} would use ${selected.image}.`);
    } else {
      executionStarted = true;
      let webRevisionBefore;
      if (receipt.changed) {
        webRevisionBefore = parseWebRevision(web.deployment, 'current Web Deployment');
        const resourceVersion = web.deployment.metadata?.resourceVersion;
        if (typeof resourceVersion !== 'string' || resourceVersion.length === 0) {
          fail('current Web Deployment has no resourceVersion for a guarded update.');
        }
        // Guard the single-field patch against any Web Deployment change after this snapshot.
        const imagePath = `/spec/template/spec/containers/${web.containerIndex}/image`;
        const patch = [
          { op: 'test', path: '/metadata/resourceVersion', value: resourceVersion },
          { op: 'test', path: imagePath, value: web.currentImage },
          { op: 'replace', path: imagePath, value: selected.image },
        ];
        const updated = kubectl([
          'patch', 'deployment', WEB_DEPLOYMENT, '--type=json', '--patch', JSON.stringify(patch),
        ]);
        if (updated.status !== 0) {
          fail(`could not update only deployment/${WEB_DEPLOYMENT}'s web image: ${updated.stderr.trim()}`);
        }
      }

      receipt.readiness = { status: 'waiting', timeoutSeconds: options.readinessTimeoutSeconds };
      const rollout = kubectl([
        'rollout', 'status', `deployment/${WEB_DEPLOYMENT}`,
        `--timeout=${options.readinessTimeoutSeconds}s`,
      ]);
      if (rollout.status !== 0) fail(`Web Deployment did not become ready: ${rollout.stderr.trim()}`);

      const after = inventory();
      receipt.deployments.after = after.records;
      const currentWeb = after.byName.get(WEB_DEPLOYMENT);
      receipt.webDeploymentRevision.after = webRevisionValue(currentWeb);
      if (receipt.changed) {
        const webRevisionAfter = parseWebRevision(currentWeb, 'updated Web Deployment');
        if (webRevisionAfter <= webRevisionBefore) {
          fail('Web Deployment controller revision did not increase after changing its image.');
        }
      }
      verifyDeploymentPreservation(before, after, updates, {
        namespace: NAMESPACE,
        label: 'Web',
        ignoredAnnotations: new Map([[WEB_DEPLOYMENT, [WEB_REVISION_ANNOTATION]]]),
        fail,
      });
      const currentImage = currentWeb.spec.template.spec.containers[web.containerIndex].image;
      if (currentImage !== selected.image) fail('Web Deployment readback does not match the verified image digest.');
      receipt.readiness = { status: 'passed', timeoutSeconds: options.readinessTimeoutSeconds,
        ...verifyDeploymentReady(currentWeb, web.replicas, 'Web', fail) };
      receipt.result = 'succeeded';
    }
  } catch (error) {
    primaryError = error;
    receipt.result = 'failed';
    receipt.error = redact(error instanceof Error ? error.message : String(error));
    if (receipt.readiness.status === 'waiting') {
      receipt.readiness = {
        status: 'failed',
        timeoutSeconds: options.readinessTimeoutSeconds,
        error: receipt.error,
      };
    }
    if (executionStarted && before && receipt.deployments.after.length === 0) {
      try {
        // Record live digests even on failed execution so the receipt reflects partial changes.
        const after = inventory();
        receipt.deployments.after = after.records;
        receipt.webDeploymentRevision.after = webRevisionValue(after.byName.get(WEB_DEPLOYMENT));
      } catch (afterError) {
        receipt.afterReadError = redact(afterError instanceof Error ? afterError.message : String(afterError));
      }
    }
  }

  // A component rollout is never whole-installation evidence by itself.
  receipt.installation = { status: 'partial' };
  receipt.completedAt = deps.now().toISOString();
  try {
    deps.writeReceipt(receiptPath, receipt);
  } catch (writeError) {
    if (primaryError) {
      throw new AggregateError([primaryError, writeError],
        `Web-only rollout failed and its receipt could not be written: ${redact(writeError.message)}`);
    }
    throw new Error(`Web-only rollout completed but its receipt could not be written: ${redact(writeError.message)}`,
      { cause: writeError });
  }
  deps.log(`Wrote Web-only rollout receipt: ${receiptPath}; installation remains ${receipt.installation.status}.`);
  if (primaryError) {
    throw new Error(`Web-only rollout ${receipt.result}; receipt: ${receiptPath}; ${receipt.error}`, { cause: primaryError });
  }
  return receipt;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const options = parseWebRolloutArgs(process.argv.slice(2));
    if (options.help) {
      console.log(WEB_ROLLOUT_USAGE);
    } else {
      const receipt = await runWebImageRollout(options);
      console.log(`Web-only image rollout ${receipt.result} for ${receipt.publicationSourceSha}.`);
      if (options.execute) console.log(`Web readiness: ${receipt.readiness.status}.`);
    }
  } catch (error) {
    console.error(`Web-only image rollout failed: ${redact(error.message)}`);
    process.exitCode = 1;
  }
}
