import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import {
  analyzeTake,
  analyzeTakeData,
  extractFrameTimeline,
} from '../lib/take-analyzer.mjs';

function frameProbe({ durationSec = 20, stepSec = 2 } = {}) {
  const frames = [];
  for (let seconds = 0; seconds <= durationSec; seconds += stepSec) {
    frames.push({
      best_effort_timestamp_time: String(seconds),
      pkt_duration_time: String(stepSec),
    });
  }
  return { format: { duration: String(durationSec) }, frames };
}

function captureConfig(overrides = {}) {
  return {
    schemaVersion: 1,
    beats: [{
      id: '1.7',
      outputBudgetMs: { minimum: 4000, preferred: 6000, maximum: 9000 },
      expectedCues: ['1.7.running', '1.7.done', '1.7.missing'],
      cueOrder: ['1.7.done', '1.7.running'],
      ...overrides,
    }],
  };
}

function cueManifest() {
  return {
    schemaVersion: 1,
    takeId: 'topology-take-1',
    videoPath: 'topology.webm',
    captureStartedAtEpochMs: 1000,
    cues: [
      {
        name: '1.7.running',
        beatId: '1.7',
        sequence: 0,
        tMs: 2600,
        rectStatus: 'captured',
      },
      {
        name: '1.7.done',
        beatId: '1.7',
        sequence: 1,
        tMs: 10000,
        rectStatus: 'missing-or-not-visible',
      },
    ],
  };
}

test('frame timeline normalizes non-zero ffprobe PTS', () => {
  const timeline = extractFrameTimeline({
    format: { duration: '4' },
    frames: [
      { best_effort_timestamp_time: '5.0', pkt_duration_time: '1' },
      { best_effort_timestamp_time: '6.0', pkt_duration_time: '1' },
    ],
  });
  assert.equal(timeline.firstPtsMs, 5000);
  assert.deepEqual(timeline.frames.map((frame) => frame.ptsMs), [0, 1000]);
});

test('take analyzer is lenient, uses three categories, and flags cue/frame drift over 500ms', () => {
  const { analysis, draftDirection } = analyzeTakeData({
    captureConfig: captureConfig(),
    cueManifest: cueManifest(),
    activityLog: [
      { kind: 'click', t: 1000 },
      { kind: 'mutation', t: 5000 },
    ],
    frameProbe: frameProbe(),
    beatId: '1.7',
  });

  assert.deepEqual(analysis.policy.intervalCategories, ['action', 'wait', 'dead-time']);
  assert.equal(analysis.policy.missingCueMode, 'warn-and-continue');
  assert.equal(analysis.policy.cueFrameToleranceMs, 500);
  assert.equal(analysis.policy.maximumContinuousRate, 12);
  assert.deepEqual(analysis.beats[0].intervals.map((interval) => interval.category), [
    'action',
    'wait',
    'dead-time',
  ]);
  assert.ok(analysis.warnings.some((warning) => warning.code === 'missing-cue'));
  assert.ok(analysis.warnings.some((warning) => warning.code === 'cue-order'));
  assert.ok(analysis.warnings.some((warning) => warning.code === 'cue-rect'));
  assert.ok(analysis.warnings.some((warning) => warning.code === 'cue-frame-sync' && warning.cue === '1.7.running'));
  assert.equal(analysis.cueMappings.find((mapping) => mapping.cue === '1.7.running').driftMs, 600);
  assert.equal(analysis.beats[0].suggestions[1].treatment, 'speed-ramp');
  assert.equal(analysis.beats[0].suggestions[2].treatment, 'hard-cut');
  assert.equal(draftDirection.status, 'draft-suggestion');
  assert.equal(draftDirection.approved, false);
  assert.equal(draftDirection.reviewRequired, true);
});

test('wait intervals requiring more than 12x use activity-window cuts', () => {
  const { analysis } = analyzeTakeData({
    captureConfig: captureConfig({
      outputBudgetMs: { preferred: 1000 },
      expectedCues: [],
      cueOrder: [],
    }),
    cueManifest: {
      ...cueManifest(),
      cues: [{ name: '1.7.reveal', beatId: '1.7', sequence: 0, tMs: 18000 }],
    },
    activityLog: [{ kind: 'mutation', t: 9000 }],
    frameProbe: frameProbe(),
    beatId: '1.7',
  });
  assert.equal(analysis.beats[0].suggestions[0].category, 'wait');
  assert.equal(analysis.beats[0].suggestions[0].treatment, 'activity-window-cut');
  assert.ok(analysis.beats[0].suggestions[0].requiredRate > 12);
  assert.deepEqual(analysis.beats[0].suggestions[0].candidateWindows, [{
    sourceStartMs: 8250,
    sourceEndMs: 9900,
  }]);
});

test('take analyzer writes analysis and optional draft direction files', async () => {
  const directory = await fs.mkdtemp(path.join(process.cwd(), '.take-analyzer-test-'));
  try {
    const videoPath = path.join(directory, 'take.webm');
    const capturePlanPath = path.join(directory, 'take.capture.json');
    const cueManifestPath = path.join(directory, 'capture-cues.json');
    const activityLogPath = path.join(directory, 'activity.json');
    const outputPath = path.join(directory, 'take-analysis.json');
    const draftDirectionPath = path.join(directory, 'take.direction.draft.json');
    await Promise.all([
      fs.writeFile(videoPath, 'test-video-bytes'),
      fs.writeFile(capturePlanPath, JSON.stringify(captureConfig())),
      fs.writeFile(cueManifestPath, JSON.stringify(cueManifest())),
      fs.writeFile(activityLogPath, JSON.stringify([{ kind: 'mutation', t: 5000 }])),
    ]);

    const result = await analyzeTake({
      videoPath,
      capturePlanPath,
      cueManifestPath,
      activityLogPath,
      beatId: '1.7',
      outputPath,
      draftDirectionPath,
      probeFrames: async () => frameProbe(),
    });

    const writtenAnalysis = JSON.parse(await fs.readFile(outputPath, 'utf8'));
    const writtenDraft = JSON.parse(await fs.readFile(draftDirectionPath, 'utf8'));
    assert.equal(writtenAnalysis.takeId, 'topology-take-1');
    assert.match(writtenAnalysis.sourceHashes.videoSha256, /^[a-f0-9]{64}$/);
    assert.equal(writtenDraft.status, 'draft-suggestion');
    assert.equal(writtenDraft.source.analysisPath, outputPath);
    assert.equal(result.draftDirectionPath, draftDirectionPath);
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
});
