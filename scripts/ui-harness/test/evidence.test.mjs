import test from 'node:test';
import assert from 'node:assert/strict';
import { evidenceHash, redact } from '../lib/evidence.mjs';

test('redacts storage state and credentials before transcript serialization', () => {
  assert.deepEqual(redact({ storageState: 'cookie', nested: { authorization: 'Bearer x' }, safe: 'shown' }), {
    storageState: '[REDACTED]', nested: { authorization: '[REDACTED]' }, safe: 'shown',
  });
});

test('evidence hash is deterministic', () => {
  assert.equal(evidenceHash('evidence'), evidenceHash('evidence'));
});
