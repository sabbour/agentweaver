import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

import { createRecorderSessionAuthProvider } from '../lib/auth-providers/recorder-session.mjs';
import { AgentweaverClient } from '../lib/client.mjs';

function cachedUiProvider(overrides = {}) {
  return createRecorderSessionAuthProvider({
    authRoot: 'protected-root',
    baseUrl: 'https://agentweaver.example.staging.example',
    uiHarnessAuthPathsFn: (root) => ({ storageStatePath: `${root}/recording.storageState.json` }),
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

test('PersonaActor sends the recorder provider Authorization value unchanged', async () => {
  const contract = await readFile(
    new URL('../../../.github/agents/persona-actor.agent.md', import.meta.url),
    'utf8',
  );
  assert.match(contract, /Authorization: authorization,/);
  assert.doesNotMatch(contract, /Authorization:\s*`Bearer \$\{authorization\}`/);
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

test('recorder-session provider uses the recorder layout for a protected endpoint', async (t) => {
  const authRoot = await mkdtemp(path.join(os.tmpdir(), 'agentweaver-recorder-auth-'));
  t.after(() => rm(authRoot, { recursive: true, force: true }));
  const storageStatePath = path.join(authRoot, 'recording.storageState.json');
  await writeFile(storageStatePath, JSON.stringify({
    cookies: [],
    origins: [{ origin: 'https://agentweaver.example.staging.example', localStorage: [] }],
  }), 'utf8');
  await writeFile(`${storageStatePath}.sessionStorage.json`, JSON.stringify({
    origin: 'https://agentweaver.example.staging.example',
    entries: { 'agentweaver.sessionToken': 'test-only-memory-value' },
  }), 'utf8');

  const provider = createRecorderSessionAuthProvider({
    authRoot,
    baseUrl: 'https://agentweaver.example.staging.example',
  });
  const originalFetch = globalThis.fetch;
  t.after(() => { globalThis.fetch = originalFetch; });
  globalThis.fetch = async (url, init) => {
    assert.equal(url.toString(), 'https://agentweaver.example.staging.example/api/auth/session');
    assert.match(init.headers.Authorization, /^Bearer /);
    return new Response('{"authenticated":true}', { status: 200 });
  };

  const client = new AgentweaverClient({
    baseUrl: 'https://agentweaver.example.staging.example',
    authProvider: provider,
  });
  const response = await client.get('/api/auth/session');
  assert.equal(response.status, 200);
  assert.deepEqual(response.responseBody, { authenticated: true });
});

test('recorder-session provider rejects a cached UI session for another target with refresh guidance', async () => {
  const provider = cachedUiProvider({
    loadSessionStorageSeedFn: async () => ({
      origin: 'https://another.example.staging.example',
      entries: { 'agentweaver.sessionToken': 'test-only-memory-value' },
    }),
  });
  await assert.rejects(provider.getAuthorization(), /different target origin.*demo:record -- open/i);
});

test('recorder-session provider reports how to refresh an unavailable cached UI session', async () => {
  const provider = cachedUiProvider({
    loadStorageStateFn: async () => { throw new Error('stored browser session is empty'); },
  });
  await assert.rejects(
    provider.getAuthorization(),
    /demo:record -- open --base-url https:\/\/agentweaver\.example\.staging\.example/,
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
