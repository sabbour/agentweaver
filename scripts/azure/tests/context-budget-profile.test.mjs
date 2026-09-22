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

function makeCluster({
  actionError,
  restoreMismatch = false,
  rolloutError = false,
  cleanupRolloutError = false,
  patchFailureAt = null,
  namespace = 'agentweaver',
  replaceLeaseBeforeRelease = false,
  abortOnPatch,
} = {}) {
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
    if (args[0] === 'config' && args[1] === 'current-context') return { stdout: 'staging-context' };
    if (args[0] === 'config' && args[1] === 'view') {
      return { json: { contexts: [{ context: { namespace } }] } };
    }
    if (args[0] === 'create') {
      lease = JSON.parse(options.input);
      lease.metadata.uid = 'lease-uid';
      lease.metadata.resourceVersion = '1';
      return { json: structuredClone(lease) };
    }
    if (args[0] === 'delete' && args[1].startsWith('--raw=')) {
      assert.equal(args[1], '--raw=/apis/coordination.k8s.io/v1/namespaces/agentweaver/leases/agentweaver-context-budget-harness');
      assert.deepEqual(JSON.parse(options.input).preconditions, {
        uid: 'lease-uid',
        resourceVersion: '2',
      });
      lease = null;
      return { stdout: '' };
    }
    if (args[0] === 'get' && args[1] === 'lease') {
      const value = structuredClone(lease);
      if (replaceLeaseBeforeRelease && deploymentPatchCount >= 4) value.metadata.uid = 'replacement-uid';
      return { json: value };
    }
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
    if (args[0] === 'patch' && args[1] === 'lease') {
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      assert.equal(patch[0].value, lease.metadata.resourceVersion);
      assert.equal(patch[1].value, lease.spec.holderIdentity);
      lease.metadata.annotations ??= {};
      lease.metadata.annotations['agentweaver.io/context-budget-snapshot'] = patch[2].value;
      lease.metadata.resourceVersion = '2';
      return { json: structuredClone(lease) };
    }
    if (args[0] === 'patch' && args[1] === 'deployment') {
      deploymentPatchCount += 1;
      if (deploymentPatchCount === patchFailureAt) throw new Error('deployment patch failed');
      const current = state.get(args[2]);
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      assert.equal(patch[0].value, current.metadata.resourceVersion);
      for (const operation of patch.slice(1)) {
        if (operation.op === 'test') {
          if (operation.path.endsWith('/env')) {
            assert.deepEqual(operation.value, current.spec.template.spec.containers[0].env);
          } else {
            assert.equal(operation.value, current.spec.template.metadata.annotations['agentweaver.io/context-budget-profile']);
          }
        } else if (operation.path.endsWith('/env')) current.spec.template.spec.containers[0].env = operation.value;
        else if (operation.op === 'remove') delete current.spec.template.metadata.annotations['agentweaver.io/context-budget-profile'];
        else current.spec.template.metadata.annotations['agentweaver.io/context-budget-profile'] = operation.value;
      }
      current.metadata.resourceVersion = String(Number(current.metadata.resourceVersion) + 1);
      current.metadata.generation += 1;
      if (abortOnPatch && deploymentPatchCount === abortOnPatch.at) {
        assert.equal(options.signal, undefined);
        abortOnPatch.controller.abort(new Error(abortOnPatch.reason));
      }
      return { stdout: '' };
    }
    throw new Error(`unexpected kubectl call ${args.join(' ')}`);
  };
  const run = async (_cmd, args) => {
    calls.push(['run', ...args]);
    const rolloutCount = calls.filter((call) => call[0] === 'run').length;
    if (rolloutError || (cleanupRolloutError && rolloutCount > 2)) throw new Error('rollout failed');
    return { code: 0 };
  };
  const action = async () => {
    if (actionError) throw typeof actionError === 'string' ? new Error(actionError) : actionError;
    return 'ok';
  };
  return { state, calls, capture, run, action };
}

function profileOptions(cluster, overrides = {}) {
  return {
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
    ...overrides,
  };
}

test('profile applies to both deployments and restores exact prior variable structures', async () => {
  const cluster = makeCluster();
  const result = await withContextBudgetProfile(profileOptions(cluster), cluster.action);
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
    withContextBudgetProfile(profileOptions(scenarioFailure), scenarioFailure.action),
    /scenario failed/,
  );

  const cleanupFailure = makeCluster({ actionError: 'scenario failed', rolloutError: true });
  await assert.rejects(
    withContextBudgetProfile(profileOptions(cleanupFailure), cleanupFailure.action),
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
    withContextBudgetProfile(profileOptions(cluster), cluster.action),
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
    withContextBudgetProfile(profileOptions(cluster), cluster.action),
    /cleanup failed/i,
  );
});

test('profile signal cancellation unwinds through restoration', async () => {
  const cluster = makeCluster();
  const controller = new AbortController();
  await assert.rejects(
    withContextBudgetProfile(profileOptions(cluster, { signal: controller.signal }), async () => {
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

test('profile refuses a namespace that is not active in the selected context', async () => {
  const cluster = makeCluster({ namespace: 'other-namespace' });
  await assert.rejects(
    withContextBudgetProfile(profileOptions(cluster), cluster.action),
    /Active Kubernetes namespace "other-namespace"/,
  );
  assert.equal(cluster.calls.some((call) => call[1] === 'create'), false);
});

test('profile cancellation waits for a mutating patch to settle before restoring it', async () => {
  const controller = new AbortController();
  const cluster = makeCluster({
    abortOnPatch: { at: 1, controller, reason: 'SIGINT' },
  });
  await assert.rejects(
    withContextBudgetProfile(profileOptions(cluster, { signal: controller.signal }), cluster.action),
    /SIGINT/,
  );
  assert.deepEqual(cluster.state.get('agentweaver-api').spec.template.spec.containers[0].env, [
    { name: 'UNCHANGED', value: 'api' },
    { name: 'MemoryContext__MaxItems', valueFrom: { secretKeyRef: { name: 'limits', key: 'items' } } },
  ]);
});

test('profile refuses to overwrite changed ownership or applied budget values during cleanup', async () => {
  const cluster = makeCluster();
  await assert.rejects(
    withContextBudgetProfile(profileOptions(cluster), async () => {
      const api = cluster.state.get('agentweaver-api');
      api.spec.template.metadata.annotations['agentweaver.io/context-budget-profile'] = 'different-owner';
      api.spec.template.spec.containers[0].env.find(
        (entry) => entry.name === 'MemoryContext__MaxTokens',
      ).value = '256';
    }),
    /cleanup failed/i,
  );
  assert.equal(
    cluster.state.get('agentweaver-api').spec.template.spec.containers[0].env.find(
      (entry) => entry.name === 'MemoryContext__MaxTokens',
    ).value,
    '256',
  );
});

test('profile preserves existing cleanup errors when deployment restoration also fails', async () => {
  const scenarioError = new Error('scenario failed');
  scenarioError.cleanupErrors = ['dataset cleanup failed'];
  const cluster = makeCluster({ actionError: scenarioError, cleanupRolloutError: true });
  await assert.rejects(
    withContextBudgetProfile(profileOptions(cluster), cluster.action),
    (error) => {
      assert.equal(error, scenarioError);
      assert.ok(error.cleanupErrors.includes('dataset cleanup failed'));
      assert.ok(error.cleanupErrors.some((message) => message.includes('verification: rollout failed')));
      return true;
    },
  );
});

test('profile does not delete a replaced Lease', async () => {
  const cluster = makeCluster({ replaceLeaseBeforeRelease: true });
  await assert.rejects(
    withContextBudgetProfile(profileOptions(cluster), cluster.action),
    /cleanup failed/i,
  );
  assert.equal(
    cluster.calls.some((call) => call[1] === 'delete'),
    false,
  );
});
