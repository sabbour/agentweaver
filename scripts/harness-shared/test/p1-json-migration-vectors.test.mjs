import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { sealP1NativeProbeEnvelope } from '../p1-acceptance-result.mjs';

const vectors = JSON.parse(readFileSync(fileURLToPath(new URL('../../../tests/fixtures/p1-json-migration-vectors.json', import.meta.url)), 'utf8'));

test('native probe receipt checksums match the shared JavaScript JSON migration vectors', () => {
  for (const vector of vectors) {
    const expected = createHash('sha256').update(vector.json).digest('hex');
    assert.equal(
      sealP1NativeProbeEnvelope(vector.value).receiptSha256,
      expected,
      vector.name,
    );
  }
});
