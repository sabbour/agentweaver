import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgentweaverClient } from '../lib/client.mjs';
import { resolveToken } from '../run-persona.mjs';

test('remote API auth accepts only an explicit Agentweaver token source', () => {
  assert.equal(resolveToken('explicit', { GITHUB_TOKEN: 'github-canary' }), 'explicit');
  assert.equal(resolveToken(null, { AGENTWEAVER_TOKEN: 'agentweaver', GITHUB_TOKEN: 'github-canary' }), 'agentweaver');
  assert.equal(resolveToken(null, { GITHUB_TOKEN: 'github-canary', GH_TOKEN: 'gh-canary' }), null);
});

test('API client accepts an arbitrary HTTPS host and rejects insecure remote transport', () => {
  assert.doesNotThrow(() => new AgentweaverClient({ baseUrl: 'https://example.internal', token: 'x' }));
  assert.throws(() => new AgentweaverClient({ baseUrl: 'http://example.internal', token: 'x' }), /HTTPS is required/);
});

test('API credentials cannot be sent to an attacker-controlled absolute path', async () => {
  const client = new AgentweaverClient({ baseUrl: 'https://api.example.test', token: 'secret' });
  await assert.rejects(client.get('https://attacker.example/collect'), /outside configured origin/);
});

test('API client refuses a cross-origin redirect before forwarding credentials', async (t) => {
  const originalFetch = globalThis.fetch;
  t.after(() => { globalThis.fetch = originalFetch; });
  let calls = 0;
  globalThis.fetch = async () => {
    calls += 1;
    return new Response(null, { status: 302, headers: { location: 'https://attacker.example/collect' } });
  };
  const client = new AgentweaverClient({ baseUrl: 'https://api.example.test', token: 'secret' });
  const result = await client.get('/api/projects');
  assert.equal(result.status, 0);
  assert.match(result.responseBody.message, /cross-origin API redirect/);
  assert.equal(calls, 1);
});
