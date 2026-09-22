#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { readFile, realpath } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, normalize, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

export const RUNTIME_KIND = 'agentweaver.squad-admission-runtime/v1';
const SHA = /^[0-9a-f]{40}$/iu;
const DIGEST = /^sha256:[0-9a-f]{64}$/iu;
const RUNTIME_FILES = Object.freeze([
  'squad-admission-authority.mjs',
  'squad-admission-ledger.mjs',
  'squad-admission-preflight.mjs',
]);

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

function policyDigest(files) {
  return digest(RUNTIME_FILES.map((name) => `${name}:${files[name]}`).join('\n'));
}

export async function loadTrustedRuntime(manifestPath, dependencies = {}) {
  const read = dependencies.readFile ?? readFile;
  const canonicalize = dependencies.realpath ?? realpath;
  const manifestArgument = required(manifestPath, 'runtime manifest');
  if (!isAbsolute(manifestArgument)) throw new Error('runtime manifest must be absolute');
  const requestedManifest = resolve(manifestArgument);
  const canonicalManifest = await canonicalize(requestedManifest);
  if (!samePath(requestedManifest, canonicalManifest)) throw new Error('runtime manifest must not use a symlink or canonical-path substitution');
  const runtimeDirectory = dirname(canonicalManifest);
  const manifest = JSON.parse(await read(canonicalManifest, 'utf8'));
  if (manifest.kind !== RUNTIME_KIND) throw new Error(`runtime manifest kind must be ${RUNTIME_KIND}`);
  if (!SHA.test(required(manifest.source?.commit, 'runtime source commit'))) {
    throw new Error('runtime source commit must be a 40-character SHA');
  }
  required(manifest.source?.ref, 'runtime source ref');
  if (!DIGEST.test(required(manifest.launcherDigest, 'runtime launcher digest'))) {
    throw new Error('runtime launcher digest must be a sha256 digest');
  }
  if (!DIGEST.test(required(manifest.policyDigest, 'runtime policy digest'))) {
    throw new Error('runtime policy digest must be a sha256 digest');
  }

  const launcherPath = join(runtimeDirectory, basename(fileURLToPath(import.meta.url)));
  const canonicalLauncher = await canonicalize(launcherPath);
  if (!samePath(launcherPath, canonicalLauncher)) throw new Error('runtime launcher must not use a symlink or canonical-path substitution');
  if (digest(await read(canonicalLauncher)) !== manifest.launcherDigest) {
    throw new Error('runtime launcher digest does not match the installed manifest');
  }

  const files = {};
  for (const name of RUNTIME_FILES) {
    const expected = required(manifest.files?.[name], `runtime digest for ${name}`);
    if (!DIGEST.test(expected)) throw new Error(`runtime digest for ${name} must be a sha256 digest`);
    const requested = join(runtimeDirectory, name);
    const canonical = await canonicalize(requested);
    if (!samePath(requested, canonical)) throw new Error(`${name} must not use a symlink or canonical-path substitution`);
    const actual = digest(await read(canonical));
    if (actual !== expected) throw new Error(`${name} digest does not match the installed manifest`);
    files[name] = actual;
  }
  if (policyDigest(files) !== manifest.policyDigest) {
    throw new Error('runtime policy digest does not match the installed manifest');
  }

  return {
    manifest: {
      kind: manifest.kind,
      source: { ref: manifest.source.ref, commit: manifest.source.commit.toLowerCase() },
      launcherDigest: manifest.launcherDigest,
      policyDigest: manifest.policyDigest,
    },
    ledger: await import(pathToFileURL(join(runtimeDirectory, 'squad-admission-ledger.mjs')).href),
    preflight: await import(pathToFileURL(join(runtimeDirectory, 'squad-admission-preflight.mjs')).href),
  };
}

function parseOptions(args) {
  return Object.fromEntries(Array.from({ length: args.length / 2 }, (_, index) => [args[index * 2], args[index * 2 + 1]]));
}

async function main() {
  const [command, ...args] = process.argv.slice(2);
  const options = parseOptions(args);
  const runtime = await loadTrustedRuntime(options['--runtime-manifest']);
  const common = {
    cwd: options['--worktree'],
    headSha: options['--head-sha'],
    baseSha: options['--base-sha'],
    trustedRuntime: runtime.manifest,
  };
  if (command === 'preflight') {
    const result = await runtime.preflight.runAdmissionPreflight(
      options['--repository'],
      Number(options['--pr-number']),
      {
        ...common,
        teamRoot: options['--team-root'],
        stateBackend: options['--state-backend'],
      },
    );
    process.stdout.write(`${JSON.stringify(result)}\n`);
    return;
  }
  if (command === 'materialize') {
    const input = JSON.parse(await readFile(options['--input'], 'utf8'));
    if (input.headSha?.toLowerCase() !== common.headSha?.toLowerCase()) {
      throw new Error('materialization input SHA does not match the launcher-attested live PR head');
    }
    const result = await runtime.ledger.materializeAdmissionLedger(input, {
      ...common,
      teamRoot: options['--team-root'],
      stateBackend: options['--state-backend'],
    });
    process.stdout.write(`${JSON.stringify(result)}\n`);
    return;
  }
  throw new Error('usage: squad-admission-launcher.mjs <materialize|preflight> --runtime-manifest <absolute-path> ...');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
