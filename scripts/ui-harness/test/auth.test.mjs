import test from 'node:test';
import assert from 'node:assert/strict';
import { rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { isAuthExpired, loadStorageState } from '../lib/auth.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));

test('detects login redirects and authentication statuses', () => {
  assert.equal(isAuthExpired({ url: 'https://example.staging.test/login' }), true);
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
