#!/usr/bin/env node
// Binds every coverage artifact to the workflow run attempt that produced it.
//
// "Re-run failed jobs" keeps the artifacts of jobs that passed and adds new
// artifacts for the jobs it reruns. A fixed artifact name therefore appears once
// per attempt, and a pattern download merges both copies into one directory.
// Each job records its attempt and status in `attempt.json` and uploads
// `<artifact>-<attempt>`; the coverage job keeps only the newest attempt of each
// suite and fails unless that attempt passed.
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const markerFile = 'attempt.json';

const artifactPattern = /^v1-(?:dotnet-(?<suite>[a-z0-9-]+?)|(?<surface>node|web)-coverage)-(?<attempt>[1-9][0-9]*)$/;

function positiveAttempt(value) {
  const attempt = Number(value);
  if (!Number.isSafeInteger(attempt) || attempt < 1) throw new Error(`Run attempt must be a positive integer: ${value}`);
  return attempt;
}

/** Writes the attempt marker that `selectAttempts` later verifies. */
export function recordAttempt({ directory, suite, runAttempt, jobStatus }) {
  if (!suite) throw new Error('Suite name is required');
  if (!jobStatus) throw new Error('Job status is required');
  mkdirSync(directory, { recursive: true });
  const marker = { suite, runAttempt: positiveAttempt(runAttempt), jobStatus };
  writeFileSync(join(directory, markerFile), `${JSON.stringify(marker, null, 2)}\n`);
  return marker;
}

/**
 * Moves the newest attempt of each downloaded coverage artifact into the
 * layout that `dotnet.mjs --combine` and the coverage report expect, and returns
 * the per-suite attempt manifest. Older attempts stay in `downloads`.
 */
export function selectAttempts(downloads, destination) {
  const candidates = new Map();
  for (const name of readdirSync(downloads).sort()) {
    const match = artifactPattern.exec(name);
    if (!match) throw new Error(`Coverage artifact ${name} is not named <artifact>-<run attempt>`);
    const { suite, surface } = match.groups;
    const target = suite ? `dotnet/${suite}` : surface;
    const attempt = Number(match.groups.attempt);
    const list = candidates.get(target) ?? [];
    list.push({ name, attempt, suite: suite ?? surface });
    candidates.set(target, list);
  }
  for (const required of ['node', 'web']) {
    if (!candidates.has(required)) throw new Error(`No ${required} coverage artifact was downloaded`);
  }
  if (![...candidates.keys()].some((target) => target.startsWith('dotnet/'))) {
    throw new Error('No .NET coverage artifact was downloaded');
  }

  const suites = {};
  for (const [target, list] of [...candidates].sort(([a], [b]) => a.localeCompare(b))) {
    list.sort((a, b) => b.attempt - a.attempt);
    const [newest, ...older] = list;
    const source = join(downloads, newest.name);
    const markerPath = join(source, markerFile);
    if (!existsSync(markerPath)) throw new Error(`${newest.name} has no ${markerFile}`);
    const marker = JSON.parse(readFileSync(markerPath, 'utf8'));
    if (marker.suite !== newest.suite || marker.runAttempt !== newest.attempt) {
      throw new Error(`${newest.name} contains data recorded by ${marker.suite} attempt ${marker.runAttempt}`);
    }
    if (marker.jobStatus !== 'success') {
      throw new Error(`${newest.name} is from a ${marker.jobStatus} job; rerun the suite before combining coverage`);
    }
    const output = join(destination, target);
    if (existsSync(output)) throw new Error(`Coverage destination ${output} already exists`);
    mkdirSync(dirname(output), { recursive: true });
    renameSync(source, output);
    suites[target] = { artifact: newest.name, attempt: newest.attempt, superseded: older.map(({ attempt }) => attempt) };
  }
  const manifest = { suites };
  writeFileSync(join(destination, 'attempts.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  return manifest;
}

function main() {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'record' && args.length === 2) {
    recordAttempt({
      directory: args[0], suite: args[1],
      runAttempt: process.env.GITHUB_RUN_ATTEMPT, jobStatus: process.env.JOB_STATUS,
    });
  } else if (command === 'select' && args.length === 2) {
    const { suites } = selectAttempts(resolve(args[0]), resolve(args[1]));
    for (const [target, { attempt, superseded }] of Object.entries(suites)) {
      console.log(`${target}: attempt ${attempt}${superseded.length ? ` (ignored attempts ${superseded.join(', ')})` : ''}`);
    }
  } else {
    throw new Error('Usage: attempts.mjs record <directory> <suite> | select <downloads> <destination>');
  }
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(); } catch (error) { console.error(`::error::${error.message}`); process.exit(1); }
}
