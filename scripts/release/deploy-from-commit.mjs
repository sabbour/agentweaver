#!/usr/bin/env node
import { randomUUID } from 'node:crypto';
import { existsSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { IMAGE_REPOSITORIES } from './image-repositories.mjs';
import { verifyServiceRegistryImage } from './registry.mjs';
import { run } from '../azure/lib/exec.mjs';

const REGISTRY = 'ghcr.io/sabbour';
const SHA = /^[a-f0-9]{40}$/;
const DIGEST = /^sha256:[a-f0-9]{64}$/;

export const DEPLOY_FROM_COMMIT_USAGE =
  'Usage: npm run azure:deploy-from-commit -- <full-lowercase-40-character-commit-sha> --execute';

function fail(message) {
  throw new Error(`Commit deployment: ${message}`);
}

export function parseDeployFromCommitArgs(argv) {
  if (argv.length !== 2 || !SHA.test(argv[0] ?? '') || argv[1] !== '--execute') {
    fail(DEPLOY_FROM_COMMIT_USAGE);
  }
  return { sourceSha: argv[0] };
}

function command(exec, name, args, cwd, action) {
  const result = exec(name, args, { cwd, check: false });
  if (result.status !== 0) {
    fail(`${action}: ${(result.stderr || result.stdout || `exit ${result.status}`).trim()}`);
  }
  return result;
}

export async function deployFromCommit({ sourceSha }, overrides = {}) {
  if (!SHA.test(sourceSha ?? '')) fail('source SHA must be a lowercase full 40-character commit SHA.');

  const root = path.resolve(overrides.root ?? process.cwd());
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
      const tag = `${REGISTRY}/${repository}:sha-${sourceSha}`;
      const inspected = command(exec, 'docker', [
        'buildx', 'imagetools', 'inspect', tag, '--format', '{{.Manifest.Digest}}',
      ], worktreePath, `Could not resolve ${tag}`);
      const digest = inspected.stdout.trim();
      if (!DIGEST.test(digest)) fail(`Registry returned an invalid digest for ${tag}.`);

      const verified = await verifyImage(
        `${REGISTRY}/${repository}@${digest}`, fetchImpl);
      if (verified.sourceSha !== sourceSha || verified.sourceTree !== sourceTree) {
        fail(`Image ${repository} does not match source ${sourceSha} and tree ${sourceTree}.`);
      }
      images[repository] = digest;
    }

    const imageManifest = path.join(worktreePath, `.deploy-from-commit-${randomUUID()}.json`);
    writeFileSync(imageManifest, JSON.stringify({ images }, null, 2), { flag: 'wx' });
    const deployKustomize = overrides.deployKustomize ?? (await import(pathToFileURL(
      path.join(worktreePath, 'scripts', 'release', 'deploy-kustomize.mjs'),
    ).href)).deployKustomize;
    result = await deployKustomize(
      { imageManifest }, { root: worktreePath });
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
