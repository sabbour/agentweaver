import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { IMAGE_REPOSITORIES } from '../image-repositories.mjs';
import {
  DEPLOY_FROM_COMMIT_USAGE,
  deployFromCommit,
  parseDeployFromCommitArgs,
} from '../deploy-from-commit.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const sourceSha = 'a'.repeat(40);
const sourceTree = 'b'.repeat(40);
const digestFor = index => `sha256:${index.toString(16).padStart(64, '0')}`;

function checkoutState() {
  return [
    execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }),
    execFileSync('git', ['rev-parse', '--abbrev-ref', 'HEAD'], { cwd: root, encoding: 'utf8' }),
    execFileSync('git', ['status', '--porcelain=v1', '--untracked-files=no'],
      { cwd: root, encoding: 'utf8' }),
  ];
}

function setup({ ancestor = true, mismatch, addFailure = false, cleanupFailure = false } = {}) {
  const testRoot = path.join(root, `.deploy-from-commit-test-${randomUUID()}`);
  const worktreePath = path.join(testRoot, 'source');
  mkdirSync(testRoot);
  const calls = [];
  const digests = Object.fromEntries(IMAGE_REPOSITORIES.map((repository, index) =>
    [repository, digestFor(index + 1)]));
  const exec = (name, args, options = {}) => {
    calls.push({ name, args, cwd: options.cwd });
    if (name === 'git' && args[0] === 'fetch') return { status: 0, stdout: '', stderr: '' };
    if (name === 'git' && args[0] === 'merge-base') {
      return { status: ancestor ? 0 : 1, stdout: '', stderr: '' };
    }
    if (name === 'git' && args[0] === 'rev-parse') {
      return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
    }
    if (name === 'git' && args[0] === 'worktree' && args[1] === 'add') {
      mkdirSync(worktreePath);
      if (addFailure) return { status: 1, stdout: '', stderr: 'simulated add failure' };
      return { status: 0, stdout: '', stderr: '' };
    }
    if (name === 'git' && args[0] === 'worktree' && args[1] === 'remove') {
      if (cleanupFailure) return { status: 1, stdout: '', stderr: 'simulated removal failure' };
      rmSync(worktreePath, { recursive: true, force: true });
      return { status: 0, stdout: '', stderr: '' };
    }
    if (name === 'npm' && args[0] === 'ci') {
      assert.equal(options.cwd, worktreePath);
      return { status: 0, stdout: '', stderr: '' };
    }
    if (name === 'docker' && args[0] === 'buildx') {
      assert.equal(options.cwd, worktreePath);
      const repository = args[3].slice('ghcr.io/sabbour/'.length).split(':')[0];
      return { status: 0, stdout: `${digests[repository]}\n`, stderr: '' };
    }
    assert.fail(`unexpected ${name} ${args.join(' ')}`);
  };
  const verifiedImages = [];
  const verifyImage = async image => {
    verifiedImages.push(image);
    const repository = image.slice('ghcr.io/sabbour/'.length).split('@')[0];
    return {
      sourceSha: mismatch === 'sha' && repository === IMAGE_REPOSITORIES[0]
        ? 'c'.repeat(40) : sourceSha,
      sourceTree: mismatch === 'tree' && repository === IMAGE_REPOSITORIES[0]
        ? 'd'.repeat(40) : sourceTree,
    };
  };
  let deployment;
  const deployKustomize = async (options, overrides) => {
    deployment = {
      root: overrides.root,
      images: JSON.parse(readFileSync(options.imageManifest, 'utf8')).images,
    };
  };
  return {
    testRoot, worktreePath, calls, digests, verifiedImages, exec, verifyImage,
    deployKustomize, get deployment() { return deployment; },
  };
}

function runDeploy(fixture) {
  return deployFromCommit({ sourceSha }, {
    root,
    worktreePath: fixture.worktreePath,
    exec: fixture.exec,
    verifyImage: fixture.verifyImage,
    deployKustomize: fixture.deployKustomize,
  });
}

test('requires one lowercase full commit SHA and explicit execution', () => {
  assert.deepEqual(parseDeployFromCommitArgs([sourceSha, '--execute']), { sourceSha });
  assert.match(DEPLOY_FROM_COMMIT_USAGE,
    /^Usage: npm run azure:deploy-from-commit -- /);
  const scripts = JSON.parse(readFileSync(path.join(root, 'package.json'), 'utf8')).scripts;
  assert.equal(scripts['azure:deploy-from-commit'], 'node scripts/release/deploy-from-commit.mjs');
  assert.deepEqual(Object.keys(scripts).filter(name => name.endsWith('from-commit')),
    ['azure:deploy-from-commit']);
  for (const args of [
    ['A'.repeat(40), '--execute'],
    [`${sourceSha}0`, '--execute'],
    [sourceSha],
    [sourceSha, '--execute', '--execute'],
  ]) {
    assert.throws(() => parseDeployFromCommitArgs(args), /Usage:/);
  }
});

test('fetches v1, verifies all image labels, passes only digests, and leaves the caller checkout unchanged', async () => {
  const before = checkoutState();
  const fixture = setup();
  try {
    await runDeploy(fixture);
    assert.equal(fixture.verifiedImages.length, 10);
    assert.deepEqual(fixture.verifiedImages, IMAGE_REPOSITORIES.map((repository, index) =>
      `ghcr.io/sabbour/${repository}@${fixture.digests[repository]}`));
    assert.deepEqual(Object.keys(fixture.deployment.images), IMAGE_REPOSITORIES);
    assert.deepEqual(fixture.deployment.images, fixture.digests);
    assert.equal(fixture.deployment.root, fixture.worktreePath);
    assert.ok(fixture.calls.findIndex(({ name, args }) => name === 'npm' && args[0] === 'ci') <
      fixture.calls.findIndex(({ name }) => name === 'docker'));
    assert.deepEqual(fixture.calls.find(({ name, args }) => name === 'git' && args[0] === 'fetch').args,
      ['fetch', '--no-tags', 'origin', '+refs/heads/v1:refs/remotes/origin/v1']);
    assert.deepEqual(fixture.calls.find(({ name, args }) => name === 'git' && args[0] === 'worktree' &&
      args[1] === 'add').args.slice(2), ['--detach', fixture.worktreePath, sourceSha]);
    assert.equal(fixture.calls.some(({ name, args }) =>
      name === 'git' && ['checkout', 'switch', 'reset'].includes(args[0])), false);
    assert.equal(fixture.calls.find(({ name, args }) => name === 'git' && args[0] === 'fetch').cwd, root);
    assert.equal(fixture.calls.find(({ name, args }) => name === 'git' && args[0] === 'worktree' &&
      args[1] === 'remove').args[2], '--force');
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
  }
  assert.deepEqual(checkoutState(), before);
});

test('rejects a commit outside origin/v1 before creating a worktree', async () => {
  const fixture = setup({ ancestor: false });
  try {
    await assert.rejects(runDeploy(fixture), /must be an ancestor of the current origin\/v1/);
    assert.equal(fixture.calls.some(({ args }) => args[0] === 'worktree'), false);
    assert.equal(fixture.calls.some(({ name }) => name === 'npm'), false);
    assert.equal(fixture.deployment, undefined);
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
  }
});

test('removes a partially created worktree when git worktree add fails', async () => {
  const fixture = setup({ addFailure: true });
  try {
    await assert.rejects(runDeploy(fixture), /simulated add failure/);
    assert.equal(fixture.calls.some(({ args }) => args[0] === 'worktree' &&
      args[1] === 'remove'), true);
    assert.equal(existsSync(fixture.worktreePath), false);
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
  }
});

test('rejects an image with a different source SHA or source tree and removes the worktree', async t => {
  for (const mismatch of ['sha', 'tree']) {
    await t.test(mismatch, async () => {
      const fixture = setup({ mismatch });
      try {
        await assert.rejects(runDeploy(fixture), /does not match source/);
        assert.equal(fixture.deployment, undefined);
        assert.equal(fixture.calls.some(({ args }) => args[0] === 'worktree' &&
          args[1] === 'remove'), true);
        assert.equal(fixture.verifiedImages.length, 1);
      } finally {
        rmSync(fixture.testRoot, { recursive: true, force: true });
      }
    });
  }
});

test('reports failed worktree cleanup and leaves the directory in place', async () => {
  const fixture = setup({ cleanupFailure: true });
  try {
    await assert.rejects(runDeploy(fixture), error =>
      error.message.includes('Could not remove temporary worktree') &&
      error.message.includes('simulated removal failure') &&
      error.message.includes(fixture.worktreePath));
    assert.equal(fixture.deployment.root, fixture.worktreePath);
    assert.equal(fixture.calls.some(({ args }) => args[0] === 'worktree' &&
      args[1] === 'remove'), true);
    assert.equal(fixture.calls.some(({ name, args }) =>
      name === 'rm' || name === 'rmdir' || args[0] === 'clean'), false);
    assert.equal(existsSync(fixture.worktreePath), true);
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
  }
});
