import assert from 'node:assert/strict';
import test from 'node:test';

import { redact } from '../redaction.mjs';

test('redact strips URL userinfo, query names and values, and fragments recursively', () => {
  const canary = 'credential-canary';
  const result = redact({
    command: ['node', `https://user:${canary}@example.test/path?${canary}=${canary}#${canary}`],
    error: `request failed: https://example.test/path?${canary}=${canary}#${canary}`,
    nested: { token: canary },
  });
  const persisted = JSON.stringify(result);
  assert.doesNotMatch(persisted, new RegExp(canary));
  assert.match(result.command[1], /^https:\/\/example\.test\/path$/);
  assert.equal(result.nested.token, '[REDACTED]');
});
