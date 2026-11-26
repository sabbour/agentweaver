#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { existsSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { IMAGE_REPOSITORIES } from './image-repositories.mjs';
import { DEFAULT_REGISTRY, parseRegistryTarget, verifyServiceRegistryImage } from './registry.mjs';
import { assertTargetContext } from './deploy-kustomize.mjs';
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
  '[--registry <registry>] [--resource-group <name>] [--model-bindings <file.json>] ' +
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
  const removeModelBindings = [];
  for (let index = 1; index < argv.length; index += 1) {
    if (argv[index] === '--execute') {
      if (execute) fail(DEPLOY_FROM_COMMIT_USAGE);
      execute = true;
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
    ...(resourceGroup === undefined || resourceGroup === 'agentweaver' ? {} : { resourceGroup }),
  };
}

function command(exec, name, args, cwd, action) {
  const result = exec(name, args, { cwd, check: false });
  if (result.status !== 0) {
    fail(`${action}: ${(result.stderr || result.stdout || `exit ${result.status}`).trim()}`);
  }
  return result;
}

function requireLiveProviderHostRequestDisabledForOlderDeployer(exec, worktreePath, resourceGroup, sourceSha) {
  const context = command(
    exec,
    'kubectl',
    ['config', 'current-context'],
    worktreePath,
    'Could not read the selected kubectl context').stdout.trim();
  if (!context) fail('No kubectl context is selected.');
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

function requireLiveProviderRateCardDisabledForOlderDeployer(exec, worktreePath, resourceGroup, sourceSha) {
  const context = command(
    exec,
    'kubectl',
    ['config', 'current-context'],
    worktreePath,
    'Could not read the selected kubectl context').stdout.trim();
  if (!context) fail('No kubectl context is selected.');
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

export async function deployFromCommit({
  sourceSha, registry = DEFAULT_REGISTRY, modelBindings, removeModelBindings, providerHostRequest,
  providerRateCard, resourceGroup, agentHostEgressAudit = 'off',
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

  let worktreeAdded = false;
  let worktreeAttempted = false;
  let result;
  let failure;
  try {
    worktreeAttempted = true;
    command(exec, 'git', [
      'worktree', 'add', '--detach', worktreePath, sourceSha,
    ], root, `Could not create the detached worktree at ${sourceSha}`);
    worktreeAdded = true;

    command(exec, 'npm', ['ci'], worktreePath, 'Locked dependency installation failed');

    const images = {};
    for (const repository of IMAGE_REPOSITORIES) {
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
      requireLiveProviderHostRequestDisabledForOlderDeployer(exec, worktreePath, resourceGroup, sourceSha);
    }
    if (deployModule?.DEPLOY_KUSTOMIZE_PROVIDER_RATE_CARD_OPTION !== true) {
      requireLiveProviderRateCardDisabledForOlderDeployer(exec, worktreePath, resourceGroup, sourceSha);
    }
    const deployKustomize = overrides.deployKustomize ?? deployModule.deployKustomize;
    result = await deployKustomize(
      { imageManifest, registry, ...modelOptions, ...hostRequestOptions, ...rateCardOptions, agentHostEgressAudit,
        ...(resourceGroup === undefined ? {} : { resourceGroup }) },
      { root: worktreePath, registryOptions: overrides.registryOptions });
  } catch (error) {
    failure = error;
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
