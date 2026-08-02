import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { ffprobeFrames } from './ffmpeg.mjs';
import { loadCaptureConfig } from './capture-config.mjs';

const ACTION_ACTIVITY = new Set(['click', 'press', 'eval', 'select', 'waitFor', 'waitText', 'goto', 'focus']);
const IGNORED_ACTIVITY = new Set(['capture-ready', 'capture-stop']);
const SYNC_TOLERANCE_MS = 500;
const MAX_CONTINUOUS_RATE = 12;
const READABLE_HOLD_MAX_MS = 3000;

function finiteNumber(value, fallback = 0) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function frameTimestampMs(frame) {
  const seconds = frame.best_effort_timestamp_time ?? frame.pkt_pts_time;
  return finiteNumber(seconds, Number.NaN) * 1000;
}

export function extractFrameTimeline(frameProbe) {
  const raw = (frameProbe.frames ?? [])
    .map((frame, index) => ({
      index,
      rawPtsMs: frameTimestampMs(frame),
      durationMs: finiteNumber(frame.pkt_duration_time) * 1000,
    }))
    .filter((frame) => Number.isFinite(frame.rawPtsMs))
    .sort((left, right) => left.rawPtsMs - right.rawPtsMs);
  const firstPtsMs = raw[0]?.rawPtsMs ?? 0;
  const frames = raw.map((frame) => ({ ...frame, ptsMs: frame.rawPtsMs - firstPtsMs }));
  const formatDurationMs = finiteNumber(frameProbe.format?.duration) * 1000;
  const last = frames.at(-1);
  const frameDurationMs = last ? last.ptsMs + last.durationMs : 0;
  return {
    firstPtsMs,
    durationMs: Math.max(formatDurationMs, frameDurationMs),
    frameCount: frames.length,
    frames,
  };
}

function nearestFrame(frames, cueTimeMs) {
  if (!frames.length) return null;
  let low = 0;
  let high = frames.length - 1;
  while (low < high) {
    const middle = Math.floor((low + high) / 2);
    if (frames[middle].ptsMs < cueTimeMs) low = middle + 1;
    else high = middle;
  }
  const after = frames[low];
  const before = frames[Math.max(0, low - 1)];
  return Math.abs(before.ptsMs - cueTimeMs) <= Math.abs(after.ptsMs - cueTimeMs) ? before : after;
}

function cueMappings(cues, timeline, warnings) {
  return cues.map((cue) => {
    const frame = nearestFrame(timeline.frames, cue.tMs);
    const driftMs = frame ? Math.abs(frame.ptsMs - cue.tMs) : null;
    const syncIssue = driftMs === null || driftMs > SYNC_TOLERANCE_MS;
    if (syncIssue) {
      warnings.push({
        code: 'cue-frame-sync',
        cue: cue.name,
        message: frame
          ? `Cue ${cue.name} is ${Math.round(driftMs)}ms from its nearest video frame (limit ${SYNC_TOLERANCE_MS}ms).`
          : `Cue ${cue.name} could not be mapped because ffprobe returned no video frames.`,
      });
    }
    return {
      cue: cue.name,
      beatId: cue.beatId ?? null,
      cueTimeMs: cue.tMs,
      frameIndex: frame?.index ?? null,
      framePtsMs: frame?.ptsMs ?? null,
      driftMs,
      syncIssue,
    };
  });
}

function validateCues(beat, cues, warnings) {
  const positions = new Map(cues.map((cue, index) => [cue.name, index]));
  for (const name of beat.expectedCues ?? []) {
    if (!positions.has(name)) {
      warnings.push({
        code: 'missing-cue',
        beatId: beat.id,
        cue: name,
        message: `Expected cue ${name} is missing; analysis continues with available evidence.`,
      });
    }
  }
  let priorPosition = -1;
  for (const name of beat.cueOrder ?? []) {
    const position = positions.get(name);
    if (position === undefined) continue;
    if (position < priorPosition) {
      warnings.push({
        code: 'cue-order',
        beatId: beat.id,
        cue: name,
        message: `Cue ${name} fired out of the declared order; analysis continues.`,
      });
    }
    priorPosition = Math.max(priorPosition, position);
  }
  for (const cue of cues) {
    if (cue.rectStatus && cue.rectStatus !== 'captured' && cue.rectStatus !== 'not-requested') {
      warnings.push({
        code: 'cue-rect',
        beatId: beat.id,
        cue: cue.name,
        message: `Cue ${cue.name} has rectangle status ${cue.rectStatus}.`,
      });
    }
  }
}

function intervalOverride(beat, from, to) {
  return beat.intervalCategories?.find((entry) => entry.from === from && entry.to === to)?.category;
}

function classifyInterval({ beat, from, to, durationMs, events }) {
  const override = intervalOverride(beat, from, to);
  if (override) return { category: override, reason: 'capture-plan override' };
  const meaningful = events.filter((event) => !IGNORED_ACTIVITY.has(event.kind));
  if (meaningful.some((event) => ACTION_ACTIVITY.has(event.kind))) {
    return { category: 'action', reason: 'contains a causal or navigational interaction' };
  }
  if (!meaningful.length) {
    if (durationMs <= READABLE_HOLD_MAX_MS && (from !== '$start' || to !== '$end')) {
      return { category: 'action', reason: 'short cue-bounded readable proof hold' };
    }
    return { category: 'dead-time', reason: 'no meaningful capture activity' };
  }
  return { category: 'wait', reason: 'only passive UI/mutation activity is present' };
}

function buildIntervals(beat, cues, durationMs, activityLog) {
  const boundaries = [
    { name: '$start', tMs: 0 },
    ...cues.map((cue) => ({ name: cue.name, tMs: Math.max(0, Math.min(durationMs, cue.tMs)) })),
    { name: '$end', tMs: durationMs },
  ].sort((left, right) => left.tMs - right.tMs);

  const deduped = boundaries.filter((boundary, index) => (
    index === 0
    || boundary.tMs !== boundaries[index - 1].tMs
    || boundary.name === '$end'
  ));
  const intervals = [];
  for (let index = 0; index < deduped.length - 1; index += 1) {
    const left = deduped[index];
    const right = deduped[index + 1];
    const intervalDurationMs = Math.max(0, right.tMs - left.tMs);
    if (!intervalDurationMs) continue;
    const events = (activityLog ?? []).filter((event) => event.t > left.tMs && event.t <= right.tMs);
    const classification = classifyInterval({
      beat,
      from: left.name,
      to: right.name,
      durationMs: intervalDurationMs,
      events,
    });
    intervals.push({
      index: intervals.length,
      from: left.name,
      to: right.name,
      sourceStartMs: left.tMs,
      sourceEndMs: right.tMs,
      sourceDurationMs: intervalDurationMs,
      activityCount: events.length,
      activityKinds: [...new Set(events.map((event) => event.kind))],
      activityTimesMs: events.map((event) => event.t),
      ...classification,
    });
  }
  return intervals;
}

function activityWindows(interval, beforeMs = 750, afterMs = 900) {
  const windows = interval.activityTimesMs.map((timeMs) => ({
    sourceStartMs: Math.max(interval.sourceStartMs, timeMs - beforeMs),
    sourceEndMs: Math.min(interval.sourceEndMs, timeMs + afterMs),
  }));
  if (!windows.length) {
    return [
      {
        sourceStartMs: interval.sourceStartMs,
        sourceEndMs: Math.min(interval.sourceEndMs, interval.sourceStartMs + 750),
      },
      {
        sourceStartMs: Math.max(interval.sourceStartMs, interval.sourceEndMs - 900),
        sourceEndMs: interval.sourceEndMs,
      },
    ].filter((window, index, all) => (
      window.sourceEndMs > window.sourceStartMs
      && (index === 0 || window.sourceStartMs > all[index - 1].sourceEndMs)
    ));
  }
  const merged = [];
  for (const window of windows) {
    const previous = merged.at(-1);
    if (previous && window.sourceStartMs <= previous.sourceEndMs) {
      previous.sourceEndMs = Math.max(previous.sourceEndMs, window.sourceEndMs);
    } else {
      merged.push(window);
    }
  }
  return merged;
}

function suggestTreatments(beat, intervals, warnings) {
  const budget = beat.outputBudgetMs ?? {};
  const preferredMs = finiteNumber(budget.preferred, intervals.reduce((sum, interval) => (
    interval.category === 'dead-time' ? sum : sum + interval.sourceDurationMs
  ), 0));
  const actionMs = intervals
    .filter((interval) => interval.category === 'action')
    .reduce((sum, interval) => sum + interval.sourceDurationMs, 0);
  const waitMs = intervals
    .filter((interval) => interval.category === 'wait')
    .reduce((sum, interval) => sum + interval.sourceDurationMs, 0);
  const availableWaitMs = Math.max(0, preferredMs - actionMs);
  if (actionMs > preferredMs) {
    warnings.push({
      code: 'budget-action-overflow',
      beatId: beat.id,
      message: `Action/proof footage alone exceeds the preferred budget by ${Math.round(actionMs - preferredMs)}ms.`,
    });
  }

  const suggestions = intervals.map((interval) => {
    if (interval.category === 'dead-time') {
      return {
        ...interval,
        treatment: 'hard-cut',
        targetOutputMs: 0,
        playbackRate: null,
        reason: 'remove uninformative static footage',
      };
    }
    if (interval.category === 'action') {
      return {
        ...interval,
        treatment: 'keep',
        targetOutputMs: interval.sourceDurationMs,
        playbackRate: 1,
        reason: 'keep causal action, navigation, or readable proof legible',
      };
    }
    const targetOutputMs = waitMs > 0
      ? Math.max(250, availableWaitMs * (interval.sourceDurationMs / waitMs))
      : 0;
    const requiredRate = targetOutputMs > 0 ? interval.sourceDurationMs / targetOutputMs : Number.POSITIVE_INFINITY;
    if (requiredRate > MAX_CONTINUOUS_RATE) {
      return {
        ...interval,
        treatment: 'activity-window-cut',
        targetOutputMs,
        playbackRate: null,
        requiredRate,
        candidateWindows: activityWindows(interval),
        reason: `continuous acceleration would exceed ${MAX_CONTINUOUS_RATE}x`,
      };
    }
    return {
      ...interval,
      treatment: requiredRate > 1.05 ? 'speed-ramp' : 'keep',
      targetOutputMs: Math.min(interval.sourceDurationMs, targetOutputMs || interval.sourceDurationMs),
      playbackRate: Math.max(1, requiredRate),
      requiredRate,
      reason: requiredRate > 1.05 ? 'compress passive wait within the preferred budget' : 'wait already fits the budget',
    };
  });

  const sourceMs = intervals.reduce((sum, interval) => sum + interval.sourceDurationMs, 0);
  const suggestedOutputMs = suggestions.reduce((sum, suggestion) => sum + suggestion.targetOutputMs, 0);
  return {
    budget: {
      minimumMs: budget.minimum ?? null,
      preferredMs,
      maximumMs: budget.maximum ?? null,
      sourceMs,
      pressureRatio: preferredMs > 0 ? sourceMs / preferredMs : null,
      suggestedOutputMs,
    },
    suggestions,
  };
}

function draftDirection(analysis) {
  return {
    schemaVersion: 1,
    status: 'draft-suggestion',
    approved: false,
    generatedBy: 'agentweaver-demo-recording take analyzer',
    source: {
      takeId: analysis.takeId,
      videoPath: analysis.videoPath,
      analysisSchemaVersion: analysis.schemaVersion,
    },
    reviewRequired: true,
    beats: analysis.beats.map((beat) => ({
      id: beat.id,
      budget: beat.budget,
      segments: beat.suggestions.map((suggestion) => ({
        sourceStartMs: suggestion.sourceStartMs,
        sourceEndMs: suggestion.sourceEndMs,
        category: suggestion.category,
        treatment: suggestion.treatment,
        playbackRate: suggestion.playbackRate,
        targetOutputMs: suggestion.targetOutputMs,
        fromCue: suggestion.from,
        toCue: suggestion.to,
        candidateWindows: suggestion.candidateWindows ?? null,
      })),
    })),
  };
}

export function analyzeTakeData({
  captureConfig,
  cueManifest,
  activityLog = [],
  frameProbe,
  beatId,
  videoPath = cueManifest.videoPath,
}) {
  const warnings = [];
  const timeline = extractFrameTimeline(frameProbe);
  const allCues = [...(cueManifest.cues ?? [])].sort((left, right) => left.tMs - right.tMs);
  const mappings = cueMappings(allCues, timeline, warnings);
  const selectedBeats = beatId
    ? captureConfig.beats.filter((beat) => beat.id === beatId)
    : captureConfig.beats.length === 1
      ? captureConfig.beats
      : captureConfig.beats.filter((beat) => allCues.some((cue) => cue.beatId === beat.id));
  if (!selectedBeats.length) {
    warnings.push({
      code: 'beat-selection',
      message: `No capture beat matched ${beatId ?? 'the cue manifest'}; no interval analysis was produced.`,
    });
  }

  const beats = selectedBeats.map((beat) => {
    const beatCues = allCues.filter((cue) => !cue.beatId || cue.beatId === beat.id);
    validateCues(beat, beatCues, warnings);
    const intervals = buildIntervals(beat, beatCues, timeline.durationMs, activityLog);
    const treatment = suggestTreatments(beat, intervals, warnings);
    return {
      id: beat.id,
      cueCount: beatCues.length,
      intervals,
      ...treatment,
    };
  });

  const analysis = {
    schemaVersion: 1,
    takeId: cueManifest.takeId,
    videoPath,
    policy: {
      intervalCategories: ['action', 'wait', 'dead-time'],
      missingCueMode: 'warn-and-continue',
      cueFrameToleranceMs: SYNC_TOLERANCE_MS,
      maximumContinuousRate: MAX_CONTINUOUS_RATE,
    },
    frameTimeline: {
      frameCount: timeline.frameCount,
      durationMs: timeline.durationMs,
      firstPtsMs: timeline.firstPtsMs,
    },
    cueMappings: mappings,
    beats,
    warnings,
  };
  return { analysis, draftDirection: draftDirection(analysis) };
}

async function sha256(filePath) {
  const contents = await fs.readFile(filePath);
  return crypto.createHash('sha256').update(contents).digest('hex');
}

export async function analyzeTake({
  videoPath,
  capturePlanPath,
  cueManifestPath,
  activityLogPath,
  beatId,
  outputPath,
  draftDirectionPath,
  probeFrames = ffprobeFrames,
}) {
  const [captureConfig, cueManifest, activityLog, frameProbe] = await Promise.all([
    loadCaptureConfig(capturePlanPath),
    fs.readFile(cueManifestPath, 'utf8').then(JSON.parse),
    activityLogPath ? fs.readFile(activityLogPath, 'utf8').then(JSON.parse) : [],
    probeFrames(videoPath),
  ]);
  const { analysis, draftDirection: draft } = analyzeTakeData({
    captureConfig,
    cueManifest,
    activityLog,
    frameProbe,
    beatId,
    videoPath,
  });
  analysis.sourceHashes = {
    videoSha256: await sha256(videoPath),
    capturePlanSha256: await sha256(capturePlanPath),
    cueManifestSha256: await sha256(cueManifestPath),
    activityLogSha256: activityLogPath ? await sha256(activityLogPath) : null,
  };
  draft.source.analysisPath = outputPath;
  draft.source.inputHashes = analysis.sourceHashes;
  await fs.mkdir(path.dirname(outputPath), { recursive: true });
  await fs.writeFile(outputPath, `${JSON.stringify(analysis, null, 2)}\n`, 'utf8');
  if (draftDirectionPath) {
    await fs.mkdir(path.dirname(draftDirectionPath), { recursive: true });
    await fs.writeFile(draftDirectionPath, `${JSON.stringify(draft, null, 2)}\n`, 'utf8');
  }
  return {
    analysis,
    draftDirectionPath: draftDirectionPath ?? null,
  };
}
