import test from 'node:test';
import assert from 'node:assert/strict';

import {
  contextBudgetProfileInternals,
  withContextBudgetProfile,
} from '../lib/context-budget-profile.mjs';

const { nextEnv, restoredEnv } = contextBudgetProfileInternals;

test('profile environment snapshots preserve value, valueFrom, and absence independently', () => {
  const original = [
    { name: 'UNCHANGED', value: 'yes' },
    { name: 'MemoryContext__MaxItems', valueFrom: { configMapKeyRef: { name: 'limits', key: 'items' } } },
  ];
  const applied = nextEnv(original, { maxItems: 1, maxTokens: 128 });
  assert.deepEqual(applied.slice(-2), [
    { name: 'MemoryContext__MaxItems', value: '1' },
    { name: 'MemoryContext__MaxTokens', value: '128' },
  ]);
  assert.deepEqual(restoredEnv(applied, {
    MemoryContext__MaxItems: original[1],
    MemoryContext__MaxTokens: null,
  }), original);
});

test('profile refuses mutation without a verified non-release target', async () => {
  let called = false;
  await assert.rejects(
    withContextBudgetProfile({
      target: 'https://agentweaver.example.test',
      confirmNonProduction: 'https://agentweaver.example.test',
      namespace: 'agentweaver',
      kubeContext: 'production-context',
      maxItems: 1,
      maxTokens: 128,
      capture: async () => {
        called = true;
        return { stdout: 'production-context' };
      },
    }, async () => {}),
    /isRelease=false/,
  );
  assert.equal(called, false);
});

function deployment(name, container, resourceVersion, generation, env, annotation) {
  return {
    metadata: { name, resourceVersion, generation },
    spec: {
      template: {
        metadata: { annotations: annotation ? { 'agentweaver.io/context-budget-profile': annotation } : {} },
        spec: { containers: [{ name: container, env }] },
      },
    },
  };
}

function makeCluster({ actionError, restoreMismatch = false, rolloutError = false, patchFailureAt = null } = {}) {
  const calls = [];
  const state = new Map([
    ['agentweaver-api', deployment('agentweaver-api', 'api', '1', 10, [
      { name: 'UNCHANGED', value: 'api' },
      { name: 'MemoryContext__MaxItems', valueFrom: { secretKeyRef: { name: 'limits', key: 'items' } } },
    ])],
    ['agentweaver-worker', deployment('agentweaver-worker', 'worker', '1', 20, [
      { name: 'UNCHANGED', value: 'worker' },
      { name: 'MemoryContext__MaxTokens', value: '777' },
    ])],
  ]);
  let lease = null;
  let deploymentPatchCount = 0;
  const capture = async (_cmd, args, options = {}) => {
    calls.push(['capture', ...args]);
    if (args[0] === 'config') return { stdout: 'staging-context' };
    if (args[0] === 'create') {
      lease = JSON.parse(options.input);
      return { stdout: '' };
    }
    if (args[0] === 'delete' && args[1] === 'lease') {
      lease = null;
      return { stdout: '' };
    }
    if (args[0] === 'get' && args[1] === 'lease') return { json: structuredClone(lease) };
    if (args[0] === 'get' && args[1] === 'httproute') {
      return { json: { spec: { hostnames: ['agentweaver.example.staging.test'] } } };
    }
    if (args[0] === 'get' && args[1] === 'deployment') {
      const value = structuredClone(state.get(args[2]));
      if (restoreMismatch && value.metadata.generation > 21 && args[2] === 'agentweaver-worker') {
        value.spec.template.spec.containers[0].env.push({ name: 'MemoryContext__MaxItems', value: '999' });
      }
      return { json: value };
    }
    if (args[0] === 'patch' && args[1] === 'lease') return { stdout: '' };
    if (args[0] === 'patch' && args[1] === 'deployment') {
      deploymentPatchCount += 1;
      if (deploymentPatchCount === patchFailureAt) throw new Error('deployment patch failed');
      const current = state.get(args[2]);
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      assert.equal(patch[0].value, current.metadata.resourceVersion);
      for (const operation of patch.slice(1)) {
        if (operation.path.endsWith('/env')) current.spec.template.spec.containers[0].env = operation.value;
        else if (operation.op === 'remove') delete current.spec.template.metadata.annotations['agentweaver.io/context-budget-profile'];
        else current.spec.template.metadata.annotations['agentweaver.io/context-budget-profile'] = operation.value;
      }
      current.metadata.resourceVersion = String(Number(current.metadata.resourceVersion) + 1);
      current.metadata.generation += 1;
      return { stdout: '' };
    }
    throw new Error(`unexpected kubectl call ${args.join(' ')}`);
  };
  const run = async (_cmd, args) => {
    calls.push(['run', ...args]);
    if (rolloutError) throw new Error('rollout failed');
    return { code: 0 };
  };
  const action = async () => {
    if (actionError) throw new Error(actionError);
    return 'ok';
  };
  return { state, calls, capture, run, action };
}

test('profile applies to both deployments and restores exact prior variable structures', async () => {
  const cluster = makeCluster();
  const result = await withContextBudgetProfile({
    target: 'https://agentweaver.example.staging.test',
    confirmNonProduction: 'https://agentweaver.example.staging.test',
    nonProductionVerified: true,
    namespace: 'agentweaver',
    kubeContext: 'staging-context',
    maxItems: 1,
    maxTokens: 128,
    capture: cluster.capture,
    run: cluster.run,
    owner: 'test-owner',
  }, cluster.action);
  assert.equal(result, 'ok');
  assert.deepEqual(cluster.state.get('agentweaver-api').spec.template.spec.containers[0].env, [
    { name: 'UNCHANGED', value: 'api' },
    { name: 'MemoryContext__MaxItems', valueFrom: { secretKeyRef: { name: 'limits', key: 'items' } } },
  ]);
  assert.deepEqual(cluster.state.get('agentweaver-worker').spec.template.spec.containers[0].env, [
    { name: 'UNCHANGED', value: 'worker' },
    { name: 'MemoryContext__MaxTokens', value: '777' },
  ]);
  assert.equal(cluster.calls.filter((call) => call[0] === 'run').length, 4);
});

test('profile restores after scenario failure and attaches cleanup failures', async () => {
  const scenarioFailure = makeCluster({ actionError: 'scenario failed' });
  await assert.rejects(
    withContextBudgetProfile({
      target: 'https://agentweaver.example.staging.test',
      confirmNonProduction: 'https://agentweaver.example.staging.test',
      nonProductionVerified: true,
      namespace: 'agentweaver',
      kubeContext: 'staging-context',
      maxItems: 1,
      maxTokens: 128,
      capture: scenarioFailure.capture,
      run: scenarioFailure.run,
      owner: 'test-owner',
    }, scenarioFailure.action),
    /scenario failed/,
  );

  const cleanupFailure = makeCluster({ actionError: 'scenario failed', rolloutError: true });
  await assert.rejects(
    withContextBudgetProfile({
      target: 'https://agentweaver.example.staging.test',
      confirmNonProduction: 'https://agentweaver.example.staging.test',
      nonProductionVerified: true,
      namespace: 'agentweaver',
      kubeContext: 'staging-context',
      maxItems: 1,
      maxTokens: 128,
      capture: cleanupFailure.capture,
      run: cleanupFailure.run,
      owner: 'test-owner',
    }, cleanupFailure.action),
    (error) => {
      assert.equal(error.message, 'rollout failed');
      assert.ok(error.cleanupErrors.some((message) => message.includes('verification')));
      return true;
    },
  );
});

test('profile restores the first deployment after partial setup failure', async () => {
  const cluster = makeCluster({ patchFailureAt: 2 });
  await assert.rejects(
    withContextBudgetProfile({
      target: 'https://agentweaver.example.staging.test',
      confirmNonProduction: 'https://agentweaver.example.staging.test',
      nonProductionVerified: true,
      namespace: 'agentweaver',
      kubeContext: 'staging-context',
      maxItems: 1,
      maxTokens: 128,
      capture: cluster.capture,
      run: cluster.run,
      owner: 'test-owner',
    }, cluster.action),
    /deployment patch failed/,
  );
  assert.deepEqual(cluster.state.get('agentweaver-api').spec.template.spec.containers[0].env, [
    { name: 'UNCHANGED', value: 'api' },
    { name: 'MemoryContext__MaxItems', valueFrom: { secretKeyRef: { name: 'limits', key: 'items' } } },
  ]);
});

test('cleanup structural mismatch fails the profile', async () => {
  const cluster = makeCluster({ restoreMismatch: true });
  await assert.rejects(
    withContextBudgetProfile({
      target: 'https://agentweaver.example.staging.test',
      confirmNonProduction: 'https://agentweaver.example.staging.test',
      nonProductionVerified: true,
      namespace: 'agentweaver',
      kubeContext: 'staging-context',
      maxItems: 1,
      maxTokens: 128,
      capture: cluster.capture,
      run: cluster.run,
      owner: 'test-owner',
    }, cluster.action),
    /cleanup failed/i,
  );
});

test('profile signal cancellation unwinds through restoration', async () => {
  const cluster = makeCluster();
  const controller = new AbortController();
  await assert.rejects(
    withContextBudgetProfile({
      target: 'https://agentweaver.example.staging.test',
      confirmNonProduction: 'https://agentweaver.example.staging.test',
      nonProductionVerified: true,
      namespace: 'agentweaver',
      kubeContext: 'staging-context',
      maxItems: 1,
      maxTokens: 128,
      capture: cluster.capture,
      run: cluster.run,
      owner: 'test-owner',
      signal: controller.signal,
    }, async () => {
      controller.abort(new Error('SIGTERM'));
      throw controller.signal.reason;
    }),
    /SIGTERM/,
  );
  assert.deepEqual(cluster.state.get('agentweaver-worker').spec.template.spec.containers[0].env, [
    { name: 'UNCHANGED', value: 'worker' },
    { name: 'MemoryContext__MaxTokens', value: '777' },
  ]);
});
