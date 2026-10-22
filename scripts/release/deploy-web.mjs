/**
 * Rolls out only the published Web image in an existing v1 namespace.
 * It verifies the source-bound publication before reading or changing Deployments.
 */
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import path from 'node:path';
import { isDeepStrictEqual } from 'node:util';
import { fileURLToPath } from 'node:url';
import { run, redact } from '../azure/lib/exec.mjs';
import { componentImageRepository, componentImageTag } from './pack.mjs';
import { verifyServiceRegistryImage } from './registry.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const WEB_COMPONENT = 'Agentweaver.Web';
const WEB_DEPLOYMENT = 'web';
const WEB_REVISION_ANNOTATION = 'deployment.kubernetes.io/revision';
const WEB_IMAGE_REPOSITORY = `ghcr.io/sabbour/${componentImageRepository(WEB_COMPONENT)}`;
const NAMESPACE = 'agentweaver';
const SHA = /^[a-f0-9]{40}$/;
const HEX_SHA256 = /^[a-f0-9]{64}$/;
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

function canonicalJson(value) {
  // Sort object keys recursively so semantically identical Kubernetes specs hash the same way.
  return JSON.stringify(value, (_key, item) => item && typeof item === 'object' && !Array.isArray(item)
    ? Object.fromEntries(Object.entries(item).sort(([left], [right]) => left.localeCompare(right)))
    : item);
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
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

/**
 * Selects the exact Web entry from the publication receipt and source manifest.
 * A reused image keeps its original revision, which must remain an ancestor of the release source.
 */
function selectPublishedWeb(publication, manifest, git, exec, root) {
  // Validate the complete receipt shape before trusting any component entry from it.
  if (!isRecord(publication) || publication.schemaVersion !== 1 ||
      !SHA.test(publication.sourceSha ?? '') || publication.status !== 'published' ||
      !HEX_SHA256.test(publication.provenanceSha256 ?? '') ||
      publication.mode === 'images-only' ||
      (publication.requiredPackageIds !== undefined &&
        (!Array.isArray(publication.requiredPackageIds) ||
          new Set(publication.requiredPackageIds).size !== publication.requiredPackageIds.length ||
          publication.requiredPackageIds.some(id => typeof id !== 'string'))) ||
      !Array.isArray(publication.planned) || !Array.isArray(publication.published)) {
    fail('publication receipt is missing, partial, or not a standard release publication.');
  }
  // The manifest is read from publication.sourceSha by the caller; standard publications keep it as a draft.
  if (!isRecord(manifest) || manifest.stage !== 'draft' || !Array.isArray(manifest.components)) {
    fail('source manifest is not a tracked draft with a component list.');
  }
  // The manifest supplies the intended Web version; published artifact digests belong to the receipt.
  const components = manifest.components.filter(component => component?.id === WEB_COMPONENT);
  if (components.length !== 1) fail('source manifest must contain exactly one Agentweaver.Web component.');
  const component = components[0];
  if (component.kind !== 'service' || typeof component.version !== 'string' ||
      component.version.trim() !== component.version || component.version.length === 0) {
    fail('source manifest has no valid Agentweaver.Web version.');
  }

  // Require one planned and one published Web record so unrelated entries cannot supply the image.
  const planned = publication.planned.filter(entry => entry?.id === WEB_COMPONENT);
  const published = publication.published.filter(entry => entry?.id === WEB_COMPONENT);
  if (planned.length !== 1 || published.length !== 1) {
    fail('publication receipt must contain exactly one planned and published Web image.');
  }
  const plan = planned[0];
  const result = published[0];
  // Match both records to the source version and to the exact archive measured during publication.
  if (plan.kind !== 'image' || result.kind !== 'image' ||
      plan.version !== component.version || result.version !== component.version ||
      !HEX_SHA256.test(plan.sha256 ?? '') || result.archiveSha256 !== plan.sha256) {
    fail('Web publication receipt does not prove the exact source-manifest artifact.');
  }

  // A newly built image belongs to this publication; a reused image keeps its original build identity.
  let imageSourceSha = publication.sourceSha;
  let imageSourceTree;
  if (plan.reused !== undefined || result.reused !== undefined) {
    // Reuse requires the current receipt format and keeps the identity of the original image build.
    const reused = plan.reused;
    if (publication.requiredPackageIds === undefined ||
        !isRecord(reused) || !isDeepStrictEqual(result.reused, reused) ||
        !SHA.test(reused.sourceSha ?? '') || reused.sourceSha === publication.sourceSha ||
        !SHA.test(reused.sourceTree ?? '') || !SHA.test(reused.receiptSourceSha ?? '') ||
        !IMAGE_DIGEST.test(reused.inputFingerprint ?? '')) {
      fail('reused Web image provenance is malformed or differs between the plan and result.');
    }
    imageSourceSha = reused.sourceSha;
    imageSourceTree = reused.sourceTree;
    requireAncestor(exec, root, imageSourceSha, publication.sourceSha, 'reused image source');
  }
  // The tag names the image's build commit, while the immutable digest comes from the published result.
  const expectedTag = publication.requiredPackageIds === undefined
    ? component.version : componentImageTag(component.version, imageSourceSha);
  const imagePrefix = `${WEB_IMAGE_REPOSITORY}@`;
  const publishedDigest = typeof result.image === 'string' && result.image.startsWith(imagePrefix)
    ? result.image.slice(imagePrefix.length) : undefined;
  if (plan.destination !== `${WEB_IMAGE_REPOSITORY}:${expectedTag}` ||
      !IMAGE_DIGEST.test(publishedDigest ?? '') || /^0+$/.test(publishedDigest?.slice(7) ?? '')) {
    fail('Web image tag, repository, digest, and source manifest do not agree.');
  }
  const expectedImage = `${WEB_IMAGE_REPOSITORY}@${publishedDigest}`;
  // Bind the reused build tree to Git rather than trusting the receipt's source-tree string alone.
  const actualImageSourceTree = git('rev-parse', `${imageSourceSha}^{tree}`).trim();
  if (!SHA.test(actualImageSourceTree) ||
      (imageSourceTree !== undefined && imageSourceTree !== actualImageSourceTree)) {
    fail('Web image source tree does not match its exact Git source commit.');
  }
  imageSourceTree = actualImageSourceTree;
  return {
    sourceSha: publication.sourceSha,
    imageSourceSha,
    imageSourceTree,
    version: component.version,
    image: expectedImage,
    digest: publishedDigest,
  };
}

function imageDigest(image, where) {
  if (typeof image !== 'string' || image.trim() !== image) fail(`${where} has an invalid image reference.`);
  const match = /@(sha256:[a-f0-9]{64})$/.exec(image);
  if (!match) fail(`${where} image is not pinned by an immutable sha256 digest.`);
  return match[1];
}

function deploymentSettings(deployment, { ignoreControllerRevision = false } = {}) {
  const annotations = { ...(deployment.metadata?.annotations ?? {}) };
  if (ignoreControllerRevision) {
    // Kubernetes increments this controller-owned annotation when it creates a new Web revision.
    delete annotations[WEB_REVISION_ANNOTATION];
  }
  return {
    labels: deployment.metadata?.labels ?? {},
    annotations,
    spec: deployment.spec,
  };
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

function deploymentRecord(deployment) {
  if (!isRecord(deployment) || deployment.apiVersion !== 'apps/v1' ||
      deployment.kind !== 'Deployment' || deployment.metadata?.namespace !== NAMESPACE) {
    fail(`Deployment list contains an item that is not an apps/v1 Deployment in namespace ${NAMESPACE}.`);
  }
  const name = deployment.metadata?.name;
  if (typeof name !== 'string' || name.trim() !== name || name.length === 0 ||
      !isRecord(deployment.spec) || !isRecord(deployment.spec.template?.spec)) {
    fail('a Deployment has an invalid name or pod template.');
  }
  const template = deployment.spec.template.spec;
  if (!Array.isArray(template.containers) || template.containers.length === 0) {
    fail(`Deployment ${name} has no application containers.`);
  }
  const initContainers = template.initContainers ?? [];
  if (!Array.isArray(initContainers)) fail(`Deployment ${name} has an invalid init container list.`);
  // Include init containers so each Deployment snapshot accounts for every pinned image.
  const containers = [
    ...initContainers,
    ...template.containers,
  ];
  const names = new Set();
  const images = containers.map((container, index) => {
    if (typeof container?.name !== 'string' || container.name.length === 0 || names.has(container.name)) {
      fail(`Deployment ${name} has a missing or duplicate container name.`);
    }
    names.add(container.name);
    const kind = index < initContainers.length ? 'initContainer' : 'container';
    return {
      kind,
      name: container.name,
      image: container.image,
      digest: imageDigest(container.image, `Deployment ${name} container ${container.name}`),
    };
  });
  return {
    name,
    images,
    settingsSha256: sha256(canonicalJson(deploymentSettings(deployment))),
  };
}

function deploymentInventory(exec, options, root) {
  const result = exec('kubectl', [
    '--context', options.context, '--namespace', NAMESPACE, 'get', 'deployments', '--output', 'json',
  ], { cwd: root, check: false });
  if (result.status !== 0) fail(`could not read Deployments in namespace ${NAMESPACE}: ${result.stderr.trim()}`);
  const list = parseJson(result.stdout, 'Kubernetes Deployment list');
  // kubectl emits a generic List for `get deployments`; accept the typed wrapper too for compatibility.
  if (!isRecord(list) || !['List', 'DeploymentList'].includes(list.kind) ||
      !Array.isArray(list.items) || list.items.length === 0) {
    fail(`namespace ${NAMESPACE} has no valid Deployment list.`);
  }
  const byName = new Map();
  const records = [];
  for (const deployment of list.items) {
    const record = deploymentRecord(deployment);
    if (byName.has(record.name)) fail(`Deployment ${record.name} appears more than once.`);
    byName.set(record.name, deployment);
    records.push(record);
  }
  records.sort((left, right) => left.name.localeCompare(right.name));
  return { byName, records };
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

function plannedInventory(before, web, targetImage) {
  const items = [...before.byName.values()].map(deployment => structuredClone(deployment));
  const plannedWeb = items.find(item => item.metadata.name === WEB_DEPLOYMENT);
  plannedWeb.spec.template.spec.containers[web.containerIndex].image = targetImage;
  return { items, records: items.map(deploymentRecord).sort((left, right) => left.name.localeCompare(right.name)) };
}

function verifyPreservation(before, after, targetImage, webIndex) {
  // Compare full Deployment settings and permit only the selected Web container image to differ.
  if (before.byName.size !== after.byName.size ||
      [...before.byName.keys()].some(name => !after.byName.has(name))) {
    fail('the Deployment set changed during the Web rollout.');
  }
  for (const [name, original] of before.byName) {
    const current = after.byName.get(name);
    const expected = structuredClone(original);
    if (name === WEB_DEPLOYMENT) expected.spec.template.spec.containers[webIndex].image = targetImage;
    const ignoreControllerRevision = name === WEB_DEPLOYMENT;
    if (!isDeepStrictEqual(
      deploymentSettings(current, { ignoreControllerRevision }),
      deploymentSettings(expected, { ignoreControllerRevision }),
    )) {
      fail(name === WEB_DEPLOYMENT
        ? 'the Web rollout changed settings other than the selected container image.'
        : `non-Web Deployment ${name} changed during the Web rollout.`);
    }
  }
}

function verifyWebReady(deployment, replicas) {
  const generation = deployment.metadata?.generation;
  const observedGeneration = deployment.status?.observedGeneration;
  const status = deployment.status ?? {};
  // The controller must observe this exact generation and have every requested replica available.
  if (!Number.isSafeInteger(generation) || !Number.isSafeInteger(observedGeneration) ||
      observedGeneration < generation || status.updatedReplicas !== replicas ||
      status.readyReplicas !== replicas || status.availableReplicas !== replicas) {
    fail('Web rollout completed without every requested replica observed, updated, ready, and available.');
  }
  return { generation, observedGeneration, replicas, readyReplicas: status.readyReplicas };
}

function defaultWriteReceipt(file, receipt) {
  mkdirSync(path.dirname(file), { recursive: true });
  writeFileSync(file, `${JSON.stringify(receipt, null, 2)}\n`, { flag: 'wx' });
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
    writeReceipt: defaultWriteReceipt,
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

    before = deploymentInventory(deps.exec, options, root);
    web = validateWebDeployment(before);
    receipt.deployments.before = before.records;
    receipt.webDeploymentRevision.before = webRevisionValue(web.deployment);
    receipt.changed = web.currentImage !== selected.image;
    receipt.deployments.plannedAfter = plannedInventory(before, web, selected.image).records;

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

      const after = deploymentInventory(deps.exec, options, root);
      receipt.deployments.after = after.records;
      const currentWeb = after.byName.get(WEB_DEPLOYMENT);
      receipt.webDeploymentRevision.after = webRevisionValue(currentWeb);
      if (receipt.changed) {
        const webRevisionAfter = parseWebRevision(currentWeb, 'updated Web Deployment');
        if (webRevisionAfter <= webRevisionBefore) {
          fail('Web Deployment controller revision did not increase after changing its image.');
        }
      }
      verifyPreservation(before, after, selected.image, web.containerIndex);
      const currentImage = currentWeb.spec.template.spec.containers[web.containerIndex].image;
      if (currentImage !== selected.image) fail('Web Deployment readback does not match the verified image digest.');
      receipt.readiness = { status: 'passed', timeoutSeconds: options.readinessTimeoutSeconds,
        ...verifyWebReady(currentWeb, web.replicas) };
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
        const after = deploymentInventory(deps.exec, options, root);
        receipt.deployments.after = after.records;
        receipt.webDeploymentRevision.after = webRevisionValue(after.byName.get(WEB_DEPLOYMENT));
      } catch (afterError) {
        receipt.afterReadError = redact(afterError instanceof Error ? afterError.message : String(afterError));
      }
    }
  }

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
  deps.log(`Wrote Web-only rollout receipt: ${receiptPath}`);
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
