import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
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

function setup({
  ancestor = true, mismatch, addFailure = false, cleanupFailure = false,
  registry = 'ghcr.io/sabbour',
} = {}) {
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
      const repository = args[3].slice(`${registry}/`.length).split(':')[0];
      return { status: 0, stdout: `${digests[repository]}\n`, stderr: '' };
    }
    assert.fail(`unexpected ${name} ${args.join(' ')}`);
  };
  const verifiedImages = [];
  const verifyImage = async image => {
    verifiedImages.push(image);
    const repository = image.slice(`${registry}/`.length).split('@')[0];
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
      registry: options.registry,
      modelBindings: options.modelBindings,
      removeModelBindings: options.removeModelBindings,
      resourceGroup: options.resourceGroup,
      images: JSON.parse(readFileSync(options.imageManifest, 'utf8')).images,
    };
  };
  return {
    testRoot, worktreePath, calls, digests, verifiedImages, exec, verifyImage,
    deployKustomize, get deployment() { return deployment; },
  };
}

function runDeploy(fixture, registry = 'ghcr.io/sabbour', registryAwareHelper = true,
  modelOptions = {}, modelAwareHelper = true, targetAwareHelper = true) {
  return deployFromCommit({ sourceSha, registry, ...modelOptions }, {
    root,
    worktreePath: fixture.worktreePath,
    exec: fixture.exec,
    verifyImage: fixture.verifyImage,
    deployKustomize: fixture.deployKustomize,
    deployModule: {
      DEPLOY_KUSTOMIZE_REGISTRY_OPTION: registryAwareHelper,
      DEPLOY_KUSTOMIZE_MODEL_BINDINGS_OPTION: modelAwareHelper,
      DEPLOY_KUSTOMIZE_TARGET_OPTION: targetAwareHelper,
    },
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
    [sourceSha, '--registry', 'bad', '--execute'],
    [sourceSha, '--registry', 'ghcr.io/sabbour', '--registry', 'agentweaverasabbour.azurecr.io', '--execute'],
  ]) {
    assert.throws(() => parseDeployFromCommitArgs(args), /Usage:|registry must/);
  }
  assert.deepEqual(parseDeployFromCommitArgs([
    sourceSha, '--registry', 'agentweaverasabbour.azurecr.io', '--execute',
  ]), { sourceSha, registry: 'agentweaverasabbour.azurecr.io' });
});

test('passes an absolute model bindings file and named removals to the exact-source helper', async () => {
  assert.deepEqual(parseDeployFromCommitArgs([
    sourceSha, '--model-bindings', 'bindings.json', '--remove-model-binding', 'old', '--execute',
  ]), { sourceSha, modelBindings: path.resolve('bindings.json'), removeModelBindings: ['old'] });
  for (const args of [
    [sourceSha, '--model-bindings', '--execute'],
    [sourceSha, '--model-bindings', 'a.json', '--model-bindings', 'b.json', '--execute'],
    [sourceSha, '--remove-model-binding', 'old', '--remove-model-binding', 'old', '--execute'],
  ]) {
    assert.throws(() => parseDeployFromCommitArgs(args), /Usage:/);
  }

  const fixture = setup();
  const bindings = path.join(fixture.testRoot, 'bindings.json');
  writeFileSync(bindings, '{}');
  try {
    await runDeploy(fixture, undefined, true, { modelBindings: bindings, removeModelBindings: ['old'] });
    assert.equal(fixture.deployment.modelBindings, bindings);
    assert.deepEqual(fixture.deployment.removeModelBindings, ['old']);
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
  }
});

test('passes a registered separate target and rejects it for an older Kustomize helper', async () => {
  assert.deepEqual(parseDeployFromCommitArgs([sourceSha, '--resource-group', 'awwus2np01', '--execute']),
    { sourceSha, resourceGroup: 'awwus2np01' });
  assert.deepEqual(parseDeployFromCommitArgs([sourceSha, '--resource-group', 'agentweaver', '--execute']),
    { sourceSha });
  for (const args of [
    [sourceSha, '--resource-group', 'unknown', '--execute'],
    [sourceSha, '--resource-group', '--execute'],
    [sourceSha, '--resource-group', 'awwus2np01', '--resource-group', 'awwus2np01', '--execute'],
  ]) {
    assert.throws(() => parseDeployFromCommitArgs(args), /Usage:/);
  }
  const fixture = setup();
  try {
    await runDeploy(fixture, undefined, true, { resourceGroup: 'awwus2np01' });
    assert.equal(fixture.deployment.resourceGroup, 'awwus2np01');
    const older = setup();
    try {
      await assert.rejects(runDeploy(older, undefined, true, { resourceGroup: 'awwus2np01' }, true, false),
        /predates install target selection/);
      assert.equal(older.deployment, undefined);
    } finally {
      rmSync(older.testRoot, { recursive: true, force: true });
    }
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
  }
});

test('rejects model binding options when the selected source has an older Kustomize helper', async () => {
  const fixture = setup();
  try {
    await assert.rejects(runDeploy(fixture, undefined, true, { removeModelBindings: ['old'] }, false),
      /predates the Kustomize model binding options/);
    assert.equal(fixture.deployment, undefined);
    await assert.rejects(runDeploy(fixture, undefined, true,
      { modelBindings: path.join(fixture.testRoot, 'missing.json') }), /model bindings file does not exist/);
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
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

test('selects ACR images and passes the registry to the exact-source helper', async () => {
  const registry = 'agentweaverasabbour.azurecr.io';
  const fixture = setup({ registry });
  try {
    await runDeploy(fixture, registry);
    assert.deepEqual(fixture.verifiedImages, IMAGE_REPOSITORIES.map(repository =>
      `${registry}/${repository}@${fixture.digests[repository]}`));
    assert.equal(fixture.deployment.registry, registry);
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
  }
});

test('rejects ACR deployment when the selected source has an older Kustomize helper', async () => {
  const registry = 'agentweaverasabbour.azurecr.io';
  const fixture = setup({ registry });
  try {
    await assert.rejects(runDeploy(fixture, registry, false),
      /predates registry-aware Kustomize deployment/);
    assert.equal(fixture.deployment, undefined);
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
  }
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
