import test from 'node:test';
import assert from 'node:assert/strict';

import { createRecorderSessionAuthProvider } from '../lib/auth-providers/recorder-session.mjs';
import { AgentweaverClient } from '../lib/client.mjs';

test('recorder-session provider reads the protected session value only when authorizing a request', async () => {
  let requestedPath = null;
  const provider = createRecorderSessionAuthProvider({
    authRoot: 'protected-root',
    baseUrl: 'https://agentweaver.example.staging.example',
    recordingAuthPathsFn: (root) => ({ sessionStoragePath: `${root}/session.json` }),
    parseOpenOptionsFn: () => ({}),
    openRecorderSessionFn: async () => {},
    getSessionTokenFn: async (path) => {
      requestedPath = path;
      return 'test-only-memory-value';
    },
  });

  assert.equal(requestedPath, null);
  assert.equal(await provider.getAuthorization(), 'Bearer test-only-memory-value');
  assert.equal(requestedPath, 'protected-root/session.json');
});

test('recorder-session provider launches the managed browser before token handoff', async () => {
  let requestedPath = null;
  const calls = [];
  const provider = createRecorderSessionAuthProvider({
    authRoot: 'protected-root',
    baseUrl: 'https://agentweaver.example.staging.example',
    recordingAuthPathsFn: (root) => ({ sessionStoragePath: `${root}/session.json` }),
    parseOpenOptionsFn: (command, argv) => {
      calls.push({ command, argv });
      return { command, argv };
    },
    openRecorderSessionFn: async (options) => calls.push({ open: options }),
    getSessionTokenFn: async (path) => {
      requestedPath = path;
      return 'test-only-memory-value';
    },
  });

  await provider.getAuthorization();

  assert.deepEqual(calls, [
    {
      command: 'open',
      argv: [
        '--base-url', 'https://agentweaver.example.staging.example',
        '--auth-root', 'protected-root',
      ],
    },
    {
      open: {
        command: 'open',
        argv: [
          '--base-url', 'https://agentweaver.example.staging.example',
          '--auth-root', 'protected-root',
        ],
      },
    },
  ]);
  assert.equal(requestedPath, 'protected-root/session.json');
});

test('recorder-session provider coalesces managed-browser readiness before token handoff', async () => {
  let starts = 0;
  let tokenReads = 0;
  let release;
  const started = new Promise((resolve) => { release = resolve; });
  const provider = createRecorderSessionAuthProvider({
    baseUrl: 'https://agentweaver.example.staging.example',
    recordingAuthPathsFn: () => ({ sessionStoragePath: 'protected/session.json' }),
    parseOpenOptionsFn: () => ({}),
    openRecorderSessionFn: async () => {
      starts += 1;
      await started;
    },
    getSessionTokenFn: async () => {
      tokenReads += 1;
      return 'test-only-memory-value';
    },
  });

  const first = provider.getAuthorization();
  const second = provider.getAuthorization();
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(starts, 1);
  assert.equal(tokenReads, 0);
  release();
  await Promise.all([first, second]);
  assert.equal(tokenReads, 2);
});

test('recorder-session provider refreshes and restores when an open session has no local handoff', async () => {
  let opens = 0;
  let refreshes = 0;
  let tokenReads = 0;
  const provider = createRecorderSessionAuthProvider({
    baseUrl: 'https://agentweaver.example.staging.example',
    recordingAuthPathsFn: () => ({ sessionStoragePath: 'protected/session.json' }),
    parseOpenOptionsFn: () => ({}),
    openRecorderSessionFn: async () => { opens += 1; },
    refreshRecorderAuthenticationFn: async () => { refreshes += 1; },
    getSessionTokenFn: async () => {
      tokenReads += 1;
      if (tokenReads === 1) throw new Error('Protected recording authentication is unavailable.');
      return 'test-only-memory-value';
    },
  });

  await provider.getAuthorization();

  assert.equal(opens, 2);
  assert.equal(refreshes, 1);
  assert.equal(tokenReads, 2);
});

test('recorder-session provider preserves the human-only IdP boundary', async () => {
  let tokenRead = false;
  const provider = createRecorderSessionAuthProvider({
    baseUrl: 'https://agentweaver.example.staging.example',
    recordingAuthPathsFn: () => ({ sessionStoragePath: 'protected/session.json' }),
    parseOpenOptionsFn: () => ({}),
    openRecorderSessionFn: async () => {
      throw new Error('Microsoft Entra account selection requires human interaction.');
    },
    getSessionTokenFn: async () => {
      tokenRead = true;
      return 'test-only-memory-value';
    },
  });

  await assert.rejects(provider.getAuthorization(), /requires human interaction/);
  assert.equal(tokenRead, false);
});

test('recorder-session provider requires the current target before launching Chrome', async () => {
  let browserStarted = false;
  const provider = createRecorderSessionAuthProvider({
    recordingAuthPathsFn: () => ({ sessionStoragePath: 'protected/session.json' }),
    openRecorderSessionFn: async () => { browserStarted = true; },
  });

  await assert.rejects(provider.getAuthorization(), /target base URL is required/);
  assert.equal(browserStarted, false);
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

test('API client sends a model-provider key only in its request header', async () => {
  const executionKey = 'execution-key-canary';
  const provider = {
    getAuthorization: async () => ['B', 'e', 'a', 'r', 'e', 'r', ' ', 'test-only-memory-value'].join(''),
  };
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (_url, init) => {
    assert.equal(init.headers['If-Model-Provider-Key'], executionKey);
    return new Response('{"ok":true}', { status: 200 });
  };

  try {
    const client = new AgentweaverClient({
      baseUrl: 'https://agentweaver.example.staging.example',
      authProvider: provider,
    });
    await client.post('/api/blueprints/generate', { description: 'test' }, {
      headers: { 'If-Model-Provider-Key': executionKey },
    });
    assert.doesNotMatch(JSON.stringify(client.calls), new RegExp(executionKey));
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('API client keeps execution keys transient while recording a redacted response', async () => {
  const executionKey = 'execution-key-canary';
  const provider = {
    getAuthorization: async () => ['B', 'e', 'a', 'r', 'e', 'r', ' ', 'test-only-memory-value'].join(''),
  };
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () =>
    new Response(JSON.stringify({ execution_key: executionKey }), { status: 200 });

  try {
    const client = new AgentweaverClient({
      baseUrl: 'https://agentweaver.example.staging.example',
      authProvider: provider,
    });
    const response = await client.post('/api/ai/execution-context', { operation: 'blueprint_generation' });
    assert.equal(response.responseBody.execution_key, '[REDACTED]');
    assert.equal(response.transientResponseBody.execution_key, executionKey);
    assert.doesNotMatch(JSON.stringify(client.calls), new RegExp(executionKey));
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
