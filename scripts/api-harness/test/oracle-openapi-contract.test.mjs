import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const adapterPath = new URL('../../persona-briefs/surfaces/oracle.api.md', import.meta.url);

test('Oracle chooses AI context operations from the live OpenAPI contract', async () => {
  const adapter = await readFile(adapterPath, 'utf8');

  assert.match(adapter, /request schema's\s+published enum/i);
  assert.match(adapter, /guarded endpoint's OpenAPI\s+description/i);
  assert.match(adapter, /Do not invent a fallback action/i);
});
