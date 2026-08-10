import test from 'node:test';
import assert from 'node:assert/strict';
import { init } from '../agent-driver-ui/tools.mjs';

test('scenario initialization fails with AUTH_EXPIRED when stored auth is missing', async () => {
  await assert.rejects(
    init({
      persona: 'priya',
      'base-url': 'https://agentweaver.example.staging',
      'storage-state': 'does-not-exist.storageState.json',
    }),
    (error) => error.code === 'AUTH_EXPIRED',
  );
});
