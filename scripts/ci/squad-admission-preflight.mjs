#!/usr/bin/env node
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { LEDGER_KIND as KIND, validateAdmissionLedger } from './squad-admission-ledger.mjs';

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

export async function resolveDeclaredExternalStateDirectory({
  configPath = '.squad/config.json',
  readConfig = (path) => readFile(path, 'utf8'),
  resolveDirectory,
} = {}) {
  const config = JSON.parse(await readConfig(configPath));
  if (!config || config.stateLocation !== 'external') {
    throw new Error('Squad config must declare external state');
  }
  const directoryResolver = resolveDirectory ?? (await import('@bradygaster/squad-sdk')).resolveExternalStateDir;
  return directoryResolver(required(config.projectKey, 'Squad project key'), false);
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
  const [repository, value, headOption, headSha, teamRootOption, teamRoot, backendOption, stateBackend] = process.argv.slice(2);
  const prNumber = Number(value);
  if (!repository || !value || headOption !== '--head-sha' || !headSha
    || teamRootOption !== '--team-root' || !teamRoot || backendOption !== '--state-backend' || !stateBackend) {
    throw new Error('usage: squad-admission-preflight.mjs <repository> <pr-number> --head-sha <live-pr-head> --team-root <absolute-path> --state-backend <backend>');
  }
  if (!['local', 'worktree'].includes(stateBackend)) {
    throw new Error(`state backend ${stateBackend} requires the runtime state adapter; filesystem fallback is disabled`);
  }
  const stateDirectory = teamRoot;
  console.log(JSON.stringify(await runAdmissionPreflight(repository, prNumber, { stateDirectory, headSha })));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
