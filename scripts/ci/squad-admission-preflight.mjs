#!/usr/bin/env node
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { LEDGER_KIND as KIND, validateAdmissionLedger } from './squad-admission-ledger.mjs';
import {
  assertAdmissionAuthority,
  resolveAdmissionAuthority,
  resolveAdmissionReviewPolicy,
} from './squad-admission-authority.mjs';

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

async function readAuthoritativeLedger(authority, repository, prNumber, stateAdapter) {
  const key = `admission/findings/${repository}/${prNumber}.json`;
  if (authority.stateBackend === 'local') {
    return JSON.parse(await readFile(join(authority.teamRoot, key), 'utf8'));
  }
  if (!stateAdapter || typeof stateAdapter.read !== 'function') {
    throw new Error(`state backend ${authority.stateBackend} requires an explicit runtime-owned read adapter`);
  }
  return JSON.parse(await stateAdapter.read(key));
}

export async function runAdmissionPreflight(repository, prNumber, dependencies = {}) {
  if (!/^[\w.-]+\/[\w.-]+$/u.test(repository)) throw new Error('repository must be owner/name');
  if (!Number.isSafeInteger(prNumber) || prNumber < 1) throw new Error('PR number must be a positive integer');
  const authority = dependencies.authority ?? await resolveAdmissionAuthority({ cwd: dependencies.cwd });
  const trusted = dependencies.teamRoot || dependencies.stateBackend
    ? await assertAdmissionAuthority(authority, {
      teamRoot: dependencies.teamRoot,
      stateBackend: dependencies.stateBackend,
    })
    : authority;
  const headSha = exactSha(dependencies.headSha, 'launcher-attested live PR head');
  const requiredReviewSources = await resolveAdmissionReviewPolicy(
    { cwd: dependencies.cwd, headSha, baseSha: dependencies.baseSha },
    { run: dependencies.policyRun },
  );
  const ledger = dependencies.readLedger
    ? await dependencies.readLedger(trusted, repository, prNumber)
    : await readAuthoritativeLedger(trusted, repository, prNumber, dependencies.stateAdapter);
  return {
    ...validateAdmissionPreflight(ledger, {
      repository,
      prNumber,
      headSha,
      baseSha: dependencies.baseSha,
      requiredReviewSources,
      trustedRuntime: dependencies.trustedRuntime,
    }),
    stateDirectory: trusted.teamRoot,
    stateBackend: trusted.stateBackend,
    baseSha: dependencies.baseSha,
    trustedRuntime: dependencies.trustedRuntime,
  };
}

async function main() {
  throw new Error('candidate checkout admission code is evidence only; invoke the installed runtime-owned launcher');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
