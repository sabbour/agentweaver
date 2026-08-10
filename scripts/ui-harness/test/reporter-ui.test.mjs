import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { computeDriverP0 } from '../lib/reporter-ui.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));

test('only deterministic browser facts fail P0', () => {
  const result = computeDriverP0([{ id: 1, assertions: [{ required: true, observed: false, target: 'notification-type-badge' }], console: [{ type: 'error', text: 'boom' }], network: [{ userFacing: true, status: 500, method: 'GET', url: '/api/x' }] }]);
  assert.equal(result.pass, false);
  assert.deepEqual(result.failures.map((item) => item.kind), ['required-element-missing', 'console-error', 'user-facing-network-error']);
});

test('finish fails closed when capture evidence is only the authentication loading shell', async () => {
  const domSnapshot = JSON.parse(await readFile(path.join(HERE, 'fixtures', 'auth-loading-dom.json'), 'utf8'));
  const result = computeDriverP0([{
    id: 1,
    action: 'capture',
    url: 'https://agentweaver.example.staging/projects',
    domSnapshot,
  }]);

  assert.equal(result.pass, false);
  assert.deepEqual(result.failures.map((item) => item.kind), ['auth-loading-shell']);
});

test('finish fails closed when a capture command failed before evidence was recorded', () => {
  const result = computeDriverP0([], [{
    id: 1,
    action: 'capture',
    code: 'AUTH_EXPIRED',
    message: 'AUTH_EXPIRED: Loading sign-in options',
  }]);

  assert.equal(result.pass, false);
  assert.deepEqual(result.failures.map((item) => item.kind), ['no-evidence', 'command-failed']);
});
