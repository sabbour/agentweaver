import test from 'node:test';
import assert from 'node:assert/strict';

import { createRecorderSessionAuthProvider } from '../lib/auth-providers/recorder-session.mjs';
import { AgentweaverClient } from '../lib/client.mjs';

test('recorder-session provider reads the protected session value only when authorizing a request', async () => {
  let requestedPath = null;
  const provider = createRecorderSessionAuthProvider({
    authRoot: 'protected-root',
    recordingAuthPathsFn: (root) => ({ sessionStoragePath: `${root}/session.json` }),
    getSessionTokenFn: async (path) => {
      requestedPath = path;
      return 'test-only-memory-value';
    },
  });

  assert.equal(requestedPath, null);
  assert.equal(await provider.getAuthorization(), 'Bearer test-only-memory-value');
  assert.equal(requestedPath, 'protected-root/session.json');
});

test('API client records no authorization value when using an in-memory provider', async () => {
  const provider = { getAuthorization: async () => 'Bearer test-only-memory-value' };
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (_url, init) => {
    assert.equal(init.headers.Authorization, 'Bearer test-only-memory-value');
    return new Response('{"ok":true}', { status: 200 });
  };

  try {
    const client = new AgentweaverClient({
      baseUrl: 'https://agentweaver.example.staging.example',
      authProvider: provider,
    });
    await client.get('/api/ping');
    assert.doesNotMatch(JSON.stringify(client.calls), /test-only-memory-value/);
  } finally {
    globalThis.fetch = originalFetch;
  }
});
