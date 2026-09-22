import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';

import { runContextBudgetPressure } from '../lib/context-budget-pressure.mjs';
import { main } from '../run-context-budget-pressure.mjs';

function clientFor({ cleanupFailure = false } = {}) {
  let project = 0;
  let memory = 0;
  const calls = [];
  const runDataset = new Map();
  return {
    calls,
    async post(path, body) {
      calls.push(['post', path, body]);
      if (path === '/api/projects') return response(201, { project_id: `project-${++project}` });
      if (path.includes('/memory/') && path.endsWith('/promote')) return response(200, {});
      if (path.endsWith('/memory')) return response(201, { id: ++memory });
      if (path.endsWith('/decisions')) return response(201, { id: 1 });
      if (path === '/api/ai/execution-context') {
        return response(200, {
          ai_required: true,
          operation: 'orchestration',
          phase: 'prepared',
          execution_key: 'transient-key',
        });
      }
      if (path.endsWith('/orchestrations')) {
        const dataset = body.goal.match(/deterministic (.+) context-budget/)?.[1];
        const runId = `run-${runDataset.size + 1}`;
        runDataset.set(runId, dataset);
        return response(201, { runId });
      }
      if (path.endsWith('/cancel')) return response(cleanupFailure ? 500 : 200, {});
      throw new Error(`unexpected post ${path}`);
    },
    async get(path) {
      calls.push(['get', path]);
      const runId = path.split('/')[3];
      const dataset = runDataset.get(runId);
      const payload = dataset === 'item_limit'
        ? { omittedMemoryCount: 2, omissionCauses: ['item_limit'] }
        : dataset === 'token_budget_omission'
          ? { omittedMemoryCount: 1, omissionCauses: ['budget'] }
          : { errorCode: 'mandatory_context_budget_exceeded' };
      return response(200, [{
        type: dataset === 'mandatory_context_budget_exceeded' ? 'run.failed' : 'memory.context_composition',
        payload,
      }]);
    },
    async del(path) {
      calls.push(['del', path]);
      return response(cleanupFailure ? 500 : 204, {});
    },
  };
}

function response(status, responseBody) {
  return {
    status,
    ok: status >= 200 && status < 300,
    responseBody,
    transientResponseBody: structuredClone(responseBody),
  };
}

test('pressure acceptance uses three fresh datasets and cleans each one', async () => {
  const client = clientFor();
  const result = await runContextBudgetPressure(client, { timeoutMs: 1000 });
  assert.deepEqual(result.results.map((entry) => entry.assertion), [
    'item_limit',
    'budget',
    'mandatory_context_budget_exceeded',
  ]);
  assert.equal(new Set(result.results.map((entry) => entry.projectId)).size, 3);
  assert.equal(client.calls.filter(([method, path]) => method === 'del' && path.includes('/api/projects/')).length, 3);
});

test('scenario failure still reports run and project cleanup mismatch', async () => {
  const client = clientFor({ cleanupFailure: true });
  await assert.rejects(
    runContextBudgetPressure(client, { timeoutMs: 1000 }),
    (error) => {
      assert.match(error.message, /Dataset cleanup failed/);
      return true;
    },
  );
});

test('runner signal cancellation remains primary while the profile restores', async () => {
  const processImpl = new EventEmitter();
  processImpl.stdout = { write() {} };
  let restored = false;
  const dependencies = {
    createAuthProvider: () => ({}),
    createClient: () => ({
      get: async () => response(200, { isRelease: false }),
    }),
    withProfile: async (options, action) => {
      try {
        return await action();
      } finally {
        assert.equal(options.signal.aborted, true);
        restored = true;
      }
    },
    runPressure: async (_client, options) => {
      processImpl.emit('SIGTERM');
      throw options.signal.reason;
    },
  };

  await assert.rejects(
    main([
      '--target', 'https://agentweaver.example.staging.test',
      '--namespace', 'agentweaver',
      '--kube-context', 'staging-context',
      '--confirm-non-production', 'https://agentweaver.example.staging.test',
    ], processImpl, dependencies),
    /cancelled by SIGTERM/,
  );
  assert.equal(restored, true);
  assert.equal(processImpl.listenerCount('SIGINT'), 0);
  assert.equal(processImpl.listenerCount('SIGTERM'), 0);
});
