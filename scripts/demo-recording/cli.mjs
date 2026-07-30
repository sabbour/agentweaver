import fs from 'node:fs/promises';
import path from 'node:path';
import { createApiFromSession } from './lib/api.mjs';
import { writeSeedScript } from './lib/auth.mjs';
import { loadBeatPlan, formatNarrationFile } from './lib/beats.mjs';
import { AISettings, generateNarrationText, synthesizeSpeechToFile } from './lib/azure-ai.mjs';
import {
  detectVisualActivity,
  ffprobeJson,
  getDurationMs,
  trimVideoByActivity,
  syncSegmentToAudio,
  concatVideos,
} from './lib/ffmpeg.mjs';
import { analyzeTake } from './lib/take-analyzer.mjs';

function parseArgs(argv) {
  const [command, ...rest] = argv;
  const options = {};
  for (let i = 0; i < rest.length; i += 1) {
    const token = rest[i];
    if (!token.startsWith('--')) continue;
    const key = token.slice(2);
    const value = rest[i + 1] && !rest[i + 1].startsWith('--') ? rest[++i] : 'true';
    options[key] = value;
  }
  return { command, options };
}

async function listProjects(options) {
  const api = await createApiFromSession({ baseUrl: options.baseUrl, sessionStoragePath: options.sessionStoragePath });
  const projects = await api.listProjects(Number(options.pageSize || 100));
  process.stdout.write(`${JSON.stringify(projects, null, 2)}\n`);
}

async function deleteProjectsByPattern(options) {
  const api = await createApiFromSession({ baseUrl: options.baseUrl, sessionStoragePath: options.sessionStoragePath });
  const patterns = String(options.patterns || '').split(',').map((x) => x.trim().toLowerCase()).filter(Boolean);
  const projects = await api.listProjects(Number(options.pageSize || 100));
  const matches = projects.items.filter((project) => patterns.some((pattern) => project.name.toLowerCase().includes(pattern)));
  for (const project of matches) {
    await api.deleteProject(project.project_id);
  }
  process.stdout.write(`${JSON.stringify({ deleted: matches.map((p) => ({ id: p.project_id, name: p.name })) }, null, 2)}\n`);
}

async function generateNarration(options) {
  const beats = await loadBeatPlan(options.plan);
  const settings = AISettings.fromEnv();
  const contextSummary = options.context || '';
  for (const beat of beats) {
    beat.generatedNarration = await generateNarrationText(settings, { beat, contextSummary });
  }
  await fs.mkdir(path.dirname(options.out), { recursive: true });
  await fs.writeFile(options.out, formatNarrationFile(beats), 'utf8');
  process.stdout.write(`${JSON.stringify(beats.map(({ id, title, generatedNarration }) => ({ id, title, generatedNarration })), null, 2)}\n`);
}

async function synthesize(options) {
  const settings = AISettings.fromEnv();
  const text = await fs.readFile(options.text, 'utf8');
  await synthesizeSpeechToFile(settings, {
    text,
    outputPath: options.out,
    voiceName: options.voice,
  });
  const probe = await ffprobeJson(options.out);
  process.stdout.write(`${JSON.stringify(probe, null, 2)}\n`);
}

async function seedScript(options) {
  const result = await writeSeedScript(options.out, {
    sessionStoragePath: options.sessionStoragePath,
    targetOrigin: options.targetOrigin,
  });
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

async function trimStatic(options) {
  let activityLog;
  if (options.activityLog) {
    activityLog = JSON.parse(await fs.readFile(options.activityLog, 'utf8'));
  } else {
    activityLog = await detectVisualActivity(options.video, {
      sceneThreshold: Number(options.sceneThreshold || 0.0035),
    });
  }
  const result = await trimVideoByActivity(options.video, options.out, activityLog, {
    maxStaticMs: Number(options.maxStaticMs || 2500),
    retainAfterActivityMs: Number(options.retainAfterActivityMs || 900),
    retainBeforeActivityMs: Number(options.retainBeforeActivityMs || 1200),
    minSegmentMs: Number(options.minSegmentMs || 250),
  });
  const probe = await ffprobeJson(options.out);
  process.stdout.write(`${JSON.stringify({ ...result, activityLog, outputProbe: probe }, null, 2)}\n`);
}

function beatFileId(beatId) {
  return beatId.replace(/\./g, '-');
}

// Synthesizes narration for every beat in the committed master plan up front,
// BEFORE any capture happens. This is what lets capture timing be driven by
// real audio duration instead of the old fixed/hardcoded pause constants that
// caused "video too fast compared to audio".
async function synthesizeBeats(options) {
  const beats = await loadBeatPlan(options.plan);
  const settings = AISettings.fromEnv();
  await fs.mkdir(options.outDir, { recursive: true });
  const results = [];
  for (const beat of beats) {
    const text = (options.useGenerated === 'true')
      ? await generateNarrationText(settings, { beat, contextSummary: options.context || '' })
      : beat.narrationSource;
    const fileId = beatFileId(beat.id);
    const textPath = path.join(options.outDir, `beat-${fileId}.txt`);
    const audioPath = path.join(options.outDir, `beat-${fileId}.wav`);
    await fs.writeFile(textPath, text, 'utf8');
    await synthesizeSpeechToFile(settings, { text, outputPath: audioPath, voiceName: options.voice });
    const durationMs = await getDurationMs(audioPath);
    results.push({ id: beat.id, title: beat.title, textPath, audioPath, durationMs });
  }
  process.stdout.write(`${JSON.stringify(results, null, 2)}\n`);
}

// Given a beat's raw captured screencast + its narration wav, idle-trims the
// video (if an activity log is present), then syncs the (trimmed) video and
// audio durations together — replacing the old naive '-shortest' mux.
async function syncBeat(options) {
  let workingVideo = options.video;
  let trimSummary = null;
  if (options.activityLog) {
    const activityLog = JSON.parse(await fs.readFile(options.activityLog, 'utf8'));
    const trimmedPath = `${options.out}.trimmed${path.extname(options.video)}`;
    trimSummary = await trimVideoByActivity(options.video, trimmedPath, activityLog, {
      maxStaticMs: Number(options.maxStaticMs || 2500),
      retainAfterActivityMs: Number(options.retainAfterActivityMs || 900),
      retainBeforeActivityMs: Number(options.retainBeforeActivityMs || 1200),
      minSegmentMs: Number(options.minSegmentMs || 250),
    });
    workingVideo = trimmedPath;
  }
  const syncResult = await syncSegmentToAudio(workingVideo, options.audio, options.out, {
    toleranceMs: Number(options.toleranceMs || 150),
  });
  if (trimSummary) {
    await fs.rm(workingVideo, { force: true }).catch(() => {});
  }
  process.stdout.write(`${JSON.stringify({ trim: trimSummary, sync: syncResult }, null, 2)}\n`);
}

// Concatenates every already-synced per-beat segment (in beat order from the
// master plan) into one final video. This is the reusable replacement for the
// old one-off `final-seg*.cjs` scratch-script assembly.
async function assembleFinal(options) {
  const beats = await loadBeatPlan(options.plan);
  const inputs = [];
  const missing = [];
  for (const beat of beats) {
    const segmentPath = path.join(options.segmentsDir, `synced-${beatFileId(beat.id)}.webm`);
    try {
      await fs.access(segmentPath);
      inputs.push(segmentPath);
    } catch {
      missing.push(beat.id);
    }
  }
  if (missing.length && options.allowMissing !== 'true') {
    throw new Error(`Missing synced segments for beats: ${missing.join(', ')}. Pass --allow-missing true to assemble a partial video.`);
  }
  await concatVideos(inputs, options.out);
  const probe = await ffprobeJson(options.out);
  process.stdout.write(`${JSON.stringify({ out: options.out, includedBeats: inputs.length, missingBeats: missing, durationSec: probe?.format?.duration }, null, 2)}\n`);
}

async function analyzeCapturedTake(options) {
  const capturePlan = options.capturePlan ?? options['capture-plan'];
  const activityLog = options.activityLog ?? options['activity-log'];
  const beatId = options.beatId ?? options['beat-id'];
  const draftDirection = options.draftDirection ?? options['draft-direction'];
  const required = ['video', 'capturePlan', 'cues', 'out'];
  const values = { video: options.video, capturePlan, cues: options.cues, out: options.out };
  const missing = required.filter((name) => !values[name]);
  if (missing.length) {
    throw new Error(`analyze-take requires: ${missing.map((name) => `--${name}`).join(', ')}`);
  }
  const result = await analyzeTake({
    videoPath: options.video,
    capturePlanPath: capturePlan,
    cueManifestPath: options.cues,
    activityLogPath: activityLog,
    beatId,
    outputPath: options.out,
    draftDirectionPath: draftDirection,
  });
  process.stdout.write(`${JSON.stringify({
    out: options.out,
    draftDirection: result.draftDirectionPath,
    warningCount: result.analysis.warnings.length,
    analyzedBeats: result.analysis.beats.map((beat) => beat.id),
  }, null, 2)}\n`);
}

const { command, options } = parseArgs(process.argv.slice(2));

switch (command) {
  case 'list-projects':
    await listProjects(options);
    break;
  case 'delete-projects':
    await deleteProjectsByPattern(options);
    break;
  case 'generate-narration':
    await generateNarration(options);
    break;
  case 'synthesize':
    await synthesize(options);
    break;
  case 'seed-script':
    await seedScript(options);
    break;
  case 'trim-static':
    await trimStatic(options);
    break;
  case 'synthesize-beats':
    await synthesizeBeats(options);
    break;
  case 'sync-beat':
    await syncBeat(options);
    break;
  case 'assemble-final':
    await assembleFinal(options);
    break;
  case 'analyze-take':
    await analyzeCapturedTake(options);
    break;
  default:
    throw new Error(`Unknown command: ${command}`);
}
