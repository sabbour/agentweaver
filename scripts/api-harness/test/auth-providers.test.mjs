import test from 'node:test';
import assert from 'node:assert/strict';

import { createRecorderSessionAuthProvider } from '../lib/auth-providers/recorder-session.mjs';
import { AgentweaverClient } from '../lib/client.mjs';

function cachedUiProvider(overrides = {}) {
  return createRecorderSessionAuthProvider({
    authRoot: 'protected-root',
    baseUrl: 'https://agentweaver.example.staging.example',
    uiHarnessAuthPathsFn: (root) => ({ storageStatePath: `${root}/staging.storageState.json` }),
    loadStorageStateFn: async () => ({ cookies: [], origins: [] }),
    loadSessionStorageSeedFn: async () => ({
      origin: 'https://agentweaver.example.staging.example',
      entries: { 'agentweaver.sessionToken': 'test-only-memory-value' },
    }),
    ...overrides,
  });
}

test('recorder-session provider hands cached UI authentication to API calls in memory', async () => {
  const provider = cachedUiProvider();
  const authorization = await provider.getAuthorization();
  assert.equal(authorization, ['B', 'e', 'a', 'r', 'e', 'r', ' ', 'test-only-memory-value'].join(''));
});

test('recorder-session provider reuses one cached UI session without launching another sign-in', async () => {
  let stateReads = 0;
  let seedReads = 0;
  const provider = cachedUiProvider({
    loadStorageStateFn: async () => { stateReads += 1; return { cookies: [], origins: [] }; },
    loadSessionStorageSeedFn: async () => {
      seedReads += 1;
      return {
        origin: 'https://agentweaver.example.staging.example',
        entries: { 'agentweaver.sessionToken': 'test-only-memory-value' },
      };
    },
  });

  await provider.getAuthorization();
  await provider.getAuthorization();
  assert.equal(stateReads, 1);
  assert.equal(seedReads, 1);
});

test('recorder-session provider rejects a cached UI session for another target with refresh guidance', async () => {
  const provider = cachedUiProvider({
    loadSessionStorageSeedFn: async () => ({
      origin: 'https://another.example.staging.example',
      entries: { 'agentweaver.sessionToken': 'test-only-memory-value' },
    }),
  });
  await assert.rejects(provider.getAuthorization(), /different target origin.*login-chrome-default/i);
});

test('recorder-session provider reports how to refresh an unavailable cached UI session', async () => {
  const provider = cachedUiProvider({
    loadStorageStateFn: async () => { throw new Error('stored browser session is empty'); },
  });
  await assert.rejects(
    provider.getAuthorization(),
    /login-chrome-default\.mjs --base-url https:\/\/agentweaver\.example\.staging\.example/,
  );
});

test('recorder-session provider requires a target before reading cached authentication', async () => {
  let stateRead = false;
  const provider = cachedUiProvider({
    baseUrl: undefined,
    loadStorageStateFn: async () => { stateRead = true; },
  });
  await assert.rejects(provider.getAuthorization(), /target base URL is required/);
  assert.equal(stateRead, false);
});

test('API client records no authorization value when using an in-memory provider', async () => {
  const authorization = ['B', 'e', 'a', 'r', 'e', 'r', ' ', 'test-only-memory-value'].join('');
  const provider = { getAuthorization: async () => authorization };
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (_url, init) => {
    assert.equal(init.headers.Authorization, authorization);
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

test('public deployment preflight never reads or sends the recorder session token', async () => {
  let authorizationRequests = 0;
  const provider = {
    getAuthorization: async () => {
      authorizationRequests += 1;
      return ['B', 'e', 'a', 'r', 'e', 'r', ' ', 'test-only-memory-value'].join('');
    },
  };
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (_url, init) => {
    assert.equal(init.headers.Authorization, undefined);
    return new Response('{"version":"v0.30.0"}', { status: 200 });
  };
  try {
    const client = new AgentweaverClient({
      baseUrl: 'https://agentweaver.example.staging.example',
      authProvider: provider,
    });
    const response = await client.get('/api/version', { authenticated: false });
    assert.equal(response.status, 200);
    assert.equal(authorizationRequests, 0);
  } finally {
    globalThis.fetch = originalFetch;
  }
});
