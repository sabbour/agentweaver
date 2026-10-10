import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { appendRedactedJsonLine, serializeRedactedJsonLine } from '../safe-jsonl.mjs';

test('serializes only redacted enumerable evidence and preserves numeric usage counters', async () => {
  const record = { status: 200, responseBody: { token: 'credential-canary-jsonl', inputTokens: 9 } };
  Object.defineProperty(record, 'transientResponseBody', { value: { token: 'credential-canary-raw' } });
  const line = serializeRedactedJsonLine(record);
  assert.doesNotMatch(line, /credential-canary|transientResponseBody/);
  assert.deepEqual(JSON.parse(line), { status: 200, responseBody: { token: '[REDACTED]', inputTokens: 9 } });
  const folder = await mkdtemp(path.join(tmpdir(), 'agentweaver-p1-jsonl-'));
  try {
    const file = path.join(folder, 'evidence.jsonl');
    await appendRedactedJsonLine(file, record);
    await appendRedactedJsonLine(file, { status: 409 });
    assert.deepEqual((await readFile(file, 'utf8')).trim().split('\n').map(JSON.parse),
      [JSON.parse(line), { status: 409 }]);
  } finally {
    await rm(folder, { recursive: true });
  }
});
