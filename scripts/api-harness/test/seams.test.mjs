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
