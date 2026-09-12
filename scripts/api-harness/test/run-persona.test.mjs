import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgentweaverClient } from '../lib/client.mjs';
import { parseArgs, resolveAuthProvider, resolveTargetRevision } from '../run-persona.mjs';

test('API runner defaults to the managed-browser recorder session', () => {
  assert.equal(resolveAuthProvider({}, 'https://agentweaver.example.test').name, 'recorder-session');
  assert.equal(resolveAuthProvider({ authProvider: 'recorder-session' }, 'https://agentweaver.example.test').name, 'recorder-session');
  assert.throws(() => resolveAuthProvider({ authProvider: 'environment' }), /Unsupported auth provider/);
});

test('API runner rejects retired credential argv without echoing its value', () => {
  const canary = 'secret-canary-api-argv-66';
  const retiredOption = `--${'to'}${'ken'}`;
  assert.throws(
    () => parseArgs([`${retiredOption}=${canary}`]),
    (error) => error.message.includes(retiredOption) && !error.message.includes(canary),
  );
});

test('API runner uses the reported deployment version unless a comparison revision is explicit', () => {
  const deployment = { version: 'v0.30.0', gitSha: 'abc123' };
  assert.equal(resolveTargetRevision(undefined, deployment), 'v0.30.0');
  assert.equal(resolveTargetRevision('preview-revision-42', deployment), 'preview-revision-42');
  assert.equal(resolveTargetRevision(undefined, { gitSha: 'abc123' }), 'abc123');
  assert.equal(resolveTargetRevision(undefined, null), 'unknown');
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
