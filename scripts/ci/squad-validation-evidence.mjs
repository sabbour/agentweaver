#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { realpath } from 'node:fs/promises';
import { isAbsolute, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { buildSpawnPlan } from '../azure/lib/exec.mjs';

const SHA = /^[0-9a-f]{40}$/iu;

function required(value, field) {
  if (typeof value !== 'string' || value.trim() === '') throw new Error(`${field} must be a non-empty string`);
  return value.trim();
}

async function git(args, cwd, run = spawnCommand) {
  const result = await run(['git', ...args], cwd);
  if (result.exitCode !== 0) throw new Error(`git ${args.join(' ')} failed: ${result.stderr.trim()}`);
  return result.stdout.trim();
}

function spawnCommand(argv, cwd) {
  const plan = buildSpawnPlan(argv[0], argv.slice(1));
  return new Promise((resolveResult, reject) => {
    const child = spawn(plan.file, plan.spawnArgs, { ...plan.spawnOpts, cwd, windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', (exitCode, signal) => resolveResult({ exitCode, signal, stdout, stderr }));
  });
}

async function snapshot(cwd, run) {
  return {
    cwd,
    worktree: resolve(await git(['rev-parse', '--show-toplevel'], cwd, run)),
    branch: await git(['branch', '--show-current'], cwd, run),
    headSha: (await git(['rev-parse', 'HEAD'], cwd, run)).toLowerCase(),
    status: await git(['status', '--porcelain=v1', '--untracked-files=all'], cwd, run),
  };
}

function assertExpected(actual, expected, when) {
  if (actual.cwd !== expected.worktree) throw new Error(`${when} CWD does not match expected worktree`);
  if (actual.worktree !== expected.worktree) throw new Error(`${when} git top-level does not match expected worktree`);
  if (actual.branch !== expected.branch) throw new Error(`${when} branch does not match expected branch`);
  if (actual.headSha !== expected.headSha) throw new Error(`${when} HEAD does not match expected SHA`);
  if (actual.status !== '') throw new Error(`${when} worktree is not clean`);
}

export async function runValidationEvidence({
  argv,
  expectedWorktree,
  expectedBranch,
  expectedHeadSha,
  cwd = process.cwd(),
}, dependencies = {}) {
  if (!Array.isArray(argv) || argv.length === 0 || argv.some((value) => typeof value !== 'string' || value === '')) {
    throw new Error('argv must contain one command and its exact string arguments');
  }
  if (!isAbsolute(cwd)) throw new Error('current CWD must be absolute');
  if (!isAbsolute(expectedWorktree)) throw new Error('expected worktree must be absolute');

  const run = dependencies.run ?? spawnCommand;
  const canonicalize = dependencies.realpath ?? realpath;
  const canonicalCwd = await canonicalize(cwd);
  const expected = {
    worktree: await canonicalize(expectedWorktree),
    branch: required(expectedBranch, 'expected branch'),
    headSha: required(expectedHeadSha, 'expected SHA').toLowerCase(),
  };
  if (!SHA.test(expected.headSha)) throw new Error('expected SHA must be a 40-character SHA');

  const before = await snapshot(canonicalCwd, run);
  assertExpected(before, expected, 'before execution');
  const startedAt = new Date().toISOString();
  const result = await run(argv, canonicalCwd);
  const completedAt = new Date().toISOString();
  const after = await snapshot(canonicalCwd, run);

  const evidence = {
    kind: 'agentweaver.validation-evidence/v1',
    argv: [...argv],
    cwd: canonicalCwd,
    worktree: before.worktree,
    branch: before.branch,
    headSha: before.headSha,
    startedAt,
    completedAt,
    exitCode: result.exitCode,
    signal: result.signal ?? null,
    result: result.exitCode === 0 ? 'passed' : 'failed',
    stdout: result.stdout,
    stderr: result.stderr,
    after,
  };

  try {
    assertExpected(after, expected, 'after execution');
  } catch (error) {
    error.evidence = { ...evidence, result: 'provenance-mismatch' };
    throw error;
  }
  return evidence;
}

function parseCli(args) {
  const separator = args.indexOf('--');
  if (separator < 0 || separator === args.length - 1) {
    throw new Error('usage: squad-validation-evidence.mjs --worktree <absolute-path> --branch <branch> --head-sha <sha> -- <command> [args...]');
  }
  const options = Object.fromEntries(Array.from({ length: separator / 2 }, (_, index) => [args[index * 2], args[index * 2 + 1]]));
  return {
    expectedWorktree: options['--worktree'],
    expectedBranch: options['--branch'],
    expectedHeadSha: options['--head-sha'],
    argv: args.slice(separator + 1),
  };
}

async function main() {
  try {
    const evidence = await runValidationEvidence(parseCli(process.argv.slice(2)));
    process.stdout.write(`${JSON.stringify(evidence)}\n`);
    process.exitCode = evidence.exitCode;
  } catch (error) {
    if (error.evidence) process.stdout.write(`${JSON.stringify(error.evidence)}\n`);
    throw error;
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
