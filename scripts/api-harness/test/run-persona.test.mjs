import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, rm } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { AgentweaverClient } from '../lib/client.mjs';
import { writeFinding } from '../lib/reporter.mjs';
import { parseArgs, resolveAuthProvider, resolveTargetRevision } from '../run-persona.mjs';
import { serializeRedactedJsonLine } from '../../harness-shared/safe-jsonl.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));

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

test('API client preserves the cancellation reason instead of returning a status-zero response', async (t) => {
  const originalFetch = globalThis.fetch;
  t.after(() => { globalThis.fetch = originalFetch; });
  const controller = new AbortController();
  globalThis.fetch = async (_url, init) => new Promise((_resolve, reject) => {
    init.signal.addEventListener('abort', () => reject(init.signal.reason), { once: true });
    controller.abort(new Error('SIGTERM'));
  });
  const client = new AgentweaverClient({ baseUrl: 'https://api.example.test', token: 'secret' });
  await assert.rejects(client.get('/api/projects', { signal: controller.signal }), /SIGTERM/);
  assert.equal(client.calls.length, 0);
});

test('API transcript fixture and packaged finding redact descriptor credentials', async () => {
  const directory = join(HERE, '..', 'verdicts', `test-${randomUUID()}`);
  const findingPath = join(directory, 'finding.json');
  const canary = 'credential-canary-api-package-42';
  const exchange = {
    request: {
      executionContext: { name: 'provider key', value: canary },
      safe: { correlationId: 'corr-42' },
    },
    response: JSON.stringify([{ header: 'Authorization', value: `Bearer ${canary}`, status: 200 }]),
  };
  const transcript = serializeRedactedJsonLine(exchange);
  try {
    await writeFinding({ transcript: exchange, status: 'completed' }, findingPath);
    const packaged = await readFile(findingPath, 'utf8');
    assert.equal(transcript.includes(canary), false);
    assert.equal(packaged.includes(canary), false);
    assert.match(transcript, /"name":"provider key"/);
    assert.match(packaged, /"correlationId": "corr-42"/);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
