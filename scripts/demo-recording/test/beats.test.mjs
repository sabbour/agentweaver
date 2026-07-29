import test from 'node:test';
import assert from 'node:assert/strict';
import { parseBeatPlan } from '../lib/beats.mjs';
import { buildKeepSegments, summarizeTrim } from '../lib/pacing.mjs';
import { classifyZoom } from '../lib/zoom.mjs';
import { renderCaptureScript } from '../lib/capture-plan.mjs';

test('parseBeatPlan extracts beats and narration', () => {
  const beats = parseBeatPlan(`
## Beat 2.5 — Ship it

Narration: “Open the preview and inspect the live page.”

**BLOCKED(example-blocker)**
`);
  assert.equal(beats.length, 1);
  assert.equal(beats[0].id, '2.5');
  assert.equal(beats[0].title, 'Ship it');
  assert.equal(beats[0].narrationSource, 'Open the preview and inspect the live page.');
  assert.deepEqual(beats[0].blockers, ['example-blocker']);
});

test('parseBeatPlan handles CRLF line endings and ignores On screen annotations', () => {
  const crlf = '## Beat 3.1 — Schedule\r\n\r\nNarration: "Put the workflow on a schedule."\r\n\r\nOn screen: operate on the real workflow, not a stray copy.\r\n\r\n## Beat 3.2 — Webhook\r\n\r\nNarration: "Trigger it from GitHub."\r\n';
  const beats = parseBeatPlan(crlf);
  assert.equal(beats.length, 2);
  assert.equal(beats[0].narrationSource, 'Put the workflow on a schedule.');
  assert.ok(!beats[0].narrationSource.includes('On screen'), 'On screen annotation must not leak into narration');
  assert.equal(beats[1].narrationSource, 'Trigger it from GitHub.');
});

test('classifyZoom biases detail-heavy beats closer', () => {
  const previewZoom = classifyZoom({ title: 'Preview the repaired behavior', narrationSource: 'Preview the fix on a narrow tablet.' });
  const createZoom = classifyZoom({ title: 'Create the project', narrationSource: 'Paste the repo and name the project.' });
  assert.equal(previewZoom.semantic, 'detail');
  assert.ok(previewZoom.scale > createZoom.scale);
});

test('buildKeepSegments removes the middle of long inactive gaps', () => {
  const segments = buildKeepSegments({
    durationMs: 30000,
    events: [{ t: 0 }, { t: 2200 }, { t: 25000 }, { t: 30000 }],
    maxStaticMs: 2500,
    retainAfterActivityMs: 900,
    retainBeforeActivityMs: 1200,
  });
  assert.deepEqual(segments, [
    { startMs: 0, endMs: 3100 },
    { startMs: 23800, endMs: 25900 },
    { startMs: 28800, endMs: 30000 },
  ]);
  const summary = summarizeTrim({ durationMs: 30000, segments });
  assert.equal(summary.trimmedDurationMs, 6400);
  assert.equal(summary.removedMs, 23600);
});

test('capture script moves the cursor AFTER the zoom transform settles (recomputed box)', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [{ type: 'click', selector: "page.getByRole('button', { name: 'X' })", scale: 1.45 }],
  });
  // The zoom transform is applied first...
  const zoomIdx = src.indexOf('__demoZoomFocus');
  // ...then the element box is recomputed post-transform...
  const recomputeIdx = src.indexOf('const zbox = (await locator.boundingBox())');
  // ...and only then is the cursor pointed at the post-transform center.
  const pointIdx = src.indexOf('const pointAt');
  assert.ok(zoomIdx > 0, 'expected a zoom-focus call');
  assert.ok(recomputeIdx > 0, 'expected the post-transform bounding box recompute');
  assert.ok(pointIdx > 0, 'expected a pointAt cursor helper');
  // Regression guard: the old bug placed the cursor in the SAME evaluate() as the zoom,
  // using pre-transform coordinates. That coupling must be gone.
  assert.ok(
    !/__demoZoomFocus\?\.\(x, y, scale\); window\.__demoCursorMove\?\.\(x, y\)/.test(src),
    'cursor must not be moved to pre-zoom coordinates in the same call as the zoom',
  );
});

test('capture script treats scale <= 1.02 as no-zoom (resets transform, no pan)', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [{ type: 'click', selector: "page.getByRole('button', { name: 'Y' })", scale: 1 }],
  });
  assert.ok(src.includes('const zoom = scale > 1.02'), 'expected a no-zoom threshold branch');
  assert.ok(src.includes('__demoZoomReset'), 'expected the no-zoom path to reset any prior transform');
});

test('capture script re-installs the overlay bootstrap after every in-plan goto', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [{ type: 'goto', url: 'https://x/z', after: 500 }],
  });
  // After a full-page navigation the addInitScript bootstrap runs at document-start with
  // a null document.body and bails, so the cursor/zoom/activity tracking must be
  // re-installed explicitly once the new document is ready.
  const gotoIdx = src.indexOf("await page.goto(\"https://x/z\"");
  const reinstallIdx = src.indexOf('await page.evaluate(installSource);', gotoIdx);
  const markIdx = src.indexOf("__demoActivityMark?.('goto')");
  assert.ok(gotoIdx > 0, 'expected the goto navigation');
  assert.ok(reinstallIdx > gotoIdx, 'expected installSource to be re-evaluated after the goto');
  assert.ok(markIdx > reinstallIdx, 'expected the goto activity mark after re-install');
});

test('capture script supports eval, waitFor, forced clicks and selector-scoped press', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [
      { type: 'eval', expression: "document.querySelector('.dup')?.remove();" },
      { type: 'waitFor', selector: "page.getByTestId('dashboard-chart')", timeout: 45000 },
      { type: 'click', selector: "page.getByRole('button', { name: 'Approve' })", scale: 1, force: true },
      { type: 'press', selector: "page.getByRole('textbox')", key: 'Enter', after: 300 },
    ],
  });
  // eval runs the in-page expression (used e.g. to remove a duplicate list item before capture)
  assert.ok(src.includes("document.querySelector('.dup')?.remove();"), 'expected the eval expression to be emitted');
  assert.ok(src.includes("__demoActivityMark?.('eval')"), 'expected an eval activity mark');
  assert.ok(src.includes('await page.evaluate(async () =>'), 'expected eval to be wrapped in an async evaluate so snippets may await');
  // waitFor waits on a real element becoming visible (replaces fixed short timeouts)
  assert.ok(src.includes(".waitFor({ state: 'visible', timeout: 45000 })"), 'expected a visible waitFor with the given timeout');
  // forced clicks pass { force: true } through to locator.click
  assert.ok(src.includes('force ? { force: true } : {}'), 'expected the click helper to honor force');
  assert.ok(/await click\(.*'Approve'.*, true\);/s.test(src), 'expected the Approve click to be forced');
  // press can be scoped to a selector instead of the global keyboard
  assert.ok(src.includes(".press(\"Enter\")"), 'expected a selector-scoped press');
});

test('unknown step types are still emitted as nothing (no throw)', () => {
  assert.doesNotThrow(() => renderCaptureScript({
    startUrl: 'https://x/y', videoPath: 'a.webm', steps: [{ type: 'badge', label: 'L', title: 'T' }],
  }));
});

test('eval accepts a code alias with top-level await', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y', videoPath: 'a.webm',
    steps: [{ type: 'eval', code: 'const r = await fetch("/api/x"); await r.json();' }],
  });
  assert.ok(src.includes('await page.evaluate(async () =>'), 'expected async eval wrapper');
  assert.ok(src.includes('const r = await fetch("/api/x");'), 'expected the code snippet to be emitted');
});

test('activity tracking + capture clears persist across navigations via sessionStorage', () => {
  const src = renderCaptureScript({ startUrl: 'https://x/y', videoPath: 'a.webm', steps: [] });
  // stale per-capture state is cleared once at capture start...
  assert.ok(src.includes("sessionStorage.removeItem('__demoCaptureEpoch')"), 'expected epoch reset at capture start');
  assert.ok(src.includes("sessionStorage.removeItem('__demoActivityLog')"), 'expected activity-log reset at capture start');
  // ...and the bootstrap persists the log in sessionStorage so it survives page.goto.
  assert.ok(src.includes('__demoActivityLog'), 'expected the activity log to be persisted in sessionStorage');
  assert.ok(src.includes('__demoCaptureEpoch'), 'expected a wall-clock capture epoch');
});
