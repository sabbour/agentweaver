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
import { sourceSupportsConsumeGuard, sourceSupportsPairAVerifier } from '../deploy-kustomize.mjs';
import {
  parseProjectsConfigMigrationArgs,
  runProjectsConfigMigration,
} from '../../azure/lib/projects-config-migration.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const realPostgresPlanJson = readFileSync(path.join(root, 'scripts', 'azure', 'lib', 'p1-postgres-plan.json'), 'utf8');
const realProgramCs = readFileSync(
  path.join(root, 'services', 'projects-config', 'Agentweaver.Projects.Config', 'Program.cs'),
  'utf8',
);
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
  registry = 'ghcr.io/sabbour', consumeGuardSupported = true, maintenanceState = {}, leaseHolder = undefined,
} = {}) {
  const testRoot = path.join(root, `.deploy-from-commit-test-${randomUUID()}`);
  const worktreePath = path.join(testRoot, 'source');
  mkdirSync(testRoot);
  const calls = [];
  let activeLeaseHolder = leaseHolder;
  const digests = Object.fromEntries(IMAGE_REPOSITORIES.map((repository, index) =>
    [repository, digestFor(index + 1)]));
  const exec = (name, args, options = {}) => {
    calls.push({ name, args, cwd: options.cwd, input: options.input });
    if (name === 'git' && args[0] === 'fetch') return { status: 0, stdout: '', stderr: '' };
    if (name === 'git' && args[0] === 'merge-base') {
      return { status: ancestor ? 0 : 1, stdout: '', stderr: '' };
    }
    if (name === 'git' && args[0] === 'rev-parse') {
      return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
    }
    if (name === 'git' && args[0] === 'cat-file' && args[1] === '-e') {
      return { status: consumeGuardSupported ? 0 : 1, stdout: '', stderr: '' };
    }
    if (name === 'git' && args[0] === 'show') {
      if (!consumeGuardSupported) return { status: 1, stdout: '', stderr: '' };
      if (args[1].endsWith(':scripts/azure/lib/p1-postgres-plan.json')) {
        return { status: 0, stdout: realPostgresPlanJson, stderr: '' };
      }
      if (args[1].endsWith(':services/projects-config/Agentweaver.Projects.Config/Program.cs')) {
        return { status: 0, stdout: realProgramCs, stderr: '' };
      }
      return { status: 0, stdout: '', stderr: '' };
    }
    if (name === 'kubectl') {
      if (args[0] === 'config' && args[1] === 'current-context') {
        return { status: 0, stdout: 'test-ctx\n', stderr: '' };
      }
      if (args[0] === 'config' && args[1] === 'view') {
        return { status: 0, stdout: 'https://test-cluster.example.com\n', stderr: '' };
      }
      if (args[0] === 'get' && args[1] === 'lease') {
        if (activeLeaseHolder) {
          if (args.includes('--output=jsonpath={.spec.holderIdentity}')) {
            return { status: 0, stdout: activeLeaseHolder, stderr: '' };
          }
          return {
            status: 0,
            stdout: JSON.stringify({
              metadata: { uid: 'lease-uid-1', resourceVersion: '1' },
              spec: { holderIdentity: activeLeaseHolder },
            }),
            stderr: '',
          };
        }
        return { status: 1, stdout: '', stderr: 'Error from server (NotFound): leases.coordination.k8s.io "p1-db-maintenance-lock" not found' };
      }
      if (args[0] === 'create' && args.includes('--filename')) {
        const doc = JSON.parse(options.input ?? '{}');
        if (doc.kind === 'Lease') {
          activeLeaseHolder = doc.spec?.holderIdentity;
          return { status: 0, stdout: 'lease.coordination.k8s.io/p1-db-maintenance-lock created', stderr: '' };
        }
      }
      if (args[0] === 'get' && args[1] === 'configmap' && args[2] === 'p1-db-maintenance-state') {
        return { status: 0, stdout: JSON.stringify({ kind: 'ConfigMap', data: maintenanceState }), stderr: '' };
      }
      if (args[0] === 'get' && (args[1] === 'jobs' || args[1] === 'pods')) {
        return { status: 0, stdout: JSON.stringify({ kind: 'List', items: [] }), stderr: '' };
      }
      if (args[0] === 'delete' && args[1] === '--raw') {
        const expectedPath = '/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/p1-db-maintenance-lock';
        if (args[2] !== expectedPath || !args.includes('--filename') || args[args.indexOf('--filename') + 1] !== '-' ||
            !args.includes('--context')) {
          return { status: 1, stdout: '', stderr: `unexpected raw delete ${args.join(' ')}` };
        }
        const body = JSON.parse(options.input ?? '{}');
        if (body.apiVersion !== 'v1' || body.kind !== 'DeleteOptions' ||
            body.preconditions?.uid !== 'lease-uid-1' ||
            body.preconditions?.resourceVersion !== '1') {
          return { status: 1, stdout: '', stderr: 'Conflict: UID or resourceVersion mismatch' };
        }
        activeLeaseHolder = undefined;
        return { status: 0, stdout: 'lease.coordination.k8s.io "p1-db-maintenance-lock" deleted', stderr: '' };
      }
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
      context: overrides.context,
      heldMaintenanceRunId: overrides.heldMaintenanceRunId,
      registry: options.registry,
      modelBindings: options.modelBindings,
      removeModelBindings: options.removeModelBindings,
      resourceGroup: options.resourceGroup,
      agentHostEgressAudit: options.agentHostEgressAudit,
      images: JSON.parse(readFileSync(options.imageManifest, 'utf8')).images,
    };
  };
  const deleteMaintenanceLease = async ({ context, apiPath, uid, resourceVersion }) => {
    assert.equal(context, 'test-ctx');
    assert.equal(apiPath, '/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/p1-db-maintenance-lock');
    if (uid !== 'lease-uid-1' || resourceVersion !== '1') {
      throw new Error('Conflict: UID or resourceVersion mismatch');
    }
    activeLeaseHolder = undefined;
  };
  return {
    testRoot, worktreePath, calls, digests, verifiedImages, exec, verifyImage,
    deployKustomize, deleteMaintenanceLease, get deployment() { return deployment; },
    get activeLeaseHolder() { return activeLeaseHolder; },
  };
}

function runDeploy(fixture, registry = 'ghcr.io/sabbour', registryAwareHelper = true,
  modelOptions = {}, modelAwareHelper = true, targetAwareHelper = true, auditAwareHelper = true,
  providerHostRequestAwareHelper = true, providerRateCardAwareHelper = true,
  consumeGuardAwareHelper = true) {
  return deployFromCommit({ sourceSha, registry, ...modelOptions }, {
    root,
    worktreePath: fixture.worktreePath,
    exec: fixture.exec,
    verifyImage: fixture.verifyImage,
    deployKustomize: fixture.deployKustomize,
    deleteMaintenanceLease: fixture.deleteMaintenanceLease,
    deployModule: {
      DEPLOY_KUSTOMIZE_REGISTRY_OPTION: registryAwareHelper,
      DEPLOY_KUSTOMIZE_MODEL_BINDINGS_OPTION: modelAwareHelper,
      DEPLOY_KUSTOMIZE_PROVIDER_HOST_REQUEST_OPTION: providerHostRequestAwareHelper,
      DEPLOY_KUSTOMIZE_PROVIDER_RATE_CARD_OPTION: providerRateCardAwareHelper,
      DEPLOY_KUSTOMIZE_TARGET_OPTION: targetAwareHelper,
      DEPLOY_KUSTOMIZE_CONTEXT_OPTION: true,
      DEPLOY_KUSTOMIZE_AUDIT_OPTION: auditAwareHelper,
      DEPLOY_KUSTOMIZE_PROJECTS_CONFIG_CONSUME_GUARD: consumeGuardAwareHelper,
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
  const awwusExec = base => (name, args, options) => {
    if (name === 'kubectl' && args[0] === 'config' && args[1] === 'view') {
      return { status: 0, stdout: 'https://awwus2np01-aks-88n4s6n7.hcp.westus2.azmk8s.io:443\n', stderr: '' };
    }
    return base.exec(name, args, options);
  };
  const fixture = setup();
  try {
    await runDeploy({ ...fixture, exec: awwusExec(fixture) }, undefined, true, { resourceGroup: 'awwus2np01' });
    assert.equal(fixture.deployment.resourceGroup, 'awwus2np01');
    const older = setup();
    try {
      await assert.rejects(runDeploy({ ...older, exec: awwusExec(older) }, undefined, true, { resourceGroup: 'awwus2np01' }, true, false),
        /predates install target selection/);
      assert.equal(older.deployment, undefined);
    } finally {
      rmSync(older.testRoot, { recursive: true, force: true });
    }
  } finally {
    rmSync(fixture.testRoot, { recursive: true, force: true });
  }
});

test('validates and forwards AgentHost egress audit mode to the exact-source helper', async () => {
  assert.deepEqual(parseDeployFromCommitArgs([
    sourceSha, '--agenthost-egress-audit', 'denied', '--execute',
  ]), { sourceSha, agentHostEgressAudit: 'denied' });
  for (const args of [
    [sourceSha, '--agenthost-egress-audit', 'all', '--execute'],
    [sourceSha, '--agenthost-egress-audit', '--execute'],
    [sourceSha, '--agenthost-egress-audit', 'off', '--agenthost-egress-audit', 'allowed', '--execute'],
  ]) {
    assert.throws(() => parseDeployFromCommitArgs(args), /AgentHost egress audit|Usage:/);
  }

  const invalid = setup();
  try {
    await assert.rejects(runDeploy(invalid, undefined, true, { agentHostEgressAudit: 'all' }),
      /AgentHost egress audit must be off, allowed, or denied/);
    assert.equal(invalid.calls.length, 0);
  } finally {
    rmSync(invalid.testRoot, { recursive: true, force: true });
  }

  const fixture = setup();
  try {
    await runDeploy(fixture, undefined, true, { agentHostEgressAudit: 'denied' });
    assert.equal(fixture.deployment.agentHostEgressAudit, 'denied');
    const older = setup();
    try {
      await assert.rejects(runDeploy(older, undefined, true, { agentHostEgressAudit: 'allowed' },
        true, true, false), /predates AgentHost egress audit support/);
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
    assert.equal(fixture.deployment.agentHostEgressAudit, 'off');
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

test('protects an already enabled provider host-request channel before invoking an older deployer even without flag', async () => {
  const setupWithKubectl = ({ liveEnabled = false, apiServer = 'https://default.example:443' } = {}) => {
    const base = setup();
    const baseExec = base.exec;
    base.exec = (name, args, options = {}) => {
      if (name === 'kubectl') {
        base.calls.push({ name, args, cwd: options.cwd });
        if (args[0] === 'config' && args[1] === 'current-context') {
          return { status: 0, stdout: 'test-ctx\n', stderr: '' };
        }
        if (args[0] === 'config' && args[1] === 'view') {
          return { status: 0, stdout: `${apiServer}\n`, stderr: '' };
        }
        if (args[0] === 'get' && args[1] === 'configmap') {
          const cmName = args[2];
          if (liveEnabled && cmName === 'projects-config-runtime-config') {
            return {
              status: 0,
              stdout: JSON.stringify({
                kind: 'ConfigMap',
                metadata: { name: cmName, namespace: 'agentweaver' },
                data: { ProjectsConfig__ProviderHostRequest__Tls__ListenerPort: '8444' },
              }),
              stderr: '',
            };
          }
          return { status: 0, stdout: '', stderr: '' };
        }
      }
      return baseExec(name, args, options);
    };
    return base;
  };

  const enabled = setupWithKubectl({ liveEnabled: true });
  try {
    await assert.rejects(
      runDeploy(enabled, undefined, true, {}, true, true, true, false),
      /live cluster has the private provider host-request channel enabled/);
    assert.equal(enabled.deployment, undefined);
  } finally {
    rmSync(enabled.testRoot, { recursive: true, force: true });
  }

  const wrongTarget = setupWithKubectl({ liveEnabled: false, apiServer: 'https://wrong-cluster.example:443' });
  try {
    await assert.rejects(
      runDeploy(wrongTarget, undefined, true, { resourceGroup: 'awwus2np01' }, true, true, true, false),
      /does not point at the awwus2np01 cluster/);
    assert.equal(wrongTarget.deployment, undefined);
  } finally {
    rmSync(wrongTarget.testRoot, { recursive: true, force: true });
  }

  const disabled = setupWithKubectl({ liveEnabled: false });
  try {
    await runDeploy(disabled, undefined, true, {}, true, true, true, false);
    assert.equal(disabled.deployment.root, disabled.worktreePath);
  } finally {
    rmSync(disabled.testRoot, { recursive: true, force: true });
  }
});

test('v9: acquires Lease first, rejects historical commit pre-worktree when consumeGuardRequired is set, and hands off heldMaintenanceRunId', async () => {
  const blocked = setup({
    consumeGuardSupported: false,
    maintenanceState: { 'projectsConfig.consumeGuardRequired': 'true' },
  });
  try {
    await assert.rejects(
      runDeploy(blocked),
      /lacks AddNativeAcsProjectPolicyBaselineConsumeGuard/);
    assert.equal(blocked.calls.some(({ name, args }) => name === 'git' && args[0] === 'worktree'), false);
    assert.equal(blocked.calls.some(({ name }) => name === 'npm'), false);
    const leaseCreateIdx = blocked.calls.findIndex(c =>
      c.name === 'kubectl' && c.args.includes('create') && JSON.parse(c.input ?? '{}').kind === 'Lease');
    const stateGetIdx = blocked.calls.findIndex(c =>
      c.name === 'kubectl' && c.args.includes('get') && c.args.includes('configmap'));
    assert.ok(leaseCreateIdx >= 0 && leaseCreateIdx < stateGetIdx);
  } finally {
    rmSync(blocked.testRoot, { recursive: true, force: true });
  }

  const concurrent = setup({ leaseHolder: 'other-run-id' });
  try {
    await assert.rejects(runDeploy(concurrent), /database maintenance lock is held by other-run-id/);
    assert.equal(concurrent.calls.some(({ name, args }) => name === 'git' && args[0] === 'worktree'), false);
  } finally {
    rmSync(concurrent.testRoot, { recursive: true, force: true });
  }

  const ok = setup({ maintenanceState: { 'projectsConfig.consumeGuardRequired': 'true' } });
  try {
    await runDeploy(ok);
    assert.match(ok.deployment.heldMaintenanceRunId, /^[a-f0-9]{12}$/);
  } finally {
    rmSync(ok.testRoot, { recursive: true, force: true });
  }

  // Finding 6: migration-capable source with NO markers still requires DEPLOY_KUSTOMIZE_PROJECTS_CONFIG_CONSUME_GUARD.
  const noMarkersOlderDeployer = setup({ maintenanceState: {} });
  const preWorktreeExec = (name, args, options) => {
    if (name === 'git' && args[0] === 'show' && args[1].endsWith(':scripts/release/deploy-kustomize.mjs')) {
      return { status: 0, stdout: 'export const DEPLOY_KUSTOMIZE_REGISTRY_OPTION = true;\n', stderr: '' };
    }
    return noMarkersOlderDeployer.exec(name, args, options);
  };
  try {
    await assert.rejects(
      deployFromCommit({ sourceSha, registry: 'ghcr.io/sabbour' }, {
        root,
        worktreePath: noMarkersOlderDeployer.worktreePath,
        exec: preWorktreeExec,
        verifyImage: noMarkersOlderDeployer.verifyImage,
      }),
      /predates Projects & Config consume-guard deployment support/);
    assert.equal(noMarkersOlderDeployer.calls.some(({ name, args }) => name === 'git' && args[0] === 'worktree'), false);
  } finally {
    rmSync(noMarkersOlderDeployer.testRoot, { recursive: true, force: true });
  }

  // Finding 7: changed Lease UID/resourceVersion preserves new Lease.
  const changedLease = setup({ maintenanceState: { 'projectsConfig.consumeGuardRequired': 'true' } });
  const changedDeleteLease = async () => {
    throw new Error('Kubernetes rejected the version-guarded p1-db-maintenance-lock delete (HTTP 409).');
  };
  try {
    await assert.rejects(
      runDeploy({ ...changedLease, deleteMaintenanceLease: changedDeleteLease }),
      /Could not release p1-db-maintenance-lock/);
  } finally {
    rmSync(changedLease.testRoot, { recursive: true, force: true });
  }

  // Blocker 3: inner deployKustomize retain-Lease decision propagates to outer Lease owner.
  const retainInner = setup({ maintenanceState: { 'projectsConfig.consumeGuardRequired': 'true' } });
  let deleteCalled = false;
  try {
    await assert.rejects(
      deployFromCommit({ sourceSha, registry: 'ghcr.io/sabbour' }, {
        root,
        worktreePath: retainInner.worktreePath,
        exec: retainInner.exec,
        verifyImage: retainInner.verifyImage,
        deleteMaintenanceLease: async () => { deleteCalled = true; },
        deployKustomize: async (_opts, ov) => {
          assert.equal(ov.context, 'test-ctx');
          ov.onRetainLease?.();
          throw new Error('inner job cleanup failed');
        },
        deployModule: {
          DEPLOY_KUSTOMIZE_PROVIDER_HOST_REQUEST_OPTION: true,
          DEPLOY_KUSTOMIZE_PROVIDER_RATE_CARD_OPTION: true,
          DEPLOY_KUSTOMIZE_PROJECTS_CONFIG_CONSUME_GUARD: true,
        },
      }),
      /retaining p1-db-maintenance-lock.*--release-lock/);
    assert.equal(deleteCalled, false, 'outer Lease must not be released when inner deploy retains it');
  } finally {
    rmSync(retainInner.testRoot, { recursive: true, force: true });
  }

  // Blocker 7: --projects-config-only parses and resolves only agentweaver.projects.config.
  assert.deepEqual(
    parseDeployFromCommitArgs([sourceSha, '--projects-config-only', '--resource-group', 'awwus2np01', '--execute']),
    { sourceSha, projectsConfigOnly: true, resourceGroup: 'awwus2np01' },
  );
  assert.throws(
    () => parseDeployFromCommitArgs([sourceSha, '--projects-config-only', '--projects-config-only', '--execute']),
    /Usage:/,
  );
  const pcOnly = setup({ maintenanceState: { 'projectsConfig.consumeGuardRequired': 'true' } });
  try {
    await runDeploy(pcOnly, 'ghcr.io/sabbour', true, { projectsConfigOnly: true });
    assert.deepEqual(Object.keys(pcOnly.deployment.images), ['agentweaver.projects.config']);
    assert.equal(pcOnly.deployment.context, 'test-ctx');
  } finally {
    rmSync(pcOnly.testRoot, { recursive: true, force: true });
  }

  // Gap 1: historical deployKustomize without DEPLOY_KUSTOMIZE_CONTEXT_OPTION gets a scoped KUBECONFIG.
  const hist = setup({ maintenanceState: {} });
  const prevKubeconfig = process.env.KUBECONFIG;
  let observedKubeconfig;
  let observedKubeconfigText;
  const histExec = (name, args, options) => {
    if (name === 'kubectl' && args[0] === 'config' && args[1] === 'view' && args.includes('--raw')) {
      assert.ok(args.includes('--minify') && args.includes('--context') && args.includes('test-ctx'));
      return { status: 0, stdout: 'apiVersion: v1\nkind: Config\ncurrent-context: test-ctx\n', stderr: '' };
    }
    return hist.exec(name, args, options);
  };
  try {
    await deployFromCommit({ sourceSha, registry: 'ghcr.io/sabbour' }, {
      root,
      worktreePath: hist.worktreePath,
      exec: histExec,
      verifyImage: hist.verifyImage,
      deleteMaintenanceLease: hist.deleteMaintenanceLease,
      deployKustomize: async () => {
        observedKubeconfig = process.env.KUBECONFIG;
        observedKubeconfigText = readFileSync(observedKubeconfig, 'utf8');
      },
      deployModule: {
        DEPLOY_KUSTOMIZE_REGISTRY_OPTION: true,
        DEPLOY_KUSTOMIZE_PROVIDER_HOST_REQUEST_OPTION: true,
        DEPLOY_KUSTOMIZE_PROVIDER_RATE_CARD_OPTION: true,
        DEPLOY_KUSTOMIZE_PROJECTS_CONFIG_CONSUME_GUARD: true,
      },
    });
    assert.ok(observedKubeconfig && observedKubeconfig !== prevKubeconfig);
    assert.match(observedKubeconfigText, /current-context: test-ctx/);
    assert.equal(existsSync(observedKubeconfig), false, 'scoped kubeconfig must be removed after deploy');
    assert.equal(process.env.KUBECONFIG, prevKubeconfig);
  } finally {
    rmSync(hist.testRoot, { recursive: true, force: true });
  }

  // Gap 2: Pair A verifier Job foreground delete failure retains Lease and stops progression.
  const migrationClientId = '11111111-2222-4333-8444-555555555555';
  const pairADelFail = setup({ consumeGuardSupported: false, maintenanceState: {} });
  const verifierSha = 'e'.repeat(40);
  const verifierTree = 'f'.repeat(40);
  const verifierDigest = digestFor(9);
  const verifierImage = `ghcr.io/sabbour/agentweaver.projects.config@${verifierDigest}`;
  let pairALeaseDeleted = false;
  const pairADelExec = (name, args, options) => {
    if (name === 'git' && args[0] === 'rev-parse' && args[1] === '--verify' &&
        (args[2] === 'refs/remotes/origin/v1' || args[2] === 'refs/remotes/origin/v1^{tree}' ||
          args[2] === `${verifierSha}^{tree}`)) {
      return { status: 0, stdout: args[2] === 'refs/remotes/origin/v1' ? `${verifierSha}\n` : `${verifierTree}\n`, stderr: '' };
    }
    if (name === 'git' && args[0] === 'cat-file' && args[1] === '-e') {
      return { status: args[2].startsWith(verifierSha) ? 0 : 1, stdout: '', stderr: '' };
    }
    if (name === 'git' && args[0] === 'show') {
      if (!args[1].startsWith(verifierSha)) return { status: 1, stdout: '', stderr: '' };
      return {
        status: 0,
        stdout: args[1].endsWith(':scripts/azure/lib/p1-postgres-plan.json') ? realPostgresPlanJson : realProgramCs,
        stderr: '',
      };
    }
    if (name === 'docker' && args[0] === 'buildx' && args[3].includes(verifierSha)) {
      return { status: 0, stdout: `${verifierDigest}\n`, stderr: '' };
    }
    if (name === 'kubectl' && args[0] === 'get' && args[1] === 'deployment') return { status: 0, stdout: '', stderr: '' };
    if (name === 'kubectl' && args[0] === 'get' && args[1] === 'serviceaccount') {
      return {
        status: 0,
        stdout: JSON.stringify({
          kind: 'ServiceAccount',
          metadata: { name: 'projects-config-migration', annotations: { 'azure.workload.identity/client-id': migrationClientId } },
        }),
        stderr: '',
      };
    }
    if (name === 'az' && args[0] === 'identity') return { status: 0, stdout: `${migrationClientId}\n`, stderr: '' };
    if (name === 'kubectl' && args[0] === 'create' && args.includes('--filename')) {
      const doc = JSON.parse(options.input ?? '{}');
      if (doc.kind === 'Job') return { status: 0, stdout: 'job created', stderr: '' };
    }
    if (name === 'kubectl' && args[0] === 'wait') return { status: 0, stdout: '', stderr: '' };
    if (name === 'kubectl' && args[0] === 'logs') return { status: 0, stdout: 'PROJECTS_CONFIG_EXACT_PAIR_A\n', stderr: '' };
    if (name === 'kubectl' && args[0] === 'delete' && args[1] === 'job') {
      return { status: 1, stdout: '', stderr: 'delete paira job failed' };
    }
    return pairADelFail.exec(name, args, options);
  };
  try {
    await assert.rejects(
      deployFromCommit({ sourceSha, registry: 'ghcr.io/sabbour' }, {
        root,
        worktreePath: pairADelFail.worktreePath,
        exec: pairADelExec,
        verifyImage: async img => img === verifierImage ? { sourceSha: verifierSha, sourceTree: verifierTree } : pairADelFail.verifyImage(img),
        deleteMaintenanceLease: async () => { pairALeaseDeleted = true; },
        deployKustomize: pairADelFail.deployKustomize,
      }),
      /retaining p1-db-maintenance-lock/);
    assert.equal(pairALeaseDeleted, false);
    assert.equal(pairADelFail.deployment, undefined);
  } finally {
    rmSync(pairADelFail.testRoot, { recursive: true, force: true });
  }

  // Blocker 2: wrong target context fails before any Lease, state, or verifier Job operations.
  const wrongCtx = setup({ maintenanceState: {} });
  const wrongCtxExec = (name, args, options) => {
    if (name === 'kubectl' && args[0] === 'config' && args[1] === 'view') {
      return { status: 0, stdout: 'https://awwus2np01-aks-88n4s6n7.hcp.westus2.azmk8s.io:443\n', stderr: '' };
    }
    return wrongCtx.exec(name, args, options);
  };
  try {
    await assert.rejects(
      deployFromCommit({ sourceSha, registry: 'ghcr.io/sabbour' }, {
        root,
        worktreePath: wrongCtx.worktreePath,
        exec: wrongCtxExec,
        verifyImage: wrongCtx.verifyImage,
      }),
      /does not point at the agentweaver cluster/);
    assert.equal(wrongCtx.calls.some(({ name, args }) =>
      name === 'kubectl' && (args.includes('lease') || args.includes('configmap') || args.includes('job'))), false);
  } finally {
    rmSync(wrongCtx.testRoot, { recursive: true, force: true });
  }

  // Finding 4 / Blocker 3: production Pair A Job path on an old Pair A installation uses configured Job template,
  // source-pinned verifier image from origin/v1, verified workload identity, and PostgreSQL connection string.
  for (const exact of [true, false]) {
    const prodPairA = setup({ consumeGuardSupported: false, maintenanceState: {} });
    const oldPairAImage = `ghcr.io/sabbour/agentweaver.projects.config@${digestFor(8)}`;
    const verifierSha = 'e'.repeat(40);
    const verifierTree = 'f'.repeat(40);
    const verifierDigest = digestFor(9);
    const verifierImage = `ghcr.io/sabbour/agentweaver.projects.config@${verifierDigest}`;
    const createdJobs = [];
    const prodExec = (name, args, options) => {
      if (name === 'git' && args[0] === 'rev-parse' && args[1] === '--verify' && args[2] === 'refs/remotes/origin/v1') {
        return { status: 0, stdout: `${verifierSha}\n`, stderr: '' };
      }
      if (name === 'git' && args[0] === 'rev-parse' && args[1] === '--verify' &&
          (args[2] === 'refs/remotes/origin/v1^{tree}' || args[2] === `${verifierSha}^{tree}`)) {
        return { status: 0, stdout: `${verifierTree}\n`, stderr: '' };
      }
      if (name === 'git' && args[0] === 'cat-file' && args[1] === '-e') {
        return { status: args[2].startsWith(verifierSha) ? 0 : 1, stdout: '', stderr: '' };
      }
      if (name === 'git' && args[0] === 'show') {
        if (!args[1].startsWith(verifierSha)) return { status: 1, stdout: '', stderr: '' };
        return {
          status: 0,
          stdout: args[1].endsWith(':scripts/azure/lib/p1-postgres-plan.json') ? realPostgresPlanJson : realProgramCs,
          stderr: '',
        };
      }
      if (name === 'docker' && args[0] === 'buildx' && args[3] === `ghcr.io/sabbour/agentweaver.projects.config:sha-${verifierSha}`) {
        return { status: 0, stdout: `${verifierDigest}\n`, stderr: '' };
      }
      if (name === 'kubectl' && args[0] === 'get' && args[1] === 'deployment' && args[2] === 'projects-config') {
        return {
          status: 0,
          stdout: JSON.stringify({
            spec: { template: { spec: { containers: [{ image: oldPairAImage }] } } },
          }),
          stderr: '',
        };
      }
      if (name === 'kubectl' && args[0] === 'get' && args[1] === 'serviceaccount' && args[2] === 'projects-config-migration') {
        assert.ok(args.includes('--context') && args.includes('test-ctx'));
        return {
          status: 0,
          stdout: JSON.stringify({
            kind: 'ServiceAccount',
            metadata: {
              name: 'projects-config-migration',
              annotations: { 'azure.workload.identity/client-id': migrationClientId },
            },
          }),
          stderr: '',
        };
      }
      if (name === 'az' && args[0] === 'identity' && args[1] === 'show') {
        return { status: 0, stdout: `${migrationClientId}\n`, stderr: '' };
      }
      if (name === 'kubectl' && args[0] === 'create' && args.includes('--filename')) {
        assert.ok(args.includes('--context') && args.includes('test-ctx'));
        const doc = JSON.parse(options.input ?? '{}');
        if (doc.kind === 'Job') {
          createdJobs.push(doc);
          return { status: 0, stdout: 'job created', stderr: '' };
        }
      }
      if (name === 'kubectl' && args[0] === 'wait') return { status: 0, stdout: '', stderr: '' };
      if (name === 'kubectl' && args[0] === 'logs') {
        return { status: 0, stdout: exact ? 'PROJECTS_CONFIG_EXACT_PAIR_A\n' : 'PROJECTS_CONFIG_PAIR_A_NOT_PROVEN:drift\n', stderr: '' };
      }
      if (name === 'kubectl' && args[0] === 'delete' && args[1] === 'job') {
        assert.ok(args.includes('--context') && args.includes('test-ctx'));
        return { status: 0, stdout: 'job deleted', stderr: '' };
      }
      return prodPairA.exec(name, args, options);
    };
    const prodVerifyImage = async img => img === verifierImage
      ? { sourceSha: verifierSha, sourceTree: verifierTree }
      : prodPairA.verifyImage(img);
    try {
      if (exact) {
        await runDeploy({ ...prodPairA, exec: prodExec, verifyImage: prodVerifyImage });
        assert.ok(prodPairA.deployment);
      } else {
        await assert.rejects(
          runDeploy({ ...prodPairA, exec: prodExec, verifyImage: prodVerifyImage }),
          /exact Pair A could not be verified/);
      }
      assert.equal(createdJobs.length, 1);
      const job = createdJobs[0];
      const container = job.spec.template.spec.containers[0];
      const env = Object.fromEntries((container.env ?? []).map(e => [e.name, e.value]));
      assert.equal(container.image, verifierImage);
      assert.deepEqual(container.command, [
        'dotnet', 'Agentweaver.Projects.Config.dll', '--verify-projects-config-pair-a',
      ]);
      assert.equal(container.args, undefined);
      assert.equal(job.spec.template.spec.serviceAccountName, 'projects-config-migration');
      assert.equal(job.spec.template.metadata.labels['azure.workload.identity/use'], 'true');
      assert.equal(env.ProjectsConfig__Migration__WorkloadIdentity__ClientId, migrationClientId);
      assert.match(env.ConnectionStrings__ProjectsConfigMigration, /SSL Mode=VerifyFull/);
      assert.ok(env.ProjectsConfig__Migration__WorkloadIdentity__TenantId);
      assert.ok(env.ProjectsConfig__Migration__WorkloadIdentity__TokenFilePath);
      const outerKubectlCalls = prodPairA.calls.filter(c =>
        c.name === 'kubectl' && c.args[0] !== 'config');
      assert.ok(outerKubectlCalls.every(c => c.args.includes('--context') && c.args.includes('test-ctx')));
    } finally {
      rmSync(prodPairA.testRoot, { recursive: true, force: true });
    }
  }

  // Fix 1: verify compatibility and verifier availability against actual repo source files (HEAD).
  const headSha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
  const headTree = execFileSync('git', ['rev-parse', 'HEAD^{tree}'], { cwd: root, encoding: 'utf8' }).trim();
  const realGitExec = (cmd, args, opts) => {
    const res = execFileSync(cmd, args, { cwd: opts?.cwd ?? root, encoding: 'utf8' });
    return { status: 0, stdout: res, stderr: '' };
  };
  assert.equal(sourceSupportsConsumeGuard(realGitExec, root, headSha, headTree), true);
  assert.equal(sourceSupportsPairAVerifier((cmd, args, opts) => {
    if (cmd === 'git' && args[0] === 'show' &&
        args[1].endsWith(':services/projects-config/Agentweaver.Projects.Config/Program.cs')) {
      return { status: 0, stdout: realProgramCs, stderr: '' };
    }
    return realGitExec(cmd, args, opts);
  }, root, headSha, headTree), true);
  assert.equal(sourceSupportsPairAVerifier((cmd, args, opts) => {
    if (cmd === 'git' && args[0] === 'show' &&
        args[1].endsWith(':services/projects-config/Agentweaver.Projects.Config/Program.cs')) {
      return { status: 0, stdout: realProgramCs.replaceAll('--verify-projects-config-pair-a', '--other'), stderr: '' };
    }
    return realGitExec(cmd, args, opts);
  }, root, headSha, headTree), false);

  // Finding 3 + Residual 2: historical deployer lost wait with active unlabeled migration Job retains Lease,
  // and manual --release-lock also refuses until the unlabeled Job settles, then succeeds.
  const histActive = setup({ maintenanceState: {} });
  let histActiveLeaseDeleted = false;
  let unlabeledJobComplete = false;
  const histActiveExec = (name, args, options) => {
    if (name === 'kubectl' && args[0] === 'config' && args[1] === 'view' && args.includes('--raw')) {
      return { status: 0, stdout: 'apiVersion: v1\nkind: Config\ncurrent-context: test-ctx\n', stderr: '' };
    }
    if (name === 'kubectl' && args.includes('get') && args.includes('jobs') &&
        !args.some(a => a.includes('agentweaver.io/db-maintenance-run='))) {
      return {
        status: 0,
        stdout: JSON.stringify({
          kind: 'List',
          items: [{
            metadata: { name: 'identity-broker-migration-12345678' },
            status: unlabeledJobComplete
              ? { conditions: [{ type: 'Complete', status: 'True' }] }
              : { active: 1 },
          }],
        }),
        stderr: '',
      };
    }
    if (name === 'kubectl' && args.includes('get') && args.includes('pods') &&
        !args.some(a => a.includes('agentweaver.io/db-maintenance-run='))) {
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items: [] }), stderr: '' };
    }
    return histActive.exec(name, args, options);
  };
  try {
    await assert.rejects(
      deployFromCommit({ sourceSha, registry: 'ghcr.io/sabbour' }, {
        root,
        worktreePath: histActive.worktreePath,
        exec: histActiveExec,
        verifyImage: histActive.verifyImage,
        deleteMaintenanceLease: async () => { histActiveLeaseDeleted = true; },
        deployKustomize: async () => { throw new Error('historical wait disconnected'); },
        deployModule: {
          DEPLOY_KUSTOMIZE_REGISTRY_OPTION: true,
          DEPLOY_KUSTOMIZE_PROVIDER_HOST_REQUEST_OPTION: true,
          DEPLOY_KUSTOMIZE_PROVIDER_RATE_CARD_OPTION: true,
          DEPLOY_KUSTOMIZE_PROJECTS_CONFIG_CONSUME_GUARD: true,
        },
      }),
      /Cannot release p1-db-maintenance-lock|cannot release p1-db-maintenance-lock/i);
    assert.equal(histActiveLeaseDeleted, false);

    const heldRunId = histActive.activeLeaseHolder;
    assert.ok(heldRunId);
    const receiptDir = path.join(histActive.testRoot, 'release-lock-receipts');
    const releaseOverrides = {
      root: path.join(histActive.testRoot, 'checkout'),
      exec: (cmd, args, opts) => {
        if (cmd !== 'kubectl') return { status: 0, stdout: '', stderr: '' };
        let stripped = args[0] === '--kubeconfig' ? args.slice(2) : args;
        if (stripped[0] === '--namespace') stripped = stripped.slice(2);
        return histActiveExec('kubectl', stripped, opts);
      },
      withKubeconfig: async (_target, action) => action(['--kubeconfig', 'offline']),
      deleteLease: async () => { histActiveLeaseDeleted = true; },
      log: () => {},
    };
    await assert.rejects(
      runProjectsConfigMigration(
        parseProjectsConfigMigrationArgs(['--release-lock', heldRunId, '--receipt-dir', receiptDir]),
        releaseOverrides,
      ),
      /while its Job or Pods remain active or unknown/);
    assert.equal(histActiveLeaseDeleted, false);

    unlabeledJobComplete = true;
    const releaseReceipt = await runProjectsConfigMigration(
      parseProjectsConfigMigrationArgs(['--release-lock', heldRunId, '--receipt-dir', receiptDir]),
      releaseOverrides,
    );
    assert.equal(releaseReceipt.result, 'succeeded');
    assert.equal(histActiveLeaseDeleted, true);
  } finally {
    rmSync(histActive.testRoot, { recursive: true, force: true });
  }
  const withoutConsumedRevision = JSON.parse(realPostgresPlanJson);
  const pcSvc = withoutConsumedRevision.services.find(s => s.schema === 'projects_config');
  const blTbl = pcSvc.tables.find(t => t.table === 'native_acs_project_policy_baselines');
  blTbl.updateColumns = blTbl.updateColumns.filter(c => c !== 'consumed_revision');
  assert.equal(sourceSupportsConsumeGuard((cmd, args, opts) => {
    if (cmd === 'git' && args[0] === 'show' && args[1].endsWith(':scripts/azure/lib/p1-postgres-plan.json')) {
      return { status: 0, stdout: JSON.stringify(withoutConsumedRevision), stderr: '' };
    }
    return realGitExec(cmd, args, opts);
  }, root, headSha, headTree), false);
});
