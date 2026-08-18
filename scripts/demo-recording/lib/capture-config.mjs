import fs from 'node:fs/promises';
import { loadBeatPlan } from './beats.mjs';

const sourceKinds = new Set(['selector', 'attribute', 'text', 'predicate']);
const predicateOperators = new Set([
  'exists',
  'count-gte',
  'count-eq',
  'any-attribute-in',
  'all-attribute-in',
  'text-includes',
  'text-matches',
]);
const rectModes = new Set(['matched-element', 'element', 'first-matching', 'union', 'none']);
const intervalCategories = new Set(['action', 'wait', 'dead-time']);
const prerequisiteKinds = new Set(['app-url', 'github-issue-number', 'github-issue-url', 'github-pr-url']);

function fail(message) {
  throw new Error(`Invalid demo capture plan: ${message}`);
}

function validateCue(cue, location) {
  if (!cue || typeof cue !== 'object' || Array.isArray(cue)) fail(`${location} must be an object`);
  if (!/^[a-z0-9][a-z0-9._-]*$/.test(cue.name ?? '')) fail(`${location}.name must be a stable lowercase cue name`);
  if (cue.stableForMs !== undefined && (!Number.isInteger(cue.stableForMs) || cue.stableForMs < 0)) {
    fail(`${location}.stableForMs must be a non-negative integer`);
  }
  if (cue.deadlineMs !== undefined && (!Number.isInteger(cue.deadlineMs) || cue.deadlineMs < 1)) {
    fail(`${location}.deadlineMs must be a positive integer`);
  }
  if (cue.source !== undefined) {
    const source = cue.source;
    if (!sourceKinds.has(source?.kind)) {
      fail(`${location}.source.kind must be DOM-only: selector, attribute, text, or predicate`);
    }
    if (typeof source.selector !== 'string' || !source.selector.trim()) fail(`${location}.source.selector is required`);
    if (source.kind === 'attribute' && (typeof source.attribute !== 'string' || !source.attribute)) {
      fail(`${location}.source.attribute is required`);
    }
    if (source.kind === 'predicate' && !predicateOperators.has(source.operator)) {
      fail(`${location}.source.operator is unsupported`);
    }
  }
  if (cue.rect !== undefined) {
    if (!rectModes.has(cue.rect?.mode)) fail(`${location}.rect.mode is unsupported`);
    if (['element', 'first-matching', 'union'].includes(cue.rect.mode) && !cue.rect.selector) {
      fail(`${location}.rect.selector is required for ${cue.rect.mode}`);
    }
  }
}

function validatePrerequisites(prerequisites, location) {
  if (prerequisites === undefined) return;
  if (!Array.isArray(prerequisites)) fail(`${location}.prerequisites must be an array`);
  for (const [index, prerequisite] of prerequisites.entries()) {
    const item = `${location}.prerequisites[${index}]`;
    if (!prerequisite || typeof prerequisite !== 'object' || Array.isArray(prerequisite)) fail(`${item} must be an object`);
    if (!/^[A-Z][A-Z0-9_]*$/.test(prerequisite.environment ?? '')) {
      fail(`${item}.environment must be an uppercase environment variable name`);
    }
    if (!prerequisiteKinds.has(prerequisite.kind)) fail(`${item}.kind is unsupported`);
    if (prerequisite.matchesEnvironment !== undefined
      && !/^[A-Z][A-Z0-9_]*$/.test(prerequisite.matchesEnvironment)) {
      fail(`${item}.matchesEnvironment must be an uppercase environment variable name`);
    }
    if (typeof prerequisite.message !== 'string' || !prerequisite.message.trim()) {
      fail(`${item}.message must explain how to satisfy the prerequisite`);
    }
  }
}

export function validateCaptureConfig(config) {
  if (!config || typeof config !== 'object' || Array.isArray(config)) fail('root must be an object');
  if (config.schemaVersion !== 1) fail('schemaVersion must be 1');
  if (!Array.isArray(config.beats)) fail('beats must be an array');

  const beatIds = new Set();
  const cueNames = new Set();
  for (const [beatIndex, beat] of config.beats.entries()) {
    const location = `beats[${beatIndex}]`;
    if (!beat || typeof beat !== 'object' || Array.isArray(beat)) fail(`${location} must be an object`);
    if (!/^[0-9]+\.[0-9]+$/.test(beat.id ?? '')) fail(`${location}.id must match a markdown beat ID`);
    if (beatIds.has(beat.id)) fail(`duplicate beat ID ${beat.id}`);
    beatIds.add(beat.id);
    if (beat.cueWatchers !== undefined && !Array.isArray(beat.cueWatchers)) fail(`${location}.cueWatchers must be an array`);
    if (beat.steps !== undefined && !Array.isArray(beat.steps)) fail(`${location}.steps must be an array`);
    if (beat.expectedCues !== undefined && !Array.isArray(beat.expectedCues)) fail(`${location}.expectedCues must be an array`);
    if (beat.cueOrder !== undefined && !Array.isArray(beat.cueOrder)) fail(`${location}.cueOrder must be an array`);
    if (beat.disableApprovalWatcher !== undefined && typeof beat.disableApprovalWatcher !== 'boolean') {
      fail(`${location}.disableApprovalWatcher must be a boolean`);
    }
    if (beat.approvalWatcherGraceMs !== undefined
      && (!Number.isInteger(beat.approvalWatcherGraceMs) || beat.approvalWatcherGraceMs < 0)) {
      fail(`${location}.approvalWatcherGraceMs must be a non-negative integer`);
    }
    if (beat.requiresPriorBeat !== undefined && !/^[0-9]+\.[0-9]+$/.test(beat.requiresPriorBeat)) {
      fail(`${location}.requiresPriorBeat must reference a markdown beat ID`);
    }
    validatePrerequisites(beat.prerequisites, location);
    for (const [cueIndex, name] of (beat.expectedCues ?? []).entries()) {
      if (typeof name !== 'string' || !name) fail(`${location}.expectedCues[${cueIndex}] must be a cue name`);
    }
    for (const [cueIndex, name] of (beat.cueOrder ?? []).entries()) {
      if (typeof name !== 'string' || !name) fail(`${location}.cueOrder[${cueIndex}] must be a cue name`);
    }
    if (beat.outputBudgetMs !== undefined) {
      const { minimum = 0, preferred, maximum = Number.POSITIVE_INFINITY } = beat.outputBudgetMs;
      if (!Number.isInteger(preferred) || preferred < 1) fail(`${location}.outputBudgetMs.preferred must be a positive integer`);
      if (!Number.isInteger(minimum) || minimum < 0 || minimum > preferred) {
        fail(`${location}.outputBudgetMs.minimum must be between zero and preferred`);
      }
      if ((!Number.isInteger(maximum) && maximum !== Number.POSITIVE_INFINITY) || maximum < preferred) {
        fail(`${location}.outputBudgetMs.maximum must be at least preferred`);
      }
    }
    if (beat.intervalCategories !== undefined) {
      if (!Array.isArray(beat.intervalCategories)) fail(`${location}.intervalCategories must be an array`);
      for (const [intervalIndex, interval] of beat.intervalCategories.entries()) {
        if (!intervalCategories.has(interval?.category)) {
          fail(`${location}.intervalCategories[${intervalIndex}].category is unsupported`);
        }
        if (!interval?.from || !interval?.to) fail(`${location}.intervalCategories[${intervalIndex}] requires from and to`);
      }
    }

    const cues = [
      ...(beat.cueWatchers ?? []).map((cue, index) => ({ cue, location: `${location}.cueWatchers[${index}]` })),
      ...(beat.steps ?? [])
        .map((step, index) => ({ cue: step?.cue, location: `${location}.steps[${index}].cue` }))
        .filter(({ cue }) => cue !== undefined),
    ];
    for (const item of cues) {
      validateCue(item.cue, item.location);
      if (cueNames.has(item.cue.name)) fail(`duplicate semantic cue name ${item.cue.name}`);
      cueNames.add(item.cue.name);
    }
  }
  for (const beat of config.beats) {
    if (beat.requiresPriorBeat && !beatIds.has(beat.requiresPriorBeat)) {
      fail(`beat ${beat.id} requires missing prior beat ${beat.requiresPriorBeat}`);
    }
  }
  return config;
}

export async function loadCaptureConfig(capturePlanPath) {
  const config = JSON.parse(await fs.readFile(capturePlanPath, 'utf8'));
  return validateCaptureConfig(config);
}

export function joinCaptureConfig(beats, config, options = {}) {
  validateCaptureConfig(config);
  const beatById = new Map(beats.map((beat) => [beat.id, beat]));
  for (const captureBeat of config.beats) {
    if (!beatById.has(captureBeat.id)) fail(`capture beat ${captureBeat.id} does not exist in markdown`);
  }

  const requireAllBeats = options.requireAllBeats ?? config.requireAllBeats ?? false;
  if (requireAllBeats) {
    const configured = new Set(config.beats.map((beat) => beat.id));
    const missing = beats.filter((beat) => !configured.has(beat.id)).map((beat) => beat.id);
    if (missing.length) fail(`markdown beats missing capture definitions: ${missing.join(', ')}`);
  }

  const captureById = new Map(config.beats.map((beat) => [beat.id, beat]));
  return beats.map((beat) => {
    const capture = captureById.get(beat.id);
    if (!capture) return { ...beat, beatId: beat.id };
    return {
      ...beat,
      ...capture,
      id: beat.id,
      beatId: beat.id,
      startUrl: capture.startUrl ?? beat.startUrl,
      freshNavigation: capture.freshNavigation ?? beat.freshNavigation,
      cueWatchers: capture.cueWatchers ?? [],
      steps: capture.steps ?? [],
    };
  });
}

export async function loadJoinedCapturePlan({ beatPlanPath, capturePlanPath, requireAllBeats }) {
  const [beats, config] = await Promise.all([
    loadBeatPlan(beatPlanPath),
    loadCaptureConfig(capturePlanPath),
  ]);
  return joinCaptureConfig(beats, config, { requireAllBeats });
}

export function createCueManifest({ takeId, videoPath = null, captureStartedAtEpochMs, cues }) {
  if (!takeId) throw new Error('takeId is required');
  return {
    schemaVersion: 1,
    takeId,
    videoPath,
    captureStartedAtEpochMs,
    cues: [...cues].sort((left, right) => left.tMs - right.tMs || left.sequence - right.sequence),
  };
}
