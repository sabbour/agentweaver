import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { parseBeatPlan } from '../lib/beats.mjs';

test('Blueprint plan preserves its 22-beat clean-run readiness contract', async () => {
  const plan = await fs.readFile(new URL('../plans/blueprint-demo-beats.md', import.meta.url), 'utf8');
  const normalizedPlan = plan.replace(/\s+/g, ' ');
  const beats = parseBeatPlan(plan);

  assert.equal(beats.length, 22);
  assert.deepEqual(beats.map((beat) => beat.id), [
    '0.1',
    '1.1', '1.2', '1.3',
    '2.1', '2.2', '2.3', '2.4', '2.5', '2.6', '2.7', '2.8',
    '3.1', '3.2',
    '4.1', '4.2', '4.3', '4.4', '4.5', '4.6', '4.7',
    '5.1',
  ]);
  assert.match(normalizedPlan, /#721 .*#722 .*#723 .*#724/);
  assert.match(normalizedPlan, /issues\.labeled` delivery/);
  assert.match(normalizedPlan, /do not substitute a UI-started workflow while narrating assistant parity/);
  assert.match(normalizedPlan, /mock preview nor a placeholder PR/);
});

test('Blueprint 2.1 capture budget allows bounded staging variance without changing its target', async () => {
  const capturePlan = JSON.parse(await fs.readFile(
    new URL('../plans/blueprint-demo.capture.json', import.meta.url),
    'utf8',
  ));
  const frameProduct = capturePlan.beats.find((beat) => beat.id === '2.1');

  assert.deepEqual(frameProduct.outputBudgetMs, {
    minimum: 12000,
    preferred: 20000,
    maximum: 32000,
  });
});
