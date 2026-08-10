import test from 'node:test';
import assert from 'node:assert/strict';
import { computeDriverP0 } from '../lib/reporter-ui.mjs';

test('only deterministic browser facts fail P0', () => {
  const result = computeDriverP0([{ id: 1, assertions: [{ required: true, observed: false, target: 'notification-type-badge' }], console: [{ type: 'error', text: 'boom' }], network: [{ userFacing: true, status: 500, method: 'GET', url: '/api/x' }] }]);
  assert.equal(result.pass, false);
  assert.deepEqual(result.failures.map((item) => item.kind), ['required-element-missing', 'console-error', 'user-facing-network-error']);
});

test('a recorded action failure fails P0 with its evidence', () => {
  const result = computeDriverP0([{
    id: 2,
    action: 'drag',
    outcome: 'failed',
    error: { message: 'drag target did not resolve' },
  }]);

  assert.equal(result.pass, false);
  assert.deepEqual(result.failures, [{
    kind: 'action-failed',
    turn: 2,
    evidence: 'drag target did not resolve',
  }]);
});
