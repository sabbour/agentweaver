#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { existsSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { IMAGE_REPOSITORIES } from './image-repositories.mjs';
import { DEFAULT_REGISTRY, parseRegistryTarget, verifyServiceRegistryImage } from './registry.mjs';
import {
  assertTargetContext,
  buildProjectsConfigMigrationJob,
  deleteKubernetesResourceWithPreconditions,
  isMaintenanceRunSettled,
  sourceSupportsConsumeGuard,
  sourceSupportsPairAVerifier,
} from './deploy-kustomize.mjs';
import { targetValuePairs } from './target-values.mjs';
import { hasHostRequestKeys } from './provider-host-request-overlay.mjs';
import {
  EVENTS_RATE_CARD_KEYS,
  hasRateCardKeys,
  PROJECTS_RATE_CARD_KEYS,
} from './provider-rate-card-overlay.mjs';
import { run } from '../azure/lib/exec.mjs';
import { installTarget } from '../azure/lib/targets.mjs';

const SHA = /^[a-f0-9]{40}$/;
const DIGEST = /^sha256:[a-f0-9]{64}$/;

export const DEPLOY_FROM_COMMIT_USAGE =
  'Usage: npm run azure:deploy-from-commit -- <full-lowercase-40-character-commit-sha> ' +
  '[--registry <registry>] [--resource-group <name>] [--projects-config-only] [--model-bindings <file.json>] ' +
  '[--remove-model-binding <reference>]... [--provider-host-request <file.json>] ' +
  '[--provider-rate-card <file.json>] [--agenthost-egress-audit <mode>] --execute\n' +
  'Audit mode: off, allowed, or denied. Default: off.';

function fail(message) {
  throw new Error(`Commit deployment: ${message}`);
}

export function parseDeployFromCommitArgs(argv) {
  if (!SHA.test(argv[0] ?? '')) {
    fail(DEPLOY_FROM_COMMIT_USAGE);
  }
  let registry = DEFAULT_REGISTRY;
  let hasRegistry = false;
  let execute = false;
  let modelBindings;
  let providerHostRequest;
  let providerRateCard;
  let resourceGroup;
  let agentHostEgressAudit;
  let hasAgentHostEgressAudit = false;
  let projectsConfigOnly = false;
  const removeModelBindings = [];
  for (let index = 1; index < argv.length; index += 1) {
    if (argv[index] === '--execute') {
      if (execute) fail(DEPLOY_FROM_COMMIT_USAGE);
      execute = true;
    } else if (argv[index] === '--projects-config-only') {
      if (projectsConfigOnly) fail(DEPLOY_FROM_COMMIT_USAGE);
      projectsConfigOnly = true;
    } else if (argv[index] === '--registry') {
      if (hasRegistry || argv[index + 1] === undefined ||
          argv[index + 1].startsWith('--')) fail(DEPLOY_FROM_COMMIT_USAGE);
      hasRegistry = true;
      registry = argv[++index];
      try {
        parseRegistryTarget(registry);
      } catch {
        fail('registry must be ghcr.io/sabbour or an Azure Container Registry login server.');
      }
    } else if (argv[index] === '--resource-group') {
      if (resourceGroup !== undefined || !installTarget(argv[index + 1])) fail(DEPLOY_FROM_COMMIT_USAGE);
      resourceGroup = argv[++index];
    } else if (argv[index] === '--model-bindings') {
      if (modelBindings !== undefined || argv[index + 1] === undefined ||
          argv[index + 1].startsWith('--')) fail(DEPLOY_FROM_COMMIT_USAGE);
      modelBindings = path.resolve(argv[++index]);
    } else if (argv[index] === '--provider-host-request') {
      if (providerHostRequest !== undefined || argv[index + 1] === undefined ||
          argv[index + 1].startsWith('--')) fail(DEPLOY_FROM_COMMIT_USAGE);
      providerHostRequest = path.resolve(argv[++index]);
    } else if (argv[index] === '--provider-rate-card') {
      if (providerRateCard !== undefined || argv[index + 1] === undefined ||
          argv[index + 1].startsWith('--')) fail(DEPLOY_FROM_COMMIT_USAGE);
      providerRateCard = path.resolve(argv[++index]);
    } else if (argv[index] === '--remove-model-binding') {
      const reference = argv[index + 1];
      if (reference === undefined || reference.startsWith('--') ||
          removeModelBindings.includes(reference)) fail(DEPLOY_FROM_COMMIT_USAGE);
      removeModelBindings.push(reference);
      index += 1;
    } else if (argv[index] === '--agenthost-egress-audit') {
      if (hasAgentHostEgressAudit) fail('AgentHost egress audit can be specified only once.');
      const mode = argv[index + 1];
      if (!['off', 'allowed', 'denied'].includes(mode)) {
        fail('AgentHost egress audit must be off, allowed, or denied.');
      }
      agentHostEgressAudit = mode;
      hasAgentHostEgressAudit = true;
      index += 1;
    } else fail(DEPLOY_FROM_COMMIT_USAGE);
  }
  if (!execute) fail(DEPLOY_FROM_COMMIT_USAGE);
  return {
    sourceSha: argv[0],
    ...(registry === DEFAULT_REGISTRY ? {} : { registry }),
    ...(modelBindings === undefined ? {} : { modelBindings }),
    ...(providerHostRequest === undefined ? {} : { providerHostRequest }),
    ...(providerRateCard === undefined ? {} : { providerRateCard }),
    ...(removeModelBindings.length ? { removeModelBindings } : {}),
    ...(agentHostEgressAudit === undefined ? {} : { agentHostEgressAudit }),
    ...(projectsConfigOnly ? { projectsConfigOnly: true } : {}),
    ...(resourceGroup === undefined || resourceGroup === 'agentweaver' ? {} : { resourceGroup }),
  };
}

function command(exec, name, args, cwd, action, settings = {}) {
  const result = exec(name, args, { cwd, check: false, ...settings });
  if (result.status !== 0) {
    fail(`${action}: ${(result.stderr || result.stdout || `exit ${result.status}`).trim()}`);
  }
  return result;
}

function requireLiveProviderHostRequestDisabledForOlderDeployer(exec, context, worktreePath, resourceGroup, sourceSha) {
  assertTargetContext(exec, context, worktreePath, resourceGroup ?? 'agentweaver');
  for (const [name, prefix] of [
    ['projects-config-runtime-config', 'ProjectsConfig__ProviderHostRequest__'],
    ['environment-runtime-config', 'Environment__ProviderHostRequest__'],
  ]) {
    const read = command(
      exec,
      'kubectl',
      [
        'get', 'configmap', name, '--ignore-not-found=true', '--output=json',
        '--context', context, '--namespace', 'agentweaver',
      ],
      worktreePath,
      `Could not read ConfigMap/${name}`);
    if (!read.stdout.trim()) continue;
    let configMap;
    try {
      configMap = JSON.parse(read.stdout);
    } catch {
      fail(`Kubernetes returned invalid JSON for ConfigMap/${name}.`);
    }
    if (hasHostRequestKeys(configMap, prefix)) {
      fail(
        `The live cluster has the private provider host-request channel enabled, and source ${sourceSha} ` +
        'predates provider host-request support; select a newer source commit.');
    }
  }
}

function requireLiveProviderRateCardDisabledForOlderDeployer(exec, context, worktreePath, resourceGroup, sourceSha) {
  assertTargetContext(exec, context, worktreePath, resourceGroup ?? 'agentweaver');
  for (const [name, keys] of [
    ['projects-config-runtime-config', PROJECTS_RATE_CARD_KEYS],
    ['events-and-sessions-runtime-config', EVENTS_RATE_CARD_KEYS],
  ]) {
    const read = command(
      exec,
      'kubectl',
      [
        'get', 'configmap', name, '--ignore-not-found=true', '--output=json',
        '--context', context, '--namespace', 'agentweaver',
      ],
      worktreePath,
      `Could not read ConfigMap/${name}`);
    if (!read.stdout.trim()) continue;
    let configMap;
    try {
      configMap = JSON.parse(read.stdout);
    } catch {
      fail(`Kubernetes returned invalid JSON for ConfigMap/${name}.`);
    }
    if (hasRateCardKeys(configMap, keys)) {
      fail(
        `The live cluster has private Projects-to-Events rate-card wiring enabled, and source ${sourceSha} ` +
        'predates provider rate-card support; select a newer source commit.');
    }
  }
}

const NAMESPACE = 'agentweaver';
const MAINTENANCE_LEASE_NAME = 'p1-db-maintenance-lock';
const MAINTENANCE_STATE_NAME = 'p1-db-maintenance-state';
const MAINTENANCE_RUN_LABEL = 'agentweaver.io/db-maintenance-run';

function acquireMaintenanceLease(exec, context, root, runId, now = () => new Date()) {
  const existing = exec('kubectl', [
    'get', 'lease', MAINTENANCE_LEASE_NAME, '--namespace', NAMESPACE, '--context', context, '--output=json',
  ], { cwd: root, check: false });
  if (existing.status === 0) {
    const lease = JSON.parse(existing.stdout || '{}');
    fail(`database maintenance lock is held by ${lease.spec?.holderIdentity ?? 'an unknown run'}.`);
  }
  if (!/NotFound/i.test(existing.stderr ?? '')) {
    fail(`could not inspect ${MAINTENANCE_LEASE_NAME}: ${existing.stderr || existing.stdout || 'kubectl error'}`);
  }
  command(exec, 'kubectl', ['create', '--namespace', NAMESPACE, '--context', context, '--filename', '-'], root,
    `Could not acquire ${MAINTENANCE_LEASE_NAME}`, {
      input: JSON.stringify({
        apiVersion: 'coordination.k8s.io/v1',
        kind: 'Lease',
        metadata: {
          name: MAINTENANCE_LEASE_NAME,
          namespace: NAMESPACE,
          labels: { [MAINTENANCE_RUN_LABEL]: runId },
        },
        spec: {
          holderIdentity: runId,
          acquireTime: now().toISOString().replace(/\.\d+Z$/, '.000000Z'),
        },
      }),
    });
}

async function releaseMaintenanceLease(
  exec, context, root, runId, deleteLease = deleteKubernetesResourceWithPreconditions,
  settlementOptions = {},
) {
  if (!isMaintenanceRunSettled(exec, { context, root, runId, ...settlementOptions })) {
    fail(`cannot release ${MAINTENANCE_LEASE_NAME} for ${runId} while its Job or Pods remain active or unknown; ` +
      `settle them and run npm run db:maintenance:projects-config -- --release-lock ${runId}.`);
  }
  const leaseRes = exec('kubectl', [
    'get', 'lease', MAINTENANCE_LEASE_NAME, '--namespace', NAMESPACE, '--context', context, '--output=json',
  ], { cwd: root, check: false });
  if (leaseRes.status !== 0) {
    fail(`could not read ${MAINTENANCE_LEASE_NAME} before release.`);
  }
  const lease = JSON.parse(leaseRes.stdout || '{}');
  const uid = lease.metadata?.uid;
  const resourceVersion = lease.metadata?.resourceVersion;
  if (lease.spec?.holderIdentity !== runId || !uid || !resourceVersion) {
    fail(`${MAINTENANCE_LEASE_NAME} changed holder or metadata before release (${lease.spec?.holderIdentity ?? 'unknown'}).`);
  }
  const apiPath = `/apis/coordination.k8s.io/v1/namespaces/${encodeURIComponent(NAMESPACE)}/leases/${encodeURIComponent(MAINTENANCE_LEASE_NAME)}`;
  try {
    await deleteLease({
      context,
      root,
      apiPath,
      uid,
      resourceVersion,
      resourceLabel: MAINTENANCE_LEASE_NAME,
    });
  } catch (error) {
    fail(`Could not release ${MAINTENANCE_LEASE_NAME}: ${error.message}`);
  }
}

async function verifyLiveExactPairA(
  exec, context, root, registry, resourceGroup, verifyImage, fetchImpl, registryOptions, runId,
  onRetainLease = () => {},
) {
  let verifierImage;
  const depRes = exec('kubectl', [
    'get', 'deployment', 'projects-config', '--namespace', NAMESPACE, '--context', context,
    '--ignore-not-found=true', '--output=json',
  ], { cwd: root, check: false });
  if (depRes.status === 0 && depRes.stdout?.trim()) {
    try {
      const dep = JSON.parse(depRes.stdout);
      const liveImage = dep.spec?.template?.spec?.containers?.[0]?.image;
      if (typeof liveImage === 'string' && liveImage.includes('@sha256:')) {
        const verified = await verifyImage(liveImage, fetchImpl, undefined, registryOptions);
        if (verified && sourceSupportsPairAVerifier(exec, root, verified.sourceSha, verified.sourceTree)) {
          verifierImage = liveImage;
        }
      }
    } catch {
      verifierImage = undefined;
    }
  }
  if (!verifierImage) {
    const v1HeadRes = exec('git', ['rev-parse', '--verify', 'refs/remotes/origin/v1'], { cwd: root, check: false });
    const v1TreeRes = exec('git', ['rev-parse', '--verify', 'refs/remotes/origin/v1^{tree}'], { cwd: root, check: false });
    if (v1HeadRes.status === 0 && v1TreeRes.status === 0) {
      const v1Sha = v1HeadRes.stdout.trim();
      const v1Tree = v1TreeRes.stdout.trim();
      if (sourceSupportsPairAVerifier(exec, root, v1Sha, v1Tree)) {
        const tag = `${registry}/agentweaver.projects.config:sha-${v1Sha}`;
        const inspected = exec('docker', [
          'buildx', 'imagetools', 'inspect', tag, '--format', '{{.Manifest.Digest}}',
        ], { cwd: root, check: false });
        const digest = inspected.status === 0 ? inspected.stdout.trim() : '';
        if (DIGEST.test(digest)) {
          const candidate = `${registry}/agentweaver.projects.config@${digest}`;
          try {
            const verified = await verifyImage(candidate, fetchImpl, undefined, registryOptions);
            if (verified?.sourceSha === v1Sha && verified?.sourceTree === v1Tree) {
              verifierImage = candidate;
            }
          } catch {
            verifierImage = undefined;
          }
        }
      }
    }
  }
  if (!verifierImage) return false;
  const jobName = `projects-config-paira-${runId}`;
  const pairs = targetValuePairs(root, resourceGroup);
  const jobSpec = buildProjectsConfigMigrationJob(
    root, exec, context, undefined, undefined, resourceGroup, pairs,
    jobName,
    ['dotnet', 'Agentweaver.Projects.Config.dll', '--verify-projects-config-pair-a'],
    runId,
    verifierImage,
  );
  delete jobSpec.spec.template.spec.containers[0].args;
  let passed = false;
  let verifierError;
  try {
    command(exec, 'kubectl', ['create', '--namespace', NAMESPACE, '--context', context, '--filename', '-'], root,
      'Could not create Pair A verification Job', { input: JSON.stringify(jobSpec) });
    const waitRes = exec('kubectl', [
      'wait', '--for=condition=complete', `job/${jobName}`, '--namespace', NAMESPACE,
      '--context', context, '--timeout=120s',
    ], { cwd: root, check: false });
    if (waitRes.status === 0) {
      const logsRes = exec('kubectl', [
        'logs', `job/${jobName}`, '--namespace', NAMESPACE, '--context', context, '--tail=10',
      ], { cwd: root, check: false });
      passed = logsRes.status === 0 && logsRes.stdout.includes('PROJECTS_CONFIG_EXACT_PAIR_A');
    }
  } catch (error) {
    verifierError = error;
  } finally {
    const deleted = exec('kubectl', [
      'delete', 'job', jobName, '--namespace', NAMESPACE, '--context', context,
      '--cascade=foreground', '--ignore-not-found', '--wait=true', '--timeout=120s',
    ], { cwd: root, check: false });
    if (deleted.status !== 0 || !isMaintenanceRunSettled(exec, { context, root, runId })) {
      onRetainLease();
      const cleanupError = new Error(
        `Commit deployment: could not clean up Pair A verification Job ${jobName}; ` +
        `retaining ${MAINTENANCE_LEASE_NAME} for ${runId}.`,
      );
      cleanupError.retainMaintenanceLease = true;
      throw verifierError
        ? new AggregateError([verifierError, cleanupError], `${verifierError.message}; ${cleanupError.message}`)
        : cleanupError;
    }
  }
  if (verifierError) throw verifierError;
  return passed;
}

export async function deployFromCommit({
  sourceSha, registry = DEFAULT_REGISTRY, modelBindings, removeModelBindings, providerHostRequest,
  providerRateCard, resourceGroup, agentHostEgressAudit = 'off', projectsConfigOnly = false,
}, overrides = {}) {
  if (!SHA.test(sourceSha ?? '')) fail('source SHA must be a lowercase full 40-character commit SHA.');
  try {
    parseRegistryTarget(registry);
  } catch {
    fail('registry must be ghcr.io/sabbour or an Azure Container Registry login server.');
  }
  if (!['off', 'allowed', 'denied'].includes(agentHostEgressAudit)) {
    fail('AgentHost egress audit must be off, allowed, or denied.');
  }

  const root = path.resolve(overrides.root ?? process.cwd());
  const modelOptions = {
    ...(modelBindings === undefined ? {} : { modelBindings: path.resolve(modelBindings) }),
    ...(removeModelBindings?.length ? { removeModelBindings } : {}),
  };
  const hostRequestOptions = {
    ...(providerHostRequest === undefined ? {} : { providerHostRequest: path.resolve(providerHostRequest) }),
  };
  const rateCardOptions = {
    ...(providerRateCard === undefined ? {} : { providerRateCard: path.resolve(providerRateCard) }),
  };
  if (modelOptions.modelBindings !== undefined && !existsSync(modelOptions.modelBindings)) {
    fail(`model bindings file does not exist: ${modelOptions.modelBindings}`);
  }
  if (hostRequestOptions.providerHostRequest !== undefined && !existsSync(hostRequestOptions.providerHostRequest)) {
    fail(`provider host-request file does not exist: ${hostRequestOptions.providerHostRequest}`);
  }
  if (rateCardOptions.providerRateCard !== undefined && !existsSync(rateCardOptions.providerRateCard)) {
    fail(`provider rate-card file does not exist: ${rateCardOptions.providerRateCard}`);
  }
  const exec = overrides.exec ?? run;
  const verifyImage = overrides.verifyImage ?? verifyServiceRegistryImage;
  const fetchImpl = overrides.fetchImpl ?? globalThis.fetch;
  const worktreePath = overrides.worktreePath ??
    path.join(path.dirname(root), `.agentweaver-deploy-from-commit-${randomUUID()}`);
  if (existsSync(worktreePath)) fail(`temporary worktree path already exists: ${worktreePath}`);

  command(exec, 'git', [
    'fetch', '--no-tags', 'origin', '+refs/heads/v1:refs/remotes/origin/v1',
  ], root, 'Could not fetch origin/v1');

  const ancestry = exec('git', [
    'merge-base', '--is-ancestor', sourceSha, 'refs/remotes/origin/v1',
  ], { cwd: root, check: false });
  if (ancestry.status !== 0) fail('selected commit must be an ancestor of the current origin/v1.');

  const treeResult = command(exec, 'git', [
    'rev-parse', '--verify', `${sourceSha}^{tree}`,
  ], root, 'Could not read the selected source tree');
  const sourceTree = treeResult.stdout.trim();
  if (!SHA.test(sourceTree)) fail('Git returned an invalid source tree SHA.');

  const targetResourceGroup = resourceGroup ?? 'agentweaver';
  const context = command(
    exec,
    'kubectl',
    ['config', 'current-context'],
    root,
    'Could not read the selected kubectl context',
  ).stdout.trim();
  if (!context) fail('No kubectl context is selected.');
  assertTargetContext(exec, context, root, targetResourceGroup);

  const maintenanceRunId = randomUUID().replaceAll('-', '').slice(0, 12);
  let maintenanceLeaseHeld = false;
  let retainOuterLease = false;
  let usedHistoricalDeployer = false;
  let worktreeAdded = false;
  let worktreeAttempted = false;
  let result;
  let failure;
  try {
    acquireMaintenanceLease(exec, context, root, maintenanceRunId, overrides.now);
    maintenanceLeaseHeld = true;

    const stateRes = exec('kubectl', [
      'get', 'configmap', MAINTENANCE_STATE_NAME, '--namespace', NAMESPACE, '--context', context,
      '--ignore-not-found=true', '--output=json',
    ], { cwd: root, check: false });
    if (stateRes.status !== 0) {
      fail(`could not read ${MAINTENANCE_STATE_NAME}: ${stateRes.stderr || stateRes.stdout || 'kubectl error'}`);
    }
    const stateData = stateRes.stdout?.trim()
      ? (JSON.parse(stateRes.stdout).data ?? {})
      : {};
    const consumeGuardPending = stateData['projectsConfig.consumeGuardPending'] === 'true';
    const consumeGuardRequired = stateData['projectsConfig.consumeGuardRequired'] === 'true';
    const commitSupportsConsumeGuard = sourceSupportsConsumeGuard(exec, root, sourceSha, sourceTree);
    let consumeGuardNeeded = consumeGuardPending || consumeGuardRequired;
    if (!consumeGuardNeeded && !commitSupportsConsumeGuard) {
      const exactPairA = typeof overrides.verifyExactPairA === 'function'
        ? Boolean(await overrides.verifyExactPairA({ exec, context, root, sourceSha }))
        : await verifyLiveExactPairA(
          exec, context, root, registry, targetResourceGroup,
          verifyImage, fetchImpl, overrides.registryOptions, maintenanceRunId,
          () => { retainOuterLease = true; },
        );
      if (!exactPairA) {
        fail(`source ${sourceSha} lacks AddNativeAcsProjectPolicyBaselineConsumeGuard and exact Pair A could not be verified.`);
      }
    } else if (consumeGuardNeeded && !commitSupportsConsumeGuard) {
      fail(`source ${sourceSha} lacks AddNativeAcsProjectPolicyBaselineConsumeGuard required by ${MAINTENANCE_STATE_NAME}.`);
    }
    if (!overrides.deployModule && (consumeGuardNeeded || commitSupportsConsumeGuard)) {
      const deployerSource = exec('git', ['show', `${sourceSha}:scripts/release/deploy-kustomize.mjs`],
        { cwd: root, check: false });
      if (deployerSource.status !== 0 ||
          !deployerSource.stdout.includes('DEPLOY_KUSTOMIZE_PROJECTS_CONFIG_CONSUME_GUARD')) {
        fail(`source ${sourceSha} predates Projects & Config consume-guard deployment support; select a newer source commit.`);
      }
    }

    worktreeAttempted = true;
    command(exec, 'git', [
      'worktree', 'add', '--detach', worktreePath, sourceSha,
    ], root, `Could not create the detached worktree at ${sourceSha}`);
    worktreeAdded = true;

    command(exec, 'npm', ['ci'], worktreePath, 'Locked dependency installation failed');

    const repositories = projectsConfigOnly ? ['agentweaver.projects.config'] : IMAGE_REPOSITORIES;
    const images = {};
    for (const repository of repositories) {
      const tag = `${registry}/${repository}:sha-${sourceSha}`;
      const inspected = command(exec, 'docker', [
        'buildx', 'imagetools', 'inspect', tag, '--format', '{{.Manifest.Digest}}',
      ], worktreePath, `Could not resolve ${tag}`);
      const digest = inspected.stdout.trim();
      if (!DIGEST.test(digest)) fail(`Registry returned an invalid digest for ${tag}.`);

      const verified = await verifyImage(
        `${registry}/${repository}@${digest}`, fetchImpl, undefined, overrides.registryOptions);
      if (verified.sourceSha !== sourceSha || verified.sourceTree !== sourceTree) {
        fail(`Image ${repository} does not match source ${sourceSha} and tree ${sourceTree}.`);
      }
      images[repository] = digest;
    }

    const imageManifest = path.join(worktreePath, `.deploy-from-commit-${randomUUID()}.json`);
    writeFileSync(imageManifest, JSON.stringify({ images }, null, 2), { flag: 'wx' });
    const deployModule = overrides.deployModule ?? (overrides.deployKustomize ? undefined : await import(pathToFileURL(
      path.join(worktreePath, 'scripts', 'release', 'deploy-kustomize.mjs'),
    ).href));
    if (registry !== DEFAULT_REGISTRY && deployModule?.DEPLOY_KUSTOMIZE_REGISTRY_OPTION !== true) {
      fail(`source ${sourceSha} predates registry-aware Kustomize deployment; select a newer source commit.`);
    }
    if (Object.keys(modelOptions).length && deployModule?.DEPLOY_KUSTOMIZE_MODEL_BINDINGS_OPTION !== true) {
      fail(`source ${sourceSha} predates the Kustomize model binding options; select a newer source commit.`);
    }
    if (Object.keys(hostRequestOptions).length &&
        deployModule?.DEPLOY_KUSTOMIZE_PROVIDER_HOST_REQUEST_OPTION !== true) {
      fail(`source ${sourceSha} predates the Kustomize provider host-request option; select a newer source commit.`);
    }
    if (Object.keys(rateCardOptions).length &&
        deployModule?.DEPLOY_KUSTOMIZE_PROVIDER_RATE_CARD_OPTION !== true) {
      fail(`source ${sourceSha} predates the Kustomize provider rate-card option; select a newer source commit.`);
    }
    if (resourceGroup !== undefined && deployModule?.DEPLOY_KUSTOMIZE_TARGET_OPTION !== true) {
      fail(`source ${sourceSha} predates install target selection; select a newer source commit.`);
    }
    if (agentHostEgressAudit !== 'off' && deployModule?.DEPLOY_KUSTOMIZE_AUDIT_OPTION !== true) {
      fail(`source ${sourceSha} predates AgentHost egress audit support; select a newer source commit.`);
    }
    if (deployModule?.DEPLOY_KUSTOMIZE_PROVIDER_HOST_REQUEST_OPTION !== true) {
      requireLiveProviderHostRequestDisabledForOlderDeployer(exec, context, worktreePath, resourceGroup, sourceSha);
    }
    if (deployModule?.DEPLOY_KUSTOMIZE_PROVIDER_RATE_CARD_OPTION !== true) {
      requireLiveProviderRateCardDisabledForOlderDeployer(exec, context, worktreePath, resourceGroup, sourceSha);
    }
    if ((consumeGuardNeeded || commitSupportsConsumeGuard) &&
        deployModule?.DEPLOY_KUSTOMIZE_PROJECTS_CONFIG_CONSUME_GUARD !== true) {
      fail(`source ${sourceSha} predates Projects & Config consume-guard deployment support; select a newer source commit.`);
    }
    const deployKustomize = overrides.deployKustomize ?? deployModule.deployKustomize;
    const supportsContextOverride = deployModule?.DEPLOY_KUSTOMIZE_CONTEXT_OPTION === true;
    usedHistoricalDeployer = !supportsContextOverride;
    let scopedKubeconfigPath;
    let previousKubeconfig;
    let hasPreviousKubeconfig = false;
    if (!supportsContextOverride) {
      const kubeconfigYaml = command(
        exec,
        'kubectl',
        ['config', 'view', '--minify', '--raw', '--context', context],
        root,
        `Could not export scoped kubeconfig for context ${context}`,
      ).stdout;
      if (!kubeconfigYaml.trim()) {
        fail(`Could not export scoped kubeconfig for context ${context}.`);
      }
      scopedKubeconfigPath = path.join(path.dirname(root), `.agentweaver-deploy-kubeconfig-${randomUUID()}`);
      writeFileSync(scopedKubeconfigPath, kubeconfigYaml, { mode: 0o600, flag: 'wx' });
      hasPreviousKubeconfig = Object.hasOwn(process.env, 'KUBECONFIG');
      previousKubeconfig = process.env.KUBECONFIG;
      process.env.KUBECONFIG = scopedKubeconfigPath;
    }
    try {
      result = await deployKustomize(
        { imageManifest, registry, ...modelOptions, ...hostRequestOptions, ...rateCardOptions, agentHostEgressAudit,
          ...(resourceGroup === undefined ? {} : { resourceGroup }) },
        {
          root: worktreePath,
          context,
          registryOptions: overrides.registryOptions,
          heldMaintenanceRunId: maintenanceRunId,
          onRetainLease: () => { retainOuterLease = true; },
          ...(overrides.deleteMaintenanceLease ? { deleteMaintenanceLease: overrides.deleteMaintenanceLease } : {}),
        });
    } finally {
      if (scopedKubeconfigPath) {
        if (hasPreviousKubeconfig) {
          process.env.KUBECONFIG = previousKubeconfig;
        } else {
          delete process.env.KUBECONFIG;
        }
        rmSync(scopedKubeconfigPath, { force: true });
      }
    }
  } catch (error) {
    failure = error;
  } finally {
    if (maintenanceLeaseHeld) {
      if (retainOuterLease || failure?.retainMaintenanceLease) {
        const retainError = new Error(
          `Commit deployment: retaining ${MAINTENANCE_LEASE_NAME} for ${maintenanceRunId} because its Job or Pods remain active or unknown; ` +
          `settle them and run npm run db:maintenance:projects-config -- --release-lock ${maintenanceRunId}.`,
        );
        failure = failure ? new AggregateError([failure, retainError], `${failure.message}; ${retainError.message}`) : retainError;
      } else {
        try {
          await releaseMaintenanceLease(exec, context, root, maintenanceRunId, overrides.deleteMaintenanceLease, {
            checkUnlabeledMigrationJobs: usedHistoricalDeployer,
          });
        } catch (releaseError) {
          failure = failure ? new AggregateError([failure, releaseError], `${failure.message}; ${releaseError.message}`) : releaseError;
        }
      }
    }
  }

  if (worktreeAdded || (worktreeAttempted && existsSync(worktreePath))) {
    try {
      command(exec, 'git', ['worktree', 'remove', '--force', worktreePath],
        root, `Could not remove temporary worktree ${worktreePath}`);
    } catch (error) {
      const cleanupError = `Could not remove temporary worktree "${worktreePath}": ${error.message}`;
      throw new Error(failure ? `${failure.message}; ${cleanupError}` : cleanupError);
    }
  }
  if (failure) throw failure;
  return result;
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) {
  try {
    await deployFromCommit(parseDeployFromCommitArgs(process.argv.slice(2)));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
