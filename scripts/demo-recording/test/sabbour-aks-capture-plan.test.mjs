import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import test from 'node:test';
import { parseBeatPlan } from '../lib/beats.mjs';
import { joinCaptureConfig, validateCaptureConfig } from '../lib/capture-config.mjs';

const beatIds = ['0.0', '0.1', '1.1', '1.2', '2.2', '2.3', '3.1', '3.2', '4.4', '5.1'];

test('sabbour/AKS capture plan has a complete, gated capture-all sequence', async () => {
  const [markdown, captureText] = await Promise.all([
    fs.readFile(new URL('../plans/azure-aks-demo-beats.md', import.meta.url), 'utf8'),
    fs.readFile(new URL('../plans/azure-aks-demo.capture.json', import.meta.url), 'utf8'),
  ]);
  const plan = JSON.parse(captureText);
  const beats = parseBeatPlan(markdown);

  assert.equal(plan.requireAllBeats, true);
  assert.deepEqual(plan.authentication, { mode: 'entra', repository: 'sabbour/AKS' });
  assert.deepEqual(plan.fixture, {
    projectName: 'Agentweaver Demo S2 — sabbour/AKS',
    safeProjectNamePatterns: ['^Agentweaver Demo S2 — sabbour/AKS$'],
  });
  assert.deepEqual(beats.map((beat) => beat.id), beatIds);
  assert.deepEqual(plan.beats.map((beat) => beat.id), beatIds);

  for (const [index, beat] of plan.beats.entries()) {
    assert.ok(beat.videoPath, `beat ${beat.id} needs an output path`);
    assert.ok(beat.expectedCues?.length, `beat ${beat.id} needs a capture gate`);
    assert.deepEqual(beat.cueOrder, beat.expectedCues, `beat ${beat.id} cue order must gate every cue`);
    if (index > 0) {
      assert.equal(beat.requiresPriorBeat, beatIds[index - 1], `beat ${beat.id} must preserve fixture continuity`);
    }
  }

  const workflowBeat = plan.beats.find((beat) => beat.id === '2.2');
  assert.ok(workflowBeat, 'workflow-generation beat must be included in capture --all');
  assert.deepEqual(workflowBeat.expectedCues, [
    's2.2.2.workflow-dialog',
    's2.2.2.workflow-graph',
    's2.2.2.schedule-saved',
    's2.2.2.event-saved',
  ]);
  assert.ok(workflowBeat.steps.some((step) => step.cue?.name === 's2.2.2.workflow-graph'));

  const createProjectBeat = plan.beats.find((beat) => beat.id === '1.1');
  const projectNameStep = createProjectBeat.steps.find((step) => step.type === 'type'
    && step.selector === "page.getByLabel('Project name')");
  assert.equal(projectNameStep?.text, plan.fixture.projectName);

  assert.doesNotThrow(() => validateCaptureConfig(plan));
  assert.doesNotThrow(() => joinCaptureConfig(beats, plan, { requireAllBeats: true }));
  assert.doesNotMatch(`${markdown}\n${captureText}`, /\bAzure\/AKS\b/);
});
