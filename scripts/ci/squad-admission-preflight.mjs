#!/usr/bin/env node
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { LEDGER_KIND as KIND, loadStateAdapter, validateAdmissionLedger } from './squad-admission-ledger.mjs';

export { KIND };
const SHA = /^[0-9a-f]{40}$/iu;

function required(value, field) {
  if (typeof value !== 'string' || value.trim() === '') throw new Error(`${field} must be a non-empty string`);
  return value.trim();
}

function exactSha(value, field) {
  value = required(value, field);
  if (!SHA.test(value)) throw new Error(`${field} must be a 40-character SHA`);
  return value.toLowerCase();
}

export const validateAdmissionPreflight = validateAdmissionLedger;

async function readAuthoritativeLedger(stateDirectory, repository, prNumber) {
  const directory = required(stateDirectory, 'canonical external state directory');
  const key = `admission/findings/${repository}/${prNumber}.json`;
  return JSON.parse(await readFile(join(directory, key), 'utf8'));
}

export async function runAdmissionPreflight(repository, prNumber, dependencies = {}) {
  if (!/^[\w.-]+\/[\w.-]+$/u.test(repository)) throw new Error('repository must be owner/name');
  if (!Number.isSafeInteger(prNumber) || prNumber < 1) throw new Error('PR number must be a positive integer');
  const stateDirectory = required(dependencies.stateDirectory, 'canonical external state directory');
  const headSha = exactSha(dependencies.headSha, 'launcher-attested live PR head');
  const ledger = dependencies.readLedger
    ? await dependencies.readLedger(stateDirectory, repository, prNumber)
    : await readAuthoritativeLedger(stateDirectory, repository, prNumber);
  return {
    ...validateAdmissionPreflight(ledger, { repository, prNumber, headSha }),
    stateDirectory,
  };
}

async function main() {
  const [repository, value, ...args] = process.argv.slice(2);
  const options = Object.fromEntries(Array.from({ length: args.length / 2 }, (_, index) => [args[index * 2], args[index * 2 + 1]]));
  const prNumber = Number(value);
  const headSha = options['--head-sha'];
  const teamRoot = options['--team-root'];
  const stateBackend = options['--state-backend'];
  if (!repository || !value || !headSha || !teamRoot || !stateBackend) {
    throw new Error('usage: squad-admission-preflight.mjs <repository> <pr-number> --head-sha <live-pr-head> --team-root <absolute-path> --state-backend <backend> [--state-adapter <module>]');
  }
  const local = ['local', 'worktree'].includes(stateBackend);
  const stateAdapter = options['--state-adapter']
    ? await loadStateAdapter(options['--state-adapter'], { teamRoot, stateBackend })
    : undefined;
  if (!local && !stateAdapter) throw new Error(`state backend ${stateBackend} requires --state-adapter; filesystem fallback is disabled`);
  console.log(JSON.stringify(await runAdmissionPreflight(repository, prNumber, {
    stateDirectory: teamRoot,
    headSha,
    readLedger: stateAdapter
      ? async (_, ownerRepository, number) => JSON.parse(await stateAdapter.read(`admission/findings/${ownerRepository}/${number}.json`))
      : undefined,
  })));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
