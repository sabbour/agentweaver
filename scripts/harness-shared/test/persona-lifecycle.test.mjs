import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, rm } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';

import {
  formatLifecycleLines,
  judgeLifecycleEvidence,
  loadLifecyclePersona,
  lifecycleExitCode,
  parseLifecycleArgs,
  runLifecycleCli,
  writeLifecycleJson,
} from '../persona-lifecycle.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));

test('shared lifecycle parser retains each adapter option names and aliases', () => {
  const options = {
    '--scenario': 'scenario', '--persona': 'scenario', '--target': 'target',
    '--base-url': 'target', '--keep': true,
  };
  assert.deepEqual(
    parseLifecycleArgs(['--persona', 'priya', '--base-url', 'stdio', '--keep'], { options }),
    { scenario: 'priya', target: 'stdio', keep: true },
  );
  assert.throws(() => parseLifecycleArgs(['--token=not-echoed'], { options }), /--token/);
});

test('shared lifecycle preserves deterministic, inconclusive, and setup exit behavior', async () => {
  assert.equal(lifecycleExitCode(), 0);
  assert.equal(lifecycleExitCode({ failed: true }), 1);
  assert.equal(lifecycleExitCode({ inconclusive: true }), 3);
  const exits = [];
  await runLifecycleCli(async () => 3, { redact: (value) => value, exit: (code) => exits.push(code) });
  await runLifecycleCli(async () => { throw new Error('boom'); }, {
    redact: (value) => value, exit: (code) => exits.push(code), error: () => {},
  });
  assert.deepEqual(exits, [3, 2]);
});

test('shared lifecycle formatting does not alter adapter result labels', () => {
  assert.deepEqual(formatLifecycleLines([['Persona', 'Priya'], ['Verdict', 'p0=PASS']]), [
    'Persona     : Priya',
    'Verdict     : p0=PASS',
  ]);
});

test('shared lifecycle delegates persona loading to the selected surface adapter', async () => {
  const load = async (scenario, surface) => ({ scenario, surface });
  assert.deepEqual(await loadLifecyclePersona(load, 'priya', 'mcp'), { scenario: 'priya', surface: 'mcp' });
  assert.equal(await loadLifecyclePersona(async () => { throw new Error('absent'); }, 'seam', 'api', { optional: true }), null);
});

test('shared lifecycle redacts descriptor credentials before persistence and judging', async () => {
  const directory = join(HERE, `tmp-${randomUUID()}`);
  const path = join(directory, 'lifecycle.json');
  const canary = 'credential-canary-lifecycle-42';
  const evidence = {
    metadata: { status: 'completed', correlationId: 'corr-42' },
    executionContext: { name: 'Authorization', value: `Bearer ${canary}` },
  };
  try {
    await writeLifecycleJson(path, evidence);
    const persisted = await readFile(path, 'utf8');
    assert.equal(persisted.includes(canary), false);
    assert.match(persisted, /"name": "Authorization"/);

    let judgedEvidence;
    await judgeLifecycleEvidence(evidence, async (value) => {
      judgedEvidence = value;
      return { verdict: 'PASS' };
    });
    assert.equal(JSON.stringify(judgedEvidence).includes(canary), false);
    assert.deepEqual(judgedEvidence.metadata, evidence.metadata);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
