#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { existsSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { IMAGE_REPOSITORIES } from './image-repositories.mjs';
import { DEFAULT_REGISTRY, parseRegistryTarget, verifyServiceRegistryImage } from './registry.mjs';
import { run } from '../azure/lib/exec.mjs';

const SHA = /^[a-f0-9]{40}$/;
const DIGEST = /^sha256:[a-f0-9]{64}$/;

export const DEPLOY_FROM_COMMIT_USAGE =
  'Usage: npm run azure:deploy-from-commit -- <full-lowercase-40-character-commit-sha> ' +
  '[--registry <registry>] [--model-bindings <file.json>] [--remove-model-binding <reference>]... --execute';

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
    } else if (argv[index] === '--model-bindings') {
      if (modelBindings !== undefined || argv[index + 1] === undefined ||
          argv[index + 1].startsWith('--')) fail(DEPLOY_FROM_COMMIT_USAGE);
      modelBindings = path.resolve(argv[++index]);
    } else if (argv[index] === '--remove-model-binding') {
      const reference = argv[index + 1];
      if (reference === undefined || reference.startsWith('--') ||
          removeModelBindings.includes(reference)) fail(DEPLOY_FROM_COMMIT_USAGE);
      removeModelBindings.push(reference);
      index += 1;
    } else fail(DEPLOY_FROM_COMMIT_USAGE);
  }
  if (!execute) fail(DEPLOY_FROM_COMMIT_USAGE);
  return {
    sourceSha: argv[0],
    ...(registry === DEFAULT_REGISTRY ? {} : { registry }),
    ...(modelBindings === undefined ? {} : { modelBindings }),
    ...(removeModelBindings.length ? { removeModelBindings } : {}),
  };
}

function command(exec, name, args, cwd, action) {
  const result = exec(name, args, { cwd, check: false });
  if (result.status !== 0) {
    fail(`${action}: ${(result.stderr || result.stdout || `exit ${result.status}`).trim()}`);
  }
  return result;
}

export async function deployFromCommit({
  sourceSha, registry = DEFAULT_REGISTRY, modelBindings, removeModelBindings,
}, overrides = {}) {
  if (!SHA.test(sourceSha ?? '')) fail('source SHA must be a lowercase full 40-character commit SHA.');
  try {
    parseRegistryTarget(registry);
  } catch {
    fail('registry must be ghcr.io/sabbour or an Azure Container Registry login server.');
  }

  const root = path.resolve(overrides.root ?? process.cwd());
  const modelOptions = {
    ...(modelBindings === undefined ? {} : { modelBindings: path.resolve(modelBindings) }),
    ...(removeModelBindings?.length ? { removeModelBindings } : {}),
  };
  if (modelOptions.modelBindings !== undefined && !existsSync(modelOptions.modelBindings)) {
    fail(`model bindings file does not exist: ${modelOptions.modelBindings}`);
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
    const deployKustomize = overrides.deployKustomize ?? deployModule.deployKustomize;
    result = await deployKustomize(
      { imageManifest, registry, ...modelOptions },
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
