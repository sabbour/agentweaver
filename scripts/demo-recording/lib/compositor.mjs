import fs from 'node:fs/promises';
import path from 'node:path';
import { loadBeatPlan } from './beats.mjs';
import { concatVideos, getDurationMs, renderVideoSegment, syncSegmentToAudio } from './ffmpeg.mjs';

const RATE_EPSILON = 0.01;
const CUE_TOLERANCE_MS = 25;
const SEGMENT_CATEGORIES = new Set(['action', 'wait', 'dead-time']);

function finiteNumber(value, fallback = null) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function normalizePlaybackRate(value) {
  const rate = finiteNumber(value, 1);
  if (!(rate > 0)) throw new Error(`Invalid playbackRate: ${value}`);
  return rate;
}

function cueLookup(cueManifest, beatId) {
  const entries = new Map();
  for (const cue of cueManifest.cues ?? []) {
    if (cue.beatId && beatId && cue.beatId !== beatId) continue;
    entries.set(cue.name, cue);
  }
  return entries;
}

function cueAnchorTime(cues, cueName, fallback) {
  if (!cueName || cueName === '$start' || cueName === '$end') return fallback;
  const cue = cues.get(cueName);
  if (!cue) throw new Error(`Direction references unknown cue ${cueName}`);
  return cue.tMs;
}

export function selectDirectionBeat(direction, beatId) {
  const beats = Array.isArray(direction?.beats) ? direction.beats : [];
  if (beatId) {
    const beat = beats.find((entry) => entry.id === beatId);
    if (!beat) throw new Error(`Direction file does not include beat ${beatId}`);
    return beat;
  }
  if (beats.length !== 1) {
    throw new Error('Direction file contains multiple beats; pass --beat-id to render one beat.');
  }
  return beats[0];
}

export function buildCompositePlan({ direction, cueManifest, beatId, videoDurationMs }) {
  if (direction?.approved !== true) {
    throw new Error('render-direction requires an approved direction.json (approved must be true).');
  }
  if (direction?.reviewRequired === true) {
    throw new Error('render-direction refuses direction.json while reviewRequired is true.');
  }

  const beat = selectDirectionBeat(direction, beatId);
  const cues = cueLookup(cueManifest, beat.id);
  const segments = Array.isArray(beat.segments) ? beat.segments : [];
  const renderSegments = [];
  let skippedSegments = 0;

  for (const [index, segment] of segments.entries()) {
    const sourceStartMs = finiteNumber(segment.sourceStartMs);
    const sourceEndMs = finiteNumber(segment.sourceEndMs);
    if (sourceStartMs === null || sourceEndMs === null || sourceEndMs <= sourceStartMs) {
      throw new Error(`Beat ${beat.id} segment ${index} has an invalid source range.`);
    }
    if (!SEGMENT_CATEGORIES.has(segment.category)) {
      throw new Error(`Beat ${beat.id} segment ${index} has unsupported category ${segment.category}.`);
    }
    if (sourceEndMs > videoDurationMs + CUE_TOLERANCE_MS) {
      throw new Error(`Beat ${beat.id} segment ${index} extends past the source video duration.`);
    }

    const expectedStartMs = cueAnchorTime(cues, segment.fromCue, sourceStartMs);
    const expectedEndMs = cueAnchorTime(cues, segment.toCue, sourceEndMs);
    if (Math.abs(expectedStartMs - sourceStartMs) > CUE_TOLERANCE_MS) {
      throw new Error(`Beat ${beat.id} segment ${index} start is not cue-anchored to ${segment.fromCue}.`);
    }
    if (Math.abs(expectedEndMs - sourceEndMs) > CUE_TOLERANCE_MS) {
      throw new Error(`Beat ${beat.id} segment ${index} end is not cue-anchored to ${segment.toCue}.`);
    }

    const playbackRate = normalizePlaybackRate(segment.playbackRate ?? 1);
    const requiresRateAdjustment = Math.abs(playbackRate - 1) > RATE_EPSILON;
    if (playbackRate < 1 - RATE_EPSILON) {
      throw new Error(`Beat ${beat.id} segment ${index} uses playbackRate < 1, which this renderer does not support safely.`);
    }
    if (segment.category === 'action' && requiresRateAdjustment) {
      throw new Error(`Beat ${beat.id} segment ${index} changes playbackRate on an action segment; narration safety requires 1x action footage.`);
    }

    const targetOutputMs = finiteNumber(segment.targetOutputMs);
    if (segment.treatment === 'hard-cut' || targetOutputMs === 0) {
      skippedSegments += 1;
      continue;
    }

    renderSegments.push({
      index,
      category: segment.category,
      treatment: segment.treatment ?? 'keep',
      sourceStartMs,
      sourceEndMs,
      sourceDurationMs: sourceEndMs - sourceStartMs,
      playbackRate,
      requiresRateAdjustment,
      expectedOutputMs: Math.round((sourceEndMs - sourceStartMs) / playbackRate),
      targetOutputMs,
      fromCue: segment.fromCue ?? null,
      toCue: segment.toCue ?? null,
    });
  }

  if (!renderSegments.length) {
    throw new Error(`Beat ${beat.id} has no renderable segments after hard cuts were removed.`);
  }

  return {
    beatId: beat.id,
    concatStrategy: 'hard-cut',
    narrationStrategy: 'preserve-narration-tempo',
    renderSegments,
    skippedSegments,
  };
}

export async function renderApprovedDirection(options, dependencies = {}) {
  const readFile = dependencies.readFile ?? fs.readFile;
  const mkdir = dependencies.mkdir ?? fs.mkdir;
  const rm = dependencies.rm ?? fs.rm;
  const rename = dependencies.rename ?? fs.rename;
  const renderSegment = dependencies.renderVideoSegment ?? renderVideoSegment;
  const concat = dependencies.concatVideos ?? concatVideos;
  const syncToAudio = dependencies.syncSegmentToAudio ?? syncSegmentToAudio;
  const durationOf = dependencies.getDurationMs ?? getDurationMs;

  const [direction, cueManifest] = await Promise.all([
    readFile(options.directionPath, 'utf8').then(JSON.parse),
    readFile(options.cueManifestPath, 'utf8').then(JSON.parse),
  ]);
  const videoDurationMs = await durationOf(options.videoPath);
  const plan = buildCompositePlan({
    direction,
    cueManifest,
    beatId: options.beatId,
    videoDurationMs,
  });

  const outputPath = options.outputPath;
  const segmentExtension = path.extname(outputPath) || '.webm';
  const workingDir = `${outputPath}.parts`;
  const concatenatedVideoPath = `${outputPath}.video${segmentExtension}`;
  await mkdir(workingDir, { recursive: true });

  const segmentPaths = [];
  try {
    for (const segment of plan.renderSegments) {
      const segmentPath = path.join(workingDir, `segment-${String(segment.index).padStart(3, '0')}${segmentExtension}`);
      await renderSegment(options.videoPath, segmentPath, {
        startMs: segment.sourceStartMs,
        endMs: segment.sourceEndMs,
        playbackRate: segment.playbackRate,
      });
      segmentPaths.push(segmentPath);
    }

    if (segmentPaths.length === 1) {
      await rename(segmentPaths[0], concatenatedVideoPath);
    } else {
      await concat(segmentPaths, concatenatedVideoPath);
    }

    let audioSync = null;
    if (options.audioPath) {
      /**
       * The take analyzer keeps action/proof segments at 1x and only suggests
       * acceleration for wait/dead-time spans. We preserve narration tempo and
       * never apply atempo here; instead we render the approved picture edit
       * and use the existing pad-shorter-stream sync helper to absorb small
       * end-of-beat drift without warping spoken audio.
       */
      audioSync = await syncToAudio(concatenatedVideoPath, options.audioPath, outputPath, {
        toleranceMs: Number(options.toleranceMs ?? 150),
      });
    } else {
      await rename(concatenatedVideoPath, outputPath);
    }

    return {
      out: outputPath,
      beatId: plan.beatId,
      renderedSegments: plan.renderSegments.length,
      skippedSegments: plan.skippedSegments,
      concatStrategy: plan.concatStrategy,
      narrationStrategy: plan.narrationStrategy,
      audioSync,
    };
  } finally {
    await rm(concatenatedVideoPath, { force: true }).catch(() => {});
    if (options.keepTemp !== true) {
      await rm(workingDir, { recursive: true, force: true }).catch(() => {});
    }
  }
}

export async function assembleScenarioVideo(options, dependencies = {}) {
  const beats = await loadBeatPlan(options.planPath);
  const concat = dependencies.concatVideos ?? concatVideos;
  const probeDuration = dependencies.getDurationMs ?? getDurationMs;
  const inputs = [];
  const missing = [];
  const extension = options.segmentExtension?.startsWith('.') ? options.segmentExtension : `.${options.segmentExtension ?? 'webm'}`;
  const prefix = options.segmentPrefix ?? 'synced';

  for (const beat of beats) {
    const segmentPath = path.join(options.segmentsDir, `${prefix}-${beat.id.replace(/\./g, '-')}${extension}`);
    try {
      await fs.access(segmentPath);
      inputs.push(segmentPath);
    } catch {
      missing.push(beat.id);
    }
  }

  if (missing.length && options.allowMissing !== true) {
    throw new Error(`Missing ${prefix} segments for beats: ${missing.join(', ')}. Pass --allow-missing true to assemble a partial video.`);
  }
  if (!inputs.length) {
    throw new Error(`No ${prefix} segments were found to assemble.`);
  }
  await concat(inputs, options.outputPath);
  return {
    out: options.outputPath,
    includedBeats: inputs.length,
    missingBeats: missing,
    durationMs: await probeDuration(options.outputPath),
    segmentPrefix: prefix,
    segmentExtension: extension,
  };
}
