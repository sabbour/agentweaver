import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import test from 'node:test';
import {
  assembleScenarioVideo,
  buildCompositePlan,
  renderApprovedDirection,
  selectDirectionBeat,
} from '../lib/compositor.mjs';

function approvedDirection(overrides = {}) {
  return {
    schemaVersion: 1,
    status: 'approved',
    approved: true,
    reviewRequired: false,
    beats: [{
      id: '2.5',
      segments: [
        {
          sourceStartMs: 0,
          sourceEndMs: 1000,
          category: 'action',
          treatment: 'keep',
          playbackRate: 1,
          targetOutputMs: 1000,
          fromCue: '$start',
          toCue: 'beat.action',
        },
        {
          sourceStartMs: 1000,
          sourceEndMs: 3000,
          category: 'wait',
          treatment: 'speed-ramp',
          playbackRate: 2,
          targetOutputMs: 1000,
          fromCue: 'beat.action',
          toCue: 'beat.ready',
        },
        {
          sourceStartMs: 3000,
          sourceEndMs: 4500,
          category: 'dead-time',
          treatment: 'hard-cut',
          playbackRate: 1,
          targetOutputMs: 0,
          fromCue: 'beat.ready',
          toCue: '$end',
        },
      ],
      ...overrides,
    }],
  };
}

function cueManifest() {
  return {
    schemaVersion: 1,
    takeId: 'take-1',
    cues: [
      { name: 'beat.action', beatId: '2.5', tMs: 1000 },
      { name: 'beat.ready', beatId: '2.5', tMs: 3000 },
    ],
  };
}

test('selectDirectionBeat requires explicit beat id for multi-beat files', () => {
  assert.throws(() => selectDirectionBeat({
    approved: true,
    beats: [{ id: '1.1', segments: [] }, { id: '1.2', segments: [] }],
  }), /pass --beat-id/);
});

test('buildCompositePlan keeps cue-anchored math and skips hard-cut dead time', () => {
  const plan = buildCompositePlan({
    direction: approvedDirection(),
    cueManifest: cueManifest(),
    beatId: '2.5',
    videoDurationMs: 5000,
  });

  assert.equal(plan.concatStrategy, 'hard-cut');
  assert.equal(plan.renderSegments.length, 2);
  assert.equal(plan.skippedSegments, 1);
  assert.deepEqual(plan.renderSegments.map((segment) => ({
    start: segment.sourceStartMs,
    end: segment.sourceEndMs,
    rate: segment.playbackRate,
    expectedOutputMs: segment.expectedOutputMs,
  })), [
    { start: 0, end: 1000, rate: 1, expectedOutputMs: 1000 },
    { start: 1000, end: 3000, rate: 2, expectedOutputMs: 1000 },
  ]);
});

test('buildCompositePlan rejects playback changes on narrated action segments', () => {
  assert.throws(() => buildCompositePlan({
    direction: approvedDirection({
      segments: [{
        sourceStartMs: 0,
        sourceEndMs: 1000,
        category: 'action',
        treatment: 'speed-ramp',
        playbackRate: 1.5,
        targetOutputMs: 667,
        fromCue: '$start',
        toCue: 'beat.action',
      }],
    }),
    cueManifest: cueManifest(),
    beatId: '2.5',
    videoDurationMs: 5000,
  }), /narration safety requires 1x action footage/);
});

test('renderApprovedDirection applies playbackRate only where warranted and never invokes xfade', async () => {
  const directory = await fs.mkdtemp(path.join(process.cwd(), '.compositor-test-'));
  try {
    const directionPath = path.join(directory, 'direction.json');
    const cuesPath = path.join(directory, 'cues.json');
    const outputPath = path.join(directory, 'beat-2-5.webm');
    const calls = [];

    await Promise.all([
      fs.writeFile(directionPath, JSON.stringify(approvedDirection())),
      fs.writeFile(cuesPath, JSON.stringify(cueManifest())),
    ]);

    const result = await renderApprovedDirection({
      directionPath,
      cueManifestPath: cuesPath,
      videoPath: path.join(directory, 'raw.webm'),
      audioPath: path.join(directory, 'beat.wav'),
      outputPath,
    }, {
      getDurationMs: async (filePath) => {
        if (filePath.endsWith('.webm')) return 5000;
        if (filePath.endsWith('.wav')) return 2400;
        return 2000;
      },
      renderVideoSegment: async (_videoPath, segmentPath, options) => {
        calls.push({ type: 'render', segmentPath, options });
        await fs.writeFile(segmentPath, `segment-${options.startMs}`, 'utf8');
      },
      concatVideos: async (inputs, out) => {
        calls.push({ type: 'concat', inputs, out });
        assert.ok(inputs.every((input) => !input.includes('xfade')));
        await fs.writeFile(out, 'concat', 'utf8');
      },
      syncSegmentToAudio: async (video, audio, out) => {
        calls.push({ type: 'sync', video, audio, out });
        await fs.writeFile(out, 'muxed', 'utf8');
        return { action: 'padded-video' };
      },
    });

    assert.equal(result.renderedSegments, 2);
    assert.equal(calls.filter((call) => call.type === 'render').length, 2);
    assert.deepEqual(calls.filter((call) => call.type === 'render').map((call) => call.options.playbackRate), [1, 2]);
    assert.equal(calls.some((call) => JSON.stringify(call).includes('xfade')), false);
    assert.equal(calls.at(-1).type, 'sync');
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
});

test('assembleScenarioVideo can assemble rendered beat outputs with a custom prefix', async () => {
  const directory = await fs.mkdtemp(path.join(process.cwd(), '.compositor-assemble-'));
  try {
    const planPath = path.join(directory, 'plan.md');
    const segmentsDir = path.join(directory, 'segments');
    const outputPath = path.join(directory, 'scenario.webm');
    await fs.mkdir(segmentsDir, { recursive: true });
    await fs.writeFile(planPath, [
      '## Beat 1.1 — First',
      '',
      'Narration: "One."',
      '',
      '## Beat 1.2 — Second',
      '',
      'Narration: "Two."',
      '',
    ].join('\n'), 'utf8');
    await Promise.all([
      fs.writeFile(path.join(segmentsDir, 'rendered-1-1.webm'), 'one', 'utf8'),
      fs.writeFile(path.join(segmentsDir, 'rendered-1-2.webm'), 'two', 'utf8'),
    ]);

    const result = await assembleScenarioVideo({
      planPath,
      segmentsDir,
      outputPath,
      segmentPrefix: 'rendered',
      segmentExtension: 'webm',
    }, {
      concatVideos: async (inputs, out) => {
        assert.deepEqual(inputs.map((input) => path.basename(input)), ['rendered-1-1.webm', 'rendered-1-2.webm']);
        await fs.writeFile(out, 'scenario', 'utf8');
      },
      getDurationMs: async () => 3200,
    });

    assert.equal(result.segmentPrefix, 'rendered');
    assert.equal(result.includedBeats, 2);
    assert.equal(result.durationMs, 3200);
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
});
