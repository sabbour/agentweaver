import test from 'node:test';
import assert from 'node:assert/strict';

import { runGenerationSeams } from '../lib/seams.mjs';

test('Entra-mode authentication failure identifies the required bearer type without retaining config', async () => {
  const config = { ok: true, status: 200, responseBody: { mode: 'Entra', client_id: 'public-client-id' } };
  const client = {
    async get(path) {
      if (path === '/api/auth/github')
        return { ok: false, status: 401, responseBody: { error: 'unauthorized' } };
      assert.equal(path, '/api/auth/config');
      return config;
    },
  };

  const result = await runGenerationSeams(client, {});

  assert.equal(result.pass, false);
  assert.deepEqual(result.evidence.authentication, { authStatus: 401, serverMode: 'Entra' });
  assert.deepEqual(config.responseBody, { mode: 'Entra' });
  assert.match(result.checks[0].detail, /valid Entra bearer token/i);
});

test('owned project is deleted when a later seam step throws', async () => {
  const calls = [];
  const client = {
    get: async () => ({ ok: true, status: 200, responseBody: { status: 'signed_in', login: 'test' } }),
    post: async (path) => {
      calls.push(path);
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
    get: async () => ({ ok: true, status: 200, responseBody: { status: 'signed_in', login: 'test' } }),
    post: async (path) => {
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
