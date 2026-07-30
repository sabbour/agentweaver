import fs from 'node:fs/promises';
import { accessSync, constants } from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { buildKeepSegments, summarizeTrim } from './pacing.mjs';

const COMMON_FFMPEG_PATHS = {
  ffmpeg: [
    process.env.AGENTWEAVER_DEMO_FFMPEG,
    'C:\\Users\\asabbour\\Git\\utmp3\\.venv\\Lib\\site-packages\\static_ffmpeg\\bin\\win32\\ffmpeg.exe',
    'C:\\Users\\asabbour\\OneDrive - Microsoft\\Documents\\ShareX\\Tools\\ffmpeg.exe',
  ],
  ffprobe: [
    process.env.AGENTWEAVER_DEMO_FFPROBE,
    'C:\\Users\\asabbour\\Git\\utmp3\\.venv\\Lib\\site-packages\\static_ffmpeg\\bin\\win32\\ffprobe.exe',
  ],
};

function exists(filePath) {
  if (!filePath) return false;
  try {
    accessSync(filePath, constants.F_OK);
    return true;
  } catch {
    return false;
  }
}

export function resolveBinary(name) {
  const match = COMMON_FFMPEG_PATHS[name]?.find(exists);
  if (!match) throw new Error(`Could not locate ${name}. Set AGENTWEAVER_DEMO_${name.toUpperCase()}.`);
  return match;
}

function runBinary(exe, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(exe, args, { stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', (code) => {
      if (code === 0) {
        resolve({ stdout, stderr });
        return;
      }
      reject(new Error(`${path.basename(exe)} exited ${code}: ${stderr || stdout}`));
    });
  });
}

function parseShowInfoPts(stderr) {
  const timestamps = [];
  for (const line of stderr.split(/\r?\n/)) {
    const match = line.match(/pts_time:([0-9.]+)/);
    if (match) timestamps.push(Math.round(Number(match[1]) * 1000));
  }
  return timestamps;
}

export async function ffprobeJson(filePath) {
  const exe = resolveBinary('ffprobe');
  const { stdout } = await runBinary(exe, ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', filePath]);
  return JSON.parse(stdout);
}

export async function ffprobeFrames(filePath) {
  const exe = resolveBinary('ffprobe');
  const { stdout } = await runBinary(exe, [
    '-v', 'error',
    '-select_streams', 'v:0',
    '-show_frames',
    '-show_entries', 'frame=best_effort_timestamp_time,pkt_pts_time,pkt_duration_time',
    '-show_streams',
    '-show_format',
    '-of', 'json',
    filePath,
  ]);
  return JSON.parse(stdout);
}

export async function concatWavFiles(inputs, outputPath) {
  const exe = resolveBinary('ffmpeg');
  const listPath = `${outputPath}.concat.txt`;
  const listBody = inputs.map((file) => `file '${file.replace(/'/g, "'\\''")}'`).join('\n');
  await fs.writeFile(listPath, listBody, 'utf8');
  await runBinary(exe, ['-y', '-f', 'concat', '-safe', '0', '-i', listPath, '-c', 'copy', outputPath]);
  await fs.rm(listPath, { force: true });
}

export async function concatVideos(inputs, outputPath) {
  const exe = resolveBinary('ffmpeg');
  const listPath = `${outputPath}.concat.txt`;
  const listBody = inputs.map((file) => `file '${file.replace(/'/g, "'\\''")}'`).join('\n');
  await fs.writeFile(listPath, listBody, 'utf8');
  await runBinary(exe, ['-y', '-f', 'concat', '-safe', '0', '-i', listPath, '-c', 'copy', outputPath]);
  await fs.rm(listPath, { force: true });
}

export async function muxAudio(videoPath, audioPath, outputPath) {
  const exe = resolveBinary('ffmpeg');
  await runBinary(exe, [
    '-y',
    '-i', videoPath,
    '-i', audioPath,
    '-c:v', 'copy',
    '-c:a', 'libopus',
    '-shortest',
    outputPath,
  ]);
}

export async function getDurationMs(filePath) {
  const probe = await ffprobeJson(filePath);
  return Math.round(Number(probe?.format?.duration ?? 0) * 1000);
}

/**
 * Mux a captured video segment with its narration audio, but first pad
 * whichever one is shorter so the two are the same length before muxing.
 *
 * This replaces the old `-shortest` mux (see muxAudio above), which just
 * truncated whichever stream was longer — the root cause of "video is too
 * fast compared to the audio": segments were captured with fixed, narration-
 * independent hold durations, so audio routinely ran longer than the clip and
 * got clipped off mid-sentence, or the clip outran a short narration and sat
 * on a stale frame with no narration playing.
 *
 * - If narration audio is longer than the video: freeze the video's last
 *   frame (tpad) to stretch it out, so the visual doesn't end while the
 *   voiceover is still talking.
 * - If the video is longer than the audio: pad the audio with trailing
 *   silence (apad) rather than truncating real captured footage.
 */
export async function syncSegmentToAudio(videoPath, audioPath, outputPath, options = {}) {
  const exe = resolveBinary('ffmpeg');
  const toleranceMs = Number(options.toleranceMs ?? 150);
  const videoDurationMs = await getDurationMs(videoPath);
  const audioDurationMs = await getDurationMs(audioPath);
  const diffMs = audioDurationMs - videoDurationMs;

  let syncedVideoPath = videoPath;
  let syncedAudioPath = audioPath;
  const tempFiles = [];
  let action = 'no-op';

  const videoExt = path.extname(videoPath).toLowerCase();
  const isWebm = videoExt === '.webm';

  if (diffMs > toleranceMs) {
    action = 'padded-video';
    const padded = `${outputPath}.video-padded${videoExt}`;
    await runBinary(exe, [
      '-y', '-i', videoPath,
      '-vf', `tpad=stop_mode=clone:stop_duration=${(diffMs / 1000).toFixed(3)}`,
      '-c:v', isWebm ? 'libvpx' : 'libx264',
      ...(isWebm ? ['-b:v', '2M'] : ['-preset', 'veryfast', '-pix_fmt', 'yuv420p']),
      '-an',
      padded,
    ]);
    syncedVideoPath = padded;
    tempFiles.push(padded);
  } else if (diffMs < -toleranceMs) {
    action = 'padded-audio';
    const padded = `${outputPath}.audio-padded${path.extname(audioPath)}`;
    await runBinary(exe, [
      '-y', '-i', audioPath,
      '-af', `apad=whole_dur=${(videoDurationMs / 1000).toFixed(3)}`,
      padded,
    ]);
    syncedAudioPath = padded;
    tempFiles.push(padded);
  }

  await runBinary(exe, [
    '-y',
    '-i', syncedVideoPath,
    '-i', syncedAudioPath,
    '-c:v', 'copy',
    '-c:a', 'libopus',
    outputPath,
  ]);

  for (const file of tempFiles) {
    await fs.rm(file, { force: true }).catch(() => {});
  }

  return { videoDurationMs, audioDurationMs, diffMs, action, outputPath };
}

export async function extractFrame(videoPath, outputPath, timestamp) {
  const exe = resolveBinary('ffmpeg');
  await runBinary(exe, ['-y', '-ss', timestamp, '-i', videoPath, '-frames:v', '1', outputPath]);
}

export async function detectVisualActivity(videoPath, options = {}) {
  const exe = resolveBinary('ffmpeg');
  const threshold = Number(options.sceneThreshold ?? 0.0035);
  const { stderr } = await runBinary(exe, [
    '-i', videoPath,
    '-vf', `select='gt(scene\\,${threshold})',showinfo`,
    '-an',
    '-f', 'null',
    '-',
  ]);
  return parseShowInfoPts(stderr);
}

export async function trimVideoByActivity(videoPath, outputPath, activityLog, options = {}) {
  const probe = await ffprobeJson(videoPath);
  const durationMs = Math.round(Number(probe?.format?.duration ?? 0) * 1000);
  const hasAudio = probe.streams?.some((stream) => stream.codec_type === 'audio');
  const segments = buildKeepSegments({
    durationMs,
    events: activityLog,
    maxStaticMs: Number(options.maxStaticMs ?? 2500),
    retainAfterActivityMs: Number(options.retainAfterActivityMs ?? 900),
    retainBeforeActivityMs: Number(options.retainBeforeActivityMs ?? 1200),
    minSegmentMs: Number(options.minSegmentMs ?? 250),
  });
  const summary = summarizeTrim({ durationMs, segments });
  if (segments.length === 1 && summary.removedMs <= 0) {
    await fs.copyFile(videoPath, outputPath);
    return { ...summary, outputPath, copied: true };
  }

  const exe = resolveBinary('ffmpeg');
  const trimParts = [];
  const mapParts = [];
  segments.forEach((segment, index) => {
    const start = (segment.startMs / 1000).toFixed(3);
    const end = (segment.endMs / 1000).toFixed(3);
    trimParts.push(`[0:v]trim=start=${start}:end=${end},setpts=PTS-STARTPTS[v${index}]`);
    if (hasAudio) trimParts.push(`[0:a]atrim=start=${start}:end=${end},asetpts=PTS-STARTPTS[a${index}]`);
    mapParts.push(hasAudio ? `[v${index}][a${index}]` : `[v${index}]`);
  });
  const concatSuffix = hasAudio ? ':v=1:a=1[vout][aout]' : ':v=1:a=0[vout]';
  const filterComplex = `${trimParts.join(';')};${mapParts.join('')}concat=n=${segments.length}${concatSuffix}`;
  const args = ['-y', '-i', videoPath, '-filter_complex', filterComplex, '-map', '[vout]'];
  if (hasAudio) args.push('-map', '[aout]');

  const extension = path.extname(outputPath).toLowerCase();
  if (extension === '.webm') {
    args.push('-c:v', 'libvpx', '-b:v', '2M');
    if (hasAudio) args.push('-c:a', 'libopus');
  } else {
    args.push('-c:v', 'libx264', '-preset', 'veryfast', '-pix_fmt', 'yuv420p');
    if (hasAudio) args.push('-c:a', 'aac', '-b:a', '192k');
  }
  args.push(outputPath);
  await runBinary(exe, args);
  return { ...summary, outputPath, copied: false };
}
