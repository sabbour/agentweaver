import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import test from 'node:test';
import { parseBeatPlan } from '../lib/beats.mjs';
import { joinCaptureConfig, validateCaptureConfig } from '../lib/capture-config.mjs';
import { renderCaptureScript } from '../lib/capture-plan.mjs';
import { selectCaptureBeats } from '../lib/recording-session.mjs';

async function readMarkdownPlan(name) {
  return fs.readFile(new URL(`../plans/${name}`, import.meta.url), 'utf8');
}

async function readCapturePlan(name) {
  return JSON.parse(await fs.readFile(new URL(`../plans/${name}`, import.meta.url), 'utf8'));
}

test('all Beat 0 narratives stop at the human-only Entra handoff', async () => {
  const plans = await Promise.all([
    readMarkdownPlan('blueprint-demo-beats.md'),
    readMarkdownPlan('azure-aks-demo-beats.md'),
    readMarkdownPlan('sizzle-reel-beats.md'),
  ]);

  for (const source of plans) {
    const beat = parseBeatPlan(source).find((candidate) => candidate.id === '0.0');
    assert.ok(beat, 'expected Beat 0.0');
    assert.equal(beat.narrationSource, 'Agentweaver hands sign-in to Microsoft Entra.');
    assert.match(beat.markdown, /before any identity-provider\s+action/i);
    assert.match(beat.markdown, /do not select an\s+account, type credentials, interact with MFA or consent/i);
    assert.match(beat.markdown, /tokens, cookies, or\s+profile data/i);
    assert.match(beat.markdown, /privately\s+and off camera/i);
  }
});

test('Beat 0 plans are unauthenticated, passive, and excluded from authenticated all capture', async () => {
  const plans = [
    ['blueprint-demo-beats.md', 'blueprint-demo.capture.json'],
    ['azure-aks-demo-beats.md', 'azure-aks-demo.capture.json'],
  ];
  for (const [markdownName, captureName] of plans) {
    const [markdown, capture] = await Promise.all([
      readMarkdownPlan(markdownName),
      readCapturePlan(captureName),
    ]);
    assert.doesNotThrow(() => validateCaptureConfig(capture));
    assert.doesNotThrow(() => joinCaptureConfig(parseBeatPlan(markdown), capture));
    const beat = capture.beats.find((candidate) => candidate.id === '0.0');
    assert.equal(beat.captureMode, 'unauthenticated');
    assert.deepEqual(beat.steps.map((step) => step.type), ['badge', 'waitFor', 'pause']);
    assert.match(beat.steps[1].selector, /Sign in with Microsoft Entra ID/);
    const authenticatedAll = selectCaptureBeats(capture.beats, { all: true });
    assert.equal(authenticatedAll[0].id, capture.beats.find((candidate) => candidate.captureMode !== 'unauthenticated').id);
    assert.deepEqual(selectCaptureBeats(capture.beats, { beat: '0.0', unauthenticated: true }).map((candidate) => candidate.id), ['0.0']);
    assert.throws(() => selectCaptureBeats(capture.beats, { beat: '0.0' }), /requires --unauthenticated/);

    const script = renderCaptureScript(beat);
    assert.match(script, /Sign in with Microsoft Entra ID/);
    assert.doesNotMatch(script, /page\.getByRole\('button', \{ name: 'Sign in with Microsoft Entra ID'/);
    for (const blocked of ['await typeInto(', 'await locator.selectOption(', '.dragTo(']) {
      assert.doesNotMatch(script, new RegExp(blocked.replace(/[().]/g, '\\$&')));
    }
  }
});
