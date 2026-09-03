import test from 'node:test';
import assert from 'node:assert/strict';
import { evidenceHash, redact } from '../lib/evidence.mjs';

test('redacts storage state and credentials before transcript serialization', () => {
  assert.deepEqual(redact({ storageState: 'cookie', nested: { authorization: 'Bearer x' }, safe: 'shown' }), {
    storageState: '[REDACTED]', nested: { authorization: '[REDACTED]' }, safe: 'shown',
  });

});

test('strips userinfo, query strings, fragments, and nested URL canaries from UI evidence', () => {
    const canary = 'url-canary-ui-42';
    const result = redact({
      url: `https://user:${canary}@example.test/projects?filter=${canary}#${canary}`,
      network: [{ url: `https://example.test/api/projects?token=${canary}#fragment` }],
      error: { message: `failed at https://example.test/path?q=${canary}#${canary}` },
    });
    const serialized = JSON.stringify(result);
    assert.doesNotMatch(serialized, new RegExp(canary));
    assert.equal(result.url, 'https://example.test/projects');
    assert.equal(result.network[0].url, 'https://example.test/api/projects');
    assert.equal(result.error.message, 'failed at https://example.test/path');
});

test('evidence hash is deterministic', () => {
  assert.equal(evidenceHash('evidence'), evidenceHash('evidence'));
});
