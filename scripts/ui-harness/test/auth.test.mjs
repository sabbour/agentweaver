import test from 'node:test';
import assert from 'node:assert/strict';
import { rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { isAuthExpired, loadStorageState, loadSessionStorageSeed } from '../lib/auth.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));

test('detects login redirects and authentication statuses', () => {
  assert.equal(isAuthExpired({ url: 'https://example.staging.test/login' }), true);
  assert.equal(isAuthExpired({ url: 'https://example.staging.test/auth/entra/authorize' }), true);
  assert.equal(isAuthExpired({ url: 'https://example.staging.test/auth/entra/callback?code=test' }), true);
  assert.equal(isAuthExpired({ status: 401 }), true);
  assert.equal(isAuthExpired({ url: 'https://example.staging.test/projects' }), false);
});

test('missing stored state is an explicit AUTH_EXPIRED result', async () => {
  await assert.rejects(loadStorageState('does-not-exist.storageState.json'), (error) => error.code === 'AUTH_EXPIRED');
});

test('empty stored state is an explicit AUTH_EXPIRED result', async () => {
  const statePath = path.join(HERE, 'auth-empty.storageState.json');
  await writeFile(statePath, JSON.stringify({ cookies: [], origins: [] }), 'utf8');
  try {
    await assert.rejects(
      loadStorageState(statePath),
      (error) => error.code === 'AUTH_EXPIRED' && /stored browser session is empty/i.test(String(error.message)),
    );
  } finally {
    await rm(statePath, { force: true });
  }
});

test('missing sessionStorage seed returns null rather than throwing', async () => {
  assert.equal(await loadSessionStorageSeed('does-not-exist.storageState.json'), null);
});

test('empty or malformed sessionStorage seed returns null', async () => {
  const statePath = path.join(HERE, 'auth-seed.storageState.json');
  const seedPath = `${statePath}.sessionStorage.json`;
  try {
    await writeFile(seedPath, JSON.stringify({ origin: 'https://example.staging.test', entries: {} }), 'utf8');
    assert.equal(await loadSessionStorageSeed(statePath), null, 'empty entries object is treated as absent');

    await writeFile(seedPath, JSON.stringify({ entries: { a: 'b' } }), 'utf8');
    assert.equal(await loadSessionStorageSeed(statePath), null, 'missing origin is treated as absent');
  } finally {
    await rm(seedPath, { force: true });
  }
});

test('a valid sessionStorage seed round-trips its origin and entries', async () => {
  const statePath = path.join(HERE, 'auth-seed-valid.storageState.json');
  const seedPath = `${statePath}.sessionStorage.json`;
  const payload = { origin: 'https://example.staging.test', entries: { 'agentweaver.sessionToken': 'abc123' } };
  try {
    await writeFile(seedPath, JSON.stringify(payload), 'utf8');
    assert.deepEqual(await loadSessionStorageSeed(statePath), payload);
  } finally {
    await rm(seedPath, { force: true });
  }
});
