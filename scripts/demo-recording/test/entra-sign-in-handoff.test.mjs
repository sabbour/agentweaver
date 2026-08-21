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

test("all Beat 0 narratives permit only Agentweaver's redirect before the Entra boundary", async () => {
  const plans = await Promise.all([
    readMarkdownPlan('blueprint-demo-beats.md'),
    readMarkdownPlan('sabbour-aks-demo-beats.md'),
    readMarkdownPlan('sizzle-reel-beats.md'),
  ]);

  for (const source of plans) {
    const beat = parseBeatPlan(source).find((candidate) => candidate.id === '0.0');
    assert.ok(beat, 'expected Beat 0.0');
    assert.ok(beat.narrationSource && beat.narrationSource.length > 0, 'expected non-empty Beat 0.0 narration');
    assert.doesNotMatch(beat.narrationSource, /enter|type|password|credential|account|click.*entra|select.*account/i, 'Beat 0.0 narration must not describe Entra sign-in interactions');
    assert.match(beat.markdown, /may click Agentweaver's/i);
    assert.match(beat.markdown, /own\s+button to start the\s+redirect/i);
    assert.match(beat.markdown, /cached SSO may complete it/i);
    assert.match(beat.markdown, /Cut as soon as Microsoft\s+Entra is reached/i);
    assert.match(beat.markdown, /do not\s+select an account, type credentials, interact with MFA or\s+consent/i);
    assert.match(beat.markdown, /tokens,\s+cookies,\s+profile data/i);
    assert.match(beat.markdown, /privately\s+and off camera/i);
  }
});

test('Beat 0 plans are isolated, unauthenticated, and excluded from authenticated all capture', async () => {
  const plans = [
    ['blueprint-demo-beats.md', 'blueprint-demo.capture.json'],
    ['sabbour-aks-demo-beats.md', 'sabbour-aks-demo.capture.json'],
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
