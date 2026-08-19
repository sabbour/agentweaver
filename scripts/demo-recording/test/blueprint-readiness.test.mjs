import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import { parseBeatPlan } from '../lib/beats.mjs';

test('Blueprint plan preserves its isolated handoff and 21-beat authenticated readiness contract', async () => {
  const plan = await fs.readFile(new URL('../plans/blueprint-demo-beats.md', import.meta.url), 'utf8');
  const normalizedPlan = plan.replace(/\s+/g, ' ');
  const beats = parseBeatPlan(plan);

  assert.equal(beats.length, 22);
  assert.deepEqual(beats.map((beat) => beat.id), [
    '0.0',
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

test('Blueprint 3.1 selects the Cadence combobox before saving the schedule', async () => {
  const capturePlan = JSON.parse(await fs.readFile(
    new URL('../plans/blueprint-demo.capture.json', import.meta.url),
    'utf8',
  ));
  const schedule = capturePlan.beats.find((beat) => beat.id === '3.1');
  const cadenceIndex = schedule.steps.findIndex((step) => step.type === 'select'
    && step.selector === "page.getByRole('combobox', { name: 'Cadence' })");
  const saveScheduleIndex = schedule.steps.findIndex((step) => step.type === 'click'
    && step.selector === "page.getByRole('button', { name: /Save schedule|Schedule workflow/i }).first()");

  assert.deepEqual(
    schedule.steps.slice(cadenceIndex, saveScheduleIndex + 1).map(({ type, selector }) => ({ type, selector })),
    [
      {
        type: 'select',
        selector: "page.getByRole('combobox', { name: 'Cadence' })",
      },
      {
        type: 'click',
        selector: "page.getByRole('button', { name: /Save schedule|Schedule workflow/i }).first()",
      },
    ],
  );
});
