#!/usr/bin/env node
import { execFile } from 'node:child_process';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { relative, resolve, join } from 'node:path';
import { tmpdir } from 'node:os';
import { pathToFileURL } from 'node:url';
import { promisify } from 'node:util';

export const VALIDATOR_PATH = 'scripts/ci/squad-admission-preflight.mjs';
export const TRUSTED_REF = 'origin/dev';
const SHA = /^[0-9a-f]{40}$/iu;
const runFile = promisify(execFile);

function required(value, field) {
  if (typeof value !== 'string' || value.trim() === '') throw new Error(`${field} must be a non-empty string`);
  return value.trim();
}

function exactSha(value, field) {
  value = required(value, field);
  if (!SHA.test(value)) throw new Error(`${field} must be a 40-character SHA`);
  return value.toLowerCase();
}

function outside(candidate, parent) {
  const path = relative(resolve(parent), resolve(candidate));
  return path !== '' && (path === '..' || path.startsWith('..\\') || path.startsWith('../'));
}

export async function materializeTrustedValidator({
  command = runFile,
  repositoryRoot = process.cwd(),
  trustedRef = TRUSTED_REF,
  validatorPath = VALIDATOR_PATH,
  temporaryRoot = tmpdir(),
} = {}) {
  const blobSha = exactSha(
    (await command('git', ['rev-parse', `${trustedRef}:${validatorPath}`], { cwd: repositoryRoot })).stdout,
    'trusted validator blob',
  );
  const bytes = (await command('git', ['show', `${trustedRef}:${validatorPath}`], { cwd: repositoryRoot, maxBuffer: 1024 * 1024 })).stdout;
  const temporaryDirectory = await mkdtemp(join(temporaryRoot, 'agentweaver-admission-'));
  if (!outside(temporaryDirectory, repositoryRoot)) {
    await rm(temporaryDirectory, { recursive: true, force: true });
    throw new Error('trusted validator temporary directory must be outside the candidate checkout');
  }

  const validatorFile = join(temporaryDirectory, 'squad-admission-preflight.mjs');
  try {
    await writeFile(validatorFile, bytes, { encoding: 'utf8', flag: 'wx', mode: 0o600 });
  } catch (error) {
    await rm(temporaryDirectory, { recursive: true, force: true });
    throw error;
  }
  return { blobSha, trustedRef, validatorPath, temporaryDirectory, validatorFile };
}

async function liveHead(command, repository, prNumber, repositoryRoot) {
  const head = JSON.parse((await command('gh', [
    'pr', 'view', String(prNumber), '--repo', repository, '--json', 'headRefOid',
  ], { cwd: repositoryRoot })).stdout);
  return exactSha(head.headRefOid, 'live PR head');
}

export async function launchTrustedAdmission({
  repository,
  prNumber,
  command = runFile,
  execute = runFile,
  repositoryRoot = process.cwd(),
  trustedRef = TRUSTED_REF,
  validatorPath = VALIDATOR_PATH,
  temporaryRoot,
} = {}) {
  if (!/^[\w.-]+\/[\w.-]+$/u.test(repository)) throw new Error('repository must be owner/name');
  if (!Number.isSafeInteger(prNumber) || prNumber < 1) throw new Error('PR number must be a positive integer');

  const materialized = await materializeTrustedValidator({
    command, repositoryRoot, trustedRef, validatorPath, temporaryRoot,
  });
  try {
    const result = await execute(process.execPath, [
      materialized.validatorFile, repository, String(prNumber), '--trusted-validator-blob', materialized.blobSha,
    ], { cwd: repositoryRoot, maxBuffer: 1024 * 1024 });
    const validated = JSON.parse(required(result.stdout, 'trusted validator result'));
    const headSha = await liveHead(command, repository, prNumber, repositoryRoot);
    if (exactSha(validated.headSha, 'trusted validator headSha') !== headSha) {
      throw new Error('trusted validator result is stale for the live PR head');
    }
    if (validated.validator?.path !== validatorPath ||
      validated.validator?.ref !== trustedRef ||
      exactSha(validated.validator?.blobSha, 'trusted validator result blob') !== materialized.blobSha ||
      typeof validated.validator?.version !== 'string' ||
      validated.validator.version.trim() === '') {
      throw new Error('trusted validator result does not bind the materialized validator');
    }
    return { ...validated, headSha };
  } finally {
    await rm(materialized.temporaryDirectory, { recursive: true, force: true });
  }
}

async function main() {
  const [repository, value] = process.argv.slice(2);
  if (!repository || !value) {
    throw new Error('usage: squad-admission-launcher.mjs <repository> <pr-number>');
  }
  console.log(JSON.stringify(await launchTrustedAdmission({ repository, prNumber: Number(value) })));
}

if (process.argv[1] === '-' || (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
