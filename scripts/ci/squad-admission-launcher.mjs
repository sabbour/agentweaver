#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, isAbsolute, join, normalize, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export const RUNTIME_KIND = 'agentweaver.squad-admission-runtime/v2';
export const LAUNCHER_PATH = 'scripts/ci/squad-admission-launcher.mjs';
export const RUNTIME_PATHS = Object.freeze([
  'scripts/ci/squad-admission-authority.mjs',
  'scripts/ci/squad-admission-ledger.mjs',
  'scripts/ci/squad-admission-preflight.mjs',
]);
const SHA = /^[0-9a-f]{40}$/iu;
const OBJECT_ID = /^[0-9a-f]{40,64}$/iu;

function required(value, field) {
  if (typeof value !== 'string' || value.trim() === '') throw new Error(`${field} must be a non-empty string`);
  return value.trim();
}

function samePath(left, right) {
  const normalizeCase = (value) => process.platform === 'win32' ? normalize(value).toLowerCase() : normalize(value);
  return normalizeCase(left) === normalizeCase(right);
}

function digest(bytes) {
  return `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
}

function aggregateDigest(files) {
  return digest(Object.entries(files)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([path, identity]) => `${path}:${identity.objectId}:${identity.digest}`)
    .join('\n'));
}

function spawnGit(args, cwd) {
  return new Promise((resolveResult, reject) => {
    const child = spawn('git', args, { cwd, windowsHide: true });
    const stdout = [];
    const stderr = [];
    child.stdout.on('data', (chunk) => { stdout.push(chunk); });
    child.stderr.on('data', (chunk) => { stderr.push(chunk); });
    child.on('error', reject);
    child.on('close', (exitCode) => resolveResult({
      exitCode,
      stdout: Buffer.concat(stdout),
      stderr: Buffer.concat(stderr).toString('utf8'),
    }));
  });
}

async function git(args, cwd, run = spawnGit) {
  const result = await run(args, cwd);
  if (result.exitCode !== 0) throw new Error(`git ${args.join(' ')} failed: ${result.stderr.trim()}`);
  return result.stdout;
}

async function gitText(args, cwd, run) {
  return (await git(args, cwd, run)).toString('utf8').trim();
}

async function fetchTrustedBase({ worktree, baseRef, baseSha }, run) {
  const match = /^refs\/remotes\/([^/]+)\/(.+)$/u.exec(baseRef);
  if (!match) throw new Error('trusted base ref must be refs/remotes/<remote>/<branch>');
  const [, remote, branch] = match;
  await git(['fetch', '--no-tags', remote, `+refs/heads/${branch}:${baseRef}`], worktree, run);
  const fetched = (await gitText(['rev-parse', '--verify', `${baseRef}^{commit}`], worktree, run)).toLowerCase();
  if (!SHA.test(fetched)) throw new Error('fetched trusted base did not resolve to a commit SHA');
  if (fetched !== baseSha.toLowerCase()) {
    throw new Error(`trusted base moved or is stale: expected ${baseSha.toLowerCase()}, fetched ${fetched}`);
  }
  return fetched;
}

async function readTrustedFile({ worktree, baseSha, path }, run) {
  const objectId = (await gitText(['rev-parse', `${baseSha}:${path}`], worktree, run)).toLowerCase();
  if (!OBJECT_ID.test(objectId)) throw new Error(`trusted object ID for ${path} is invalid`);
  const bytes = await git(['cat-file', 'blob', objectId], worktree, run);
  return { bytes, identity: { objectId, digest: digest(bytes) } };
}

async function canonicalAbsolute(value, field, canonicalize = realpath) {
  const requested = required(value, field);
  if (!isAbsolute(requested)) throw new Error(`${field} must be absolute`);
  const absolute = resolve(requested);
  const canonical = await canonicalize(absolute);
  if (!samePath(absolute, canonical)) throw new Error(`${field} must not use a symlink, reparse point, or canonical-path substitution`);
  return canonical;
}

export async function loadTrustedRuntime({
  worktree,
  baseRef,
  baseSha,
  launcherPath = process.argv[1],
}, dependencies = {}) {
  const run = dependencies.runGit ?? spawnGit;
  const makeTemp = dependencies.mkdtemp ?? mkdtemp;
  const remove = dependencies.rm ?? rm;
  const read = dependencies.readFile ?? readFile;
  const write = dependencies.writeFile ?? writeFile;
  const canonicalize = dependencies.realpath ?? realpath;
  const importModule = dependencies.importModule ?? ((path) => import(`${pathToFileURL(path).href}?trusted=${baseSha}`));
  const canonicalWorktree = await canonicalAbsolute(worktree, 'candidate worktree', canonicalize);
  const canonicalLauncher = await canonicalAbsolute(launcherPath, 'trusted launcher', canonicalize);
  const trustedBaseRef = required(baseRef, 'trusted base ref');
  const trustedBaseSha = required(baseSha, 'trusted base SHA').toLowerCase();
  if (!SHA.test(trustedBaseSha)) throw new Error('trusted base SHA must be a 40-character SHA');

  await fetchTrustedBase({
    worktree: canonicalWorktree,
    baseRef: trustedBaseRef,
    baseSha: trustedBaseSha,
  }, run);

  const launcher = await readTrustedFile({
    worktree: canonicalWorktree,
    baseSha: trustedBaseSha,
    path: LAUNCHER_PATH,
  }, run);
  if (!Buffer.from(await read(canonicalLauncher)).equals(launcher.bytes)) {
    throw new Error('trusted launcher bytes do not match the fetched base object');
  }

  const directory = await makeTemp(join(tmpdir(), 'agentweaver-admission-'));
  try {
    const files = {
      [LAUNCHER_PATH]: launcher.identity,
    };
    for (const path of RUNTIME_PATHS) {
      const trusted = await readTrustedFile({
        worktree: canonicalWorktree,
        baseSha: trustedBaseSha,
        path,
      }, run);
      const destination = join(directory, basename(path));
      await write(destination, trusted.bytes, { flag: 'wx' });
      if (!Buffer.from(await read(destination)).equals(trusted.bytes)) {
        throw new Error(`ephemeral bytes for ${path} do not match the fetched base object`);
      }
      files[path] = trusted.identity;
    }

    const runtime = {
      kind: RUNTIME_KIND,
      source: { ref: trustedBaseRef, commit: trustedBaseSha },
      files,
      aggregateDigest: aggregateDigest(files),
    };
    return {
      directory,
      runtime,
      ledger: await importModule(join(directory, 'squad-admission-ledger.mjs')),
      preflight: await importModule(join(directory, 'squad-admission-preflight.mjs')),
      cleanup: () => remove(directory, { recursive: true, force: true }),
    };
  } catch (error) {
    await remove(directory, { recursive: true, force: true });
    throw error;
  }
}

function parseOptions(args) {
  if (args.length % 2 !== 0) throw new Error('launcher options must be name/value pairs');
  return Object.fromEntries(Array.from({ length: args.length / 2 }, (_, index) => [args[index * 2], args[index * 2 + 1]]));
}

export async function runTrustedAdmission(command, options, dependencies = {}) {
  const runtime = await loadTrustedRuntime({
    worktree: options['--worktree'],
    baseRef: options['--base-ref'],
    baseSha: options['--base-sha'],
    launcherPath: options['--launcher-path'] ?? process.argv[1],
  }, dependencies);
  try {
    const common = {
      cwd: options['--worktree'],
      headSha: options['--head-sha'],
      baseSha: runtime.runtime.source.commit,
      trustedRuntime: runtime.runtime,
    };
    if (command === 'preflight') {
      return runtime.preflight.runAdmissionPreflight(
        options['--repository'],
        Number(options['--pr-number']),
        {
          ...common,
          teamRoot: options['--team-root'],
          stateBackend: options['--state-backend'],
        },
      );
    }
    if (command === 'materialize') {
      const input = JSON.parse(await (dependencies.readFile ?? readFile)(options['--input'], 'utf8'));
      if (input.headSha?.toLowerCase() !== common.headSha?.toLowerCase()) {
        throw new Error('materialization input SHA does not match the launcher-attested live PR head');
      }
      return runtime.ledger.materializeAdmissionLedger(input, {
        ...common,
        teamRoot: options['--team-root'],
        stateBackend: options['--state-backend'],
      });
    }
    throw new Error('command must be materialize or preflight');
  } finally {
    await runtime.cleanup();
  }
}

async function main() {
  const [command, ...args] = process.argv.slice(2);
  const result = await runTrustedAdmission(command, parseOptions(args));
  process.stdout.write(`${JSON.stringify(result)}\n`);
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
