import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { validatePersonaBrief, validatePersonaCore, validateSurfaceAdapter } from '../persona-schema.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');

test('validates a checked-in surface-agnostic core', async () => {
  const core = await readFile(path.join(root, 'personas', 'priya.md'), 'utf8');
  const result = validatePersonaCore(core);
  assert.equal(result.valid, true, result.errors.join('\n'));
  assert.equal(result.name, 'Priya Nair');
});

test('rejects a core with surface-specific action language', async () => {
  const core = await readFile(path.join(root, 'personas', 'priya.md'), 'utf8');
  const result = validatePersonaCore(`${core}\nUse curl to submit the goal.\n`);
  assert.equal(result.valid, false);
  assert.ok(result.errors.some((error) => error.includes('surface-specific')));
});

test('validates adapters and rejects an adapter for the wrong requested surface', async () => {
  const adapter = await readFile(path.join(root, 'surfaces', 'priya.api.md'), 'utf8');
  assert.equal(validateSurfaceAdapter(adapter, 'api').valid, true);
  const mismatch = validateSurfaceAdapter(adapter, 'ui');
  assert.equal(mismatch.valid, false);
  assert.ok(mismatch.errors.some((error) => error.includes('does not match requested surface')));
});

test('requires a matching core and adapter identity', async () => {
  const core = await readFile(path.join(root, 'personas', 'priya.md'), 'utf8');
  const adapter = await readFile(path.join(root, 'surfaces', 'maya.api.md'), 'utf8');
  const result = validatePersonaBrief({ core, adapter, surface: 'api' });
  assert.equal(result.valid, false);
  assert.ok(result.errors.some((error) => error.includes('does not match core')));
});
