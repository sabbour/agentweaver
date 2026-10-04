import test from 'node:test';
import assert from 'node:assert/strict';

import { createLocalTestAuthProvider } from '../lib/auth-providers/local-test.mjs';
import { resolveAuthProvider } from '../run-persona.mjs';
import { AgentweaverClient } from '../lib/client.mjs';

test('local-test provider returns a bearer from memory without logging or persisting it', async () => {
  const provider = createLocalTestAuthProvider({
    env: { AGENTWEAVER_LOCAL_TEST_BEARER: 'local-gate-token-canary' },
  });
  assert.equal(await provider.getAuthorization(), 'Bearer local-gate-token-canary');

  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (_url, init) => {
    assert.equal(init.headers.Authorization, 'Bearer local-gate-token-canary');
    return new Response('{"authenticated":true}', { status: 200 });
  };
  try {
    const client = new AgentweaverClient({
      baseUrl: 'http://127.0.0.1:18080',
      authProvider: provider,
    });
    await client.get('/api/auth/session');
    assert.doesNotMatch(JSON.stringify(client.calls), /local-gate-token-canary/);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('run-persona resolves the local-test auth provider explicitly', async () => {
  const prior = process.env.AGENTWEAVER_LOCAL_TEST_BEARER;
  process.env.AGENTWEAVER_LOCAL_TEST_BEARER = 'local-gate-token-canary';
  try {
    const provider = resolveAuthProvider({ authProvider: 'local-test' }, 'http://127.0.0.1:18080');
    assert.equal(provider.name, 'local-test');
    assert.equal(await provider.getAuthorization(), 'Bearer local-gate-token-canary');
  } finally {
    if (prior === undefined) delete process.env.AGENTWEAVER_LOCAL_TEST_BEARER;
    else process.env.AGENTWEAVER_LOCAL_TEST_BEARER = prior;
  }
});
