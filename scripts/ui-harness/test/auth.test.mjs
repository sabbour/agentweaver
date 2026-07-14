import test from 'node:test';
import assert from 'node:assert/strict';
import { isAuthExpired, loadStorageState } from '../lib/auth.mjs';

test('detects login redirects and authentication statuses', () => {
  assert.equal(isAuthExpired({ url: 'https://example.staging.test/login' }), true);
  assert.equal(isAuthExpired({ status: 401 }), true);
  assert.equal(isAuthExpired({ url: 'https://example.staging.test/projects' }), false);
});

test('missing stored state is an explicit AUTH_EXPIRED result', async () => {
  await assert.rejects(loadStorageState('does-not-exist.storageState.json'), (error) => error.code === 'AUTH_EXPIRED');
});
