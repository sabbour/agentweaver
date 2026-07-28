import test from 'node:test';
import assert from 'node:assert/strict';
import { parseBeatPlan } from '../lib/beats.mjs';
import { buildKeepSegments, summarizeTrim } from '../lib/pacing.mjs';
import { classifyZoom } from '../lib/zoom.mjs';

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
