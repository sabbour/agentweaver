import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import test from 'node:test';
import { parseBeatPlan } from '../lib/beats.mjs';
import { joinCaptureConfig, validateCaptureConfig } from '../lib/capture-config.mjs';

const narration = 'Agentweaver hands sign-in to Microsoft Entra.';

async function readMarkdownPlan(name) {
  return fs.readFile(new URL(`../plans/${name}`, import.meta.url), 'utf8');
}

async function readCapturePlan(name) {
  return JSON.parse(await fs.readFile(new URL(`../plans/${name}`, import.meta.url), 'utf8'));
}

test('Entra sign-in handoffs are present, concise, and explicitly human-safe on every story surface', async () => {
  const [blueprintSource, azureSource, sizzleSource] = await Promise.all([
    readMarkdownPlan('blueprint-demo-beats.md'),
    readMarkdownPlan('azure-aks-demo-beats.md'),
    readMarkdownPlan('sizzle-reel-beats.md'),
  ]);

  const blueprintBeat = parseBeatPlan(blueprintSource).find((beat) => beat.id === '0.1');
  const azureBeat = parseBeatPlan(azureSource).find((beat) => beat.id === '0.0');
  const sizzleBeat = parseBeatPlan(sizzleSource).find((beat) => beat.id === '0.0');

  for (const beat of [blueprintBeat, azureBeat, sizzleBeat]) {
    assert.ok(beat, 'expected a sign-in handoff beat');
    assert.equal(beat.narrationSource, narration);
    assert.match(beat.markdown, /cut (?:out )?before any identity-provider action/i);
    assert.match(beat.markdown, /do not automate/i);
    assert.match(beat.markdown, /password.*MFA.*consent/is);
    assert.match(beat.markdown, /token.*cookie.*personal(?:\/account)? content/is);
  }
  assert.match(sizzleBeat.markdown, /DOM cue:.*Sign in with Microsoft Entra ID/s);
});

test('Entra capture beats stop at the dialog and use bounded DOM-only cues', async () => {
  const [blueprintMarkdown, azureMarkdown, blueprintCapture, azureCapture] = await Promise.all([
    readMarkdownPlan('blueprint-demo-beats.md'),
    readMarkdownPlan('azure-aks-demo-beats.md'),
    readCapturePlan('blueprint-demo.capture.json'),
    readCapturePlan('azure-aks-demo.capture.json'),
  ]);

  const plans = [
    { id: '0.1', cue: 's1.0.1.entra-dialog', markdown: blueprintMarkdown, capture: blueprintCapture },
    { id: '0.0', cue: 's2.0.0.entra-dialog', markdown: azureMarkdown, capture: azureCapture },
  ];

  for (const plan of plans) {
    assert.doesNotThrow(() => validateCaptureConfig(plan.capture));
    assert.doesNotThrow(() => joinCaptureConfig(parseBeatPlan(plan.markdown), plan.capture));

    const beat = plan.capture.beats.find((candidate) => candidate.id === plan.id);
    assert.ok(beat, `expected capture beat ${plan.id}`);
    assert.equal(beat.disableApprovalWatcher, true);
    assert.deepEqual(beat.outputBudgetMs, { minimum: 3500, preferred: 5000, maximum: 7000 });
    assert.deepEqual(beat.expectedCues, [plan.cue]);
    assert.deepEqual(beat.cueOrder, [plan.cue]);
    assert.deepEqual(beat.steps.map((step) => step.type), ['badge', 'waitFor', 'pause']);
    assert.match(beat.steps[1].selector, /Sign in with Microsoft Entra ID/);
    assert.equal(beat.steps.some((step) => /click|type|select|press|goto|drag/.test(step.type)), false);
  }
});
