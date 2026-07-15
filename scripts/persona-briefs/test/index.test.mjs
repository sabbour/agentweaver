import assert from 'node:assert/strict';
import { test } from 'node:test';
import { listPersonas, loadPersona, loadPersonaCore, loadSurfaceAdapter } from '../index.mjs';
import { assembleCoreGenerationPrompt } from '../generate-core.mjs';
import { assembleAdapterGenerationPrompt } from '../generate-adapter.mjs';

test('lists shared cores and filters them by available adapter', async () => {
  assert.deepEqual(await listPersonas(), ['jordan', 'lena', 'maya', 'oracle', 'priya']);
  assert.deepEqual(await listPersonas({ surface: 'api' }), ['jordan', 'lena', 'maya', 'oracle', 'priya']);
  assert.deepEqual(await listPersonas({ surface: 'ui' }), ['jordan', 'maya', 'priya']);
});

test('loads and combines a validated core and API adapter', async () => {
  const persona = await loadPersona('jordan', 'api');
  assert.equal(persona.id, 'jordan');
  assert.equal(persona.surface, 'api');
  assert.match(persona.version, /^jordan@[a-f0-9]{12}$/);
  assert.match(persona.adapter.version, /^jordan\.api@[a-f0-9]{12}$/);
  assert.match(persona.text, /Persona core: Jordan Lee/);
  assert.match(persona.text, /Persona surface adapter: Jordan Lee — api/);
});

test('loads UI adapters and reports invalid persona names clearly', async () => {
  const adapter = await loadSurfaceAdapter('jordan', 'ui');
  assert.equal(adapter.surface, 'ui');
  await assert.rejects(() => loadPersonaCore('../jordan'), /Invalid persona name/);
});

test('loads an API adapter whose core uses judgment instead of mandatory pushback', async () => {
  const persona = await loadPersona('oracle', 'api');
  assert.equal(persona.id, 'oracle');
  assert.match(persona.text, /## Judgment, not a script/);
  assert.match(persona.text, /live OpenAPI spec/);
  assert.match(persona.text, /does \*\*not\*\* prescribe phases, checkpoints, product shape, or step/);
  assert.match(persona.text, /preview "validated"/);
});

test('core generator creates a provider-neutral prompt from free text', () => {
  const prompt = assembleCoreGenerationPrompt({
    description: 'Test a finance operations manager reconciling disputed invoices.',
    exclude: ['priya', 'maya'],
  });
  assert.match(prompt, /finance operations manager/i);
  assert.match(prompt, /surface-agnostic/i);
  assert.match(prompt, /Do not mention APIs, HTTP, curl, buttons, clicks, tool names/);
});

test('adapter generator maps a validated core to one target surface', async () => {
  const core = await loadPersonaCore('maya');
  const prompt = assembleAdapterGenerationPrompt({ core: core.content, surface: 'mcp' });
  assert.match(prompt, /Target surface: mcp/);
  assert.match(prompt, /Persona surface adapter: <Persona Name> — mcp/);
  assert.throws(() => assembleAdapterGenerationPrompt({ core: core.content, surface: 'desktop' }), /surface must be one of/);
});
