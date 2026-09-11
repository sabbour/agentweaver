import test from 'node:test';
import assert from 'node:assert/strict';

import { runGenerationSeams } from '../lib/seams.mjs';
import { redact } from '../../harness-shared/redaction.mjs';

test('Entra session preflight identifies the required bearer type without retaining config', async () => {
  const config = { ok: true, status: 200, responseBody: { mode: 'Entra', client_id: 'public-client-id' } };
  const version = { ok: true, status: 200, responseBody: { version: 'v0.30.0', gitSha: 'abc123', isRelease: false } };
  const session = { ok: false, status: 401, responseBody: { error: 'unauthorized' } };
  const calls = [];
  const client = {
    async get(path, options) {
      calls.push({ path, options });
      if (path === '/api/version') return version;
      if (path === '/api/auth/config') return config;
      assert.equal(path, '/api/auth/session');
      return session;
    },
  };

  const result = await runGenerationSeams(client, {});

  assert.equal(result.pass, false);
  assert.deepEqual(calls, [
    { path: '/api/version', options: { authenticated: false } },
    { path: '/api/auth/config', options: { authenticated: false } },
    { path: '/api/auth/session', options: undefined },
  ]);
  assert.deepEqual(result.evidence.deployment, { version: 'v0.30.0', gitSha: 'abc123', isRelease: false });
  assert.deepEqual(result.evidence.authentication, { configStatus: 200, serverMode: 'Entra', sessionStatus: 401 });
  assert.deepEqual(config.responseBody, { mode: 'Entra' });
  assert.deepEqual(session.responseBody, { authenticated: false, auth_mode: null });
  assert.match(result.checks.at(-1).detail, /valid Entra bearer token/i);
});

test('missing version stops before authenticated preflight or resource creation', async () => {
  const calls = [];
  const client = {
    async get(path, options) {
      calls.push({ path, options });
      return { ok: false, status: 404, responseBody: { error: 'not found' } };
    },
    async post(path) {
      calls.push(path);
      throw new Error('resource creation must not be attempted');
    },
  };

  const result = await runGenerationSeams(client, {});

  assert.equal(result.pass, false);
  assert.deepEqual(calls, [{ path: '/api/version', options: { authenticated: false } }]);
  assert.deepEqual(result.evidence.deployment, { version: null, gitSha: null, isRelease: null });
  assert.equal(result.checks[0].name, 'Deployment reports its current version (/api/version)');
});

test('generation error evidence redacts execution context keys', async () => {
  const executionKey = 'execution-key-canary';
  const client = {
    async get(path) {
      if (path === '/api/version')
        return { ok: true, status: 200, responseBody: { version: 'v0.30.0', gitSha: 'abc123', isRelease: false } };
      if (path === '/api/auth/config')
        return { ok: true, status: 200, responseBody: { mode: 'Entra' } };
      assert.equal(path, '/api/auth/session');
      return { ok: true, status: 200, responseBody: { authenticated: true, auth_mode: 'entra' } };
    },
    async post(path, body) {
      if (path === '/api/ai/execution-context') {
        return {
          ok: true,
          status: 200,
          responseBody: {
            ai_required: true,
            operation: body.operation,
            phase: 'prepared',
            execution_key: 'prepared-execution-key-canary',
          },
        };
      }
      if (path === '/api/blueprints/generate') {
        return {
          ok: false,
          status: 409,
          responseBody: { error: 'ai_execution_context_required', context: { execution_key: executionKey } },
        };
      }
      assert.equal(path, '/api/projects');
      return { ok: false, status: 400, responseBody: { error: 'project_not_created' } };
    },
  };

  const result = await runGenerationSeams(client, {
    projectPrefix: 'seam',
    blueprintDescription: 'generate',
  });

  const failure = result.checks.find((check) => check.name === 'Blueprint generation returned a usable draft');
  assert.ok(failure);
  assert.doesNotMatch(failure.detail, new RegExp(executionKey));
  assert.match(failure.detail, /execution_key":"\[REDACTED\]/);
});

test('generation seams prepare operation-scoped context and pass its transient header', async () => {
  const guardedRequests = [];
  const client = {
    async get(path) {
      if (path === '/api/version')
        return { ok: true, status: 200, responseBody: { version: 'v0.30.0', gitSha: 'abc123', isRelease: false } };
      if (path === '/api/auth/config')
        return { ok: true, status: 200, responseBody: { mode: 'Entra' } };
      assert.equal(path, '/api/auth/session');
      return { ok: true, status: 200, responseBody: { authenticated: true, auth_mode: 'entra' } };
    },
    async post(path, body, options) {
      if (path === '/api/ai/execution-context') {
        return {
          ok: true,
          status: 200,
          responseBody: {
            ai_required: true,
            operation: body.operation,
            phase: 'prepared',
            execution_key: `${body.operation}-key-canary`,
          },
        };
      }
      if (path === '/api/blueprints/generate') {
        guardedRequests.push({ path, body, headers: options?.headers });
        return { ok: false, status: 409, responseBody: { error: 'provider_unavailable' } };
      }
      if (path === '/api/projects') {
        return { ok: true, status: 201, responseBody: { project_id: 'owned-project' } };
      }
      assert.equal(path, '/api/projects/owned-project/workflows/generate');
      guardedRequests.push({ path, body, headers: options?.headers });
      return { ok: false, status: 409, responseBody: { error: 'provider_unavailable' } };
    },
    async put() {
      return { ok: true, status: 204, responseBody: null };
    },
    async del() {
      return { ok: true, status: 204, responseBody: null };
    },
  };

  const result = await runGenerationSeams(client, {
    projectPrefix: 'seam',
    baseBlueprintId: 'base-blueprint',
    blueprintDescription: 'generate blueprint',
    workflowDescription: 'generate workflow',
  });

  assert.deepEqual(
    guardedRequests.map(({ path, headers }) => ({ path, header: headers?.['If-Model-Provider-Key'] })),
    [
      { path: '/api/blueprints/generate', header: 'blueprint_generation-key-canary' },
      { path: '/api/projects/owned-project/workflows/generate', header: 'workflow_generation-key-canary' },
    ],
  );
  assert.deepEqual(result.evidence.aiExecutionContexts, {
    blueprintGeneration: {
      status: 200,
      operation: 'blueprint_generation',
      phase: 'prepared',
      aiRequired: true,
      keyPresent: true,
      providerState: null,
      error: null,
      replacement: null,
    },
    workflowGeneration: {
      status: 200,
      operation: 'workflow_generation',
      phase: 'prepared',
      aiRequired: true,
      keyPresent: true,
      providerState: null,
      error: null,
      replacement: null,
    },
  });
  assert.doesNotMatch(JSON.stringify(result.evidence), /key-canary/);
  assert.equal(redact(result.evidence).aiExecutionContexts.blueprintGeneration.keyPresent, true);
  assert.equal(redact(result.evidence).aiExecutionContexts.workflowGeneration.keyPresent, true);
  await result.cleanup();
});

test('generation seams retry once with the replacement context after a provider change', async () => {
  const requests = [];
  const validWorkflow = `id: generated-workflow
name: Generated Workflow
start: work
nodes:
  - id: work
    type: prompt
    role: backend-engineer
  - id: done
    type: terminal
edges:
  - { from: work, to: done }
`;
  let blueprintAttempts = 0;
  let workflowAttempts = 0;
  const client = {
    async get(path) {
      if (path === '/api/version')
        return { ok: true, status: 200, responseBody: { version: 'v0.30.0', gitSha: 'abc123', isRelease: false } };
      if (path === '/api/auth/config')
        return { ok: true, status: 200, responseBody: { mode: 'Entra' } };
      assert.equal(path, '/api/auth/session');
      return { ok: true, status: 200, responseBody: { authenticated: true, auth_mode: 'entra' } };
    },
    async post(path, body, options) {
      if (path === '/api/ai/execution-context') {
        return {
          ok: true,
          status: 200,
          responseBody: {
            ai_required: true,
            operation: body.operation,
            phase: 'prepared',
            execution_key: `${body.operation}-initial-key-canary`,
          },
        };
      }
      if (path === '/api/blueprints/generate') {
        requests.push({ path, header: options?.headers?.['If-Model-Provider-Key'] });
        blueprintAttempts += 1;
        return blueprintAttempts === 1
          ? {
            ok: false,
            status: 409,
            responseBody: {
              error: 'model_provider_changed',
              message: 'provider changed',
              context: {
                ai_required: true,
                operation: 'blueprint_generation',
                phase: 'prepared',
                execution_key: 'blueprint-replacement-key-canary',
              },
            },
          }
          : {
            ok: true,
            status: 200,
            responseBody: { blueprint: { roster: ['backend-engineer', 'product-manager'], workflows: ['generated-workflow'] } },
          };
      }
      if (path === '/api/projects') {
        return { ok: true, status: 201, responseBody: { project_id: 'owned-project' } };
      }
      assert.equal(path, '/api/projects/owned-project/workflows/generate');
      requests.push({ path, header: options?.headers?.['If-Model-Provider-Key'] });
      workflowAttempts += 1;
      return workflowAttempts === 1
        ? {
          ok: false,
          status: 409,
          responseBody: {
            error: 'model_provider_changed',
            message: 'provider changed',
            context: {
              ai_required: true,
              operation: 'workflow_generation',
              phase: 'prepared',
              execution_key: 'workflow-replacement-key-canary',
            },
          },
        }
        : {
          ok: true,
          status: 200,
          responseBody: { workflowId: 'generated-workflow', yaml: validWorkflow },
        };
    },
    async put(_path, body) {
      return body.yaml.includes('branches: [pass, fail]')
        ? { ok: false, status: 400, responseBody: { error: 'invalid_workflow' } }
        : { ok: true, status: 204, responseBody: null };
    },
    async del() {
      return { ok: true, status: 204, responseBody: null };
    },
  };

  const result = await runGenerationSeams(client, {
    projectPrefix: 'seam',
    baseBlueprintId: 'base-blueprint',
    blueprintDescription: 'generate blueprint',
    workflowDescription: 'generate workflow',
  });

  assert.equal(result.pass, true);
  assert.deepEqual(requests, [
    { path: '/api/blueprints/generate', header: 'blueprint_generation-initial-key-canary' },
    { path: '/api/blueprints/generate', header: 'blueprint-replacement-key-canary' },
    { path: '/api/projects/owned-project/workflows/generate', header: 'workflow_generation-initial-key-canary' },
    { path: '/api/projects/owned-project/workflows/generate', header: 'workflow-replacement-key-canary' },
  ]);
  assert.equal(result.evidence.aiExecutionContexts.blueprintGeneration.replacement.retryStatus, 200);
  assert.equal(result.evidence.aiExecutionContexts.workflowGeneration.replacement.retryStatus, 200);
  assert.doesNotMatch(JSON.stringify(result.evidence), /key-canary/);
  await result.cleanup();
});

test('owned project is deleted when a later seam step throws', async () => {
  const calls = [];
  const client = {
    get: async (path) => {
      if (path === '/api/version')
        return { ok: true, status: 200, responseBody: { version: 'v0.30.0', gitSha: 'abc123', isRelease: false } };
      if (path === '/api/auth/config')
        return { ok: true, status: 200, responseBody: { mode: 'Entra' } };
      assert.equal(path, '/api/auth/session');
      return { ok: true, status: 200, responseBody: { authenticated: true, auth_mode: 'entra' } };
    },
    post: async (path, body) => {
      calls.push(path);
      if (path === '/api/ai/execution-context') {
        return {
          ok: true,
          status: 200,
          responseBody: {
            ai_required: true,
            operation: body.operation,
            phase: 'prepared',
            execution_key: 'prepared-execution-key',
          },
        };
      }
      if (path === '/api/blueprints/generate') {
        return {
          ok: true,
          status: 200,
          responseBody: { blueprint: { id: 'bp', name: 'BP', roster: ['a', 'b'], workflows: ['w'] } },
        };
      }
      if (path === '/api/projects') {
        return { ok: true, status: 201, responseBody: { project_id: 'owned-project' } };
      }
      throw new Error('workflow generation failed');
    },
    del: async (path) => {
      calls.push(path);
      return { ok: true, status: 204 };
    },
  };
  await assert.rejects(
    runGenerationSeams(client, {
      projectPrefix: 'seam',
      baseBlueprintId: 'bp',
      blueprintDescription: 'generate',
      workflowDescription: 'generate',
    }),
    /workflow generation failed/,
  );
  assert.equal(calls.at(-1), '/api/projects/owned-project?confirm=true');
});

test('primary seam failure is preserved when owned cleanup also fails', async () => {
  const client = {
    get: async (path) => {
      if (path === '/api/version')
        return { ok: true, status: 200, responseBody: { version: 'v0.30.0', gitSha: 'abc123', isRelease: false } };
      if (path === '/api/auth/config')
        return { ok: true, status: 200, responseBody: { mode: 'Entra' } };
      assert.equal(path, '/api/auth/session');
      return { ok: true, status: 200, responseBody: { authenticated: true, auth_mode: 'entra' } };
    },
    post: async (path, body) => {
      if (path === '/api/ai/execution-context') {
        return {
          ok: true,
          status: 200,
          responseBody: {
            ai_required: true,
            operation: body.operation,
            phase: 'prepared',
            execution_key: 'prepared-execution-key',
          },
        };
      }
      if (path === '/api/blueprints/generate') {
        return { ok: true, status: 200, responseBody: { blueprint: { roster: ['a', 'b'], workflows: ['w'] } } };
      }
      if (path === '/api/projects') {
        return { ok: true, status: 201, responseBody: { project_id: 'owned-project' } };
      }
      throw new Error('primary seam failure');
    },
    del: async () => ({ ok: false, status: 503 }),
  };
  await assert.rejects(
    runGenerationSeams(client, {
      projectPrefix: 'seam',
      baseBlueprintId: 'bp',
      blueprintDescription: 'generate',
      workflowDescription: 'generate',
    }),
    (error) => {
      assert.equal(error.message, 'primary seam failure');
      assert.deepEqual(error.cleanupErrors, ['throwaway project cleanup failed with status 503']);
      return true;
    },
  );
});
