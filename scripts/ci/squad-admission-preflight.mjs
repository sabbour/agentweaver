#!/usr/bin/env node
import { stat, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { resolveExternalStateDir } from '@bradygaster/squad-sdk';

export const KIND = 'agentweaver.squad-admission-findings/v1';
const SHA = /^[0-9a-f]{40}$/iu;
const POLICIES = new Set(['advisory', 'required']);

function required(value, field) {
  if (typeof value !== 'string' || value.trim() === '') throw new Error(`${field} must be a non-empty string`);
  return value.trim();
}

function exactSha(value, field) {
  value = required(value, field);
  if (!SHA.test(value)) throw new Error(`${field} must be a 40-character SHA`);
  return value.toLowerCase();
}

function transition(entry, state, prefix, headSha) {
  if (!entry || entry.state !== state) throw new Error(`${prefix}.state must be ${state}`);
  required(entry.actor, `${prefix}.actor`);
  required(entry.at, `${prefix}.at`);
  if (exactSha(entry.headSha, `${prefix}.headSha`) !== headSha) throw new Error(`${prefix}.headSha is stale`);
  required(entry.evidence, `${prefix}.evidence`);
}

export function validateAdmissionPreflight(ledger, expected) {
  if (!ledger || ledger.kind !== KIND) throw new Error(`kind must be ${KIND}`);
  if (!expected || typeof expected !== 'object') throw new Error('expected admission context is required');
  if (required(ledger.repository, 'repository') !== required(expected.repository, 'expected.repository')) throw new Error('repository does not match');
  if (ledger.prNumber !== expected.prNumber) throw new Error('PR number does not match');
  const headSha = exactSha(ledger.headSha, 'headSha');
  if (headSha !== exactSha(expected.headSha, 'expected.headSha')) throw new Error('ledger evidence is stale for the live PR head');
  if (!Array.isArray(ledger.findings)) throw new Error('findings must be an array');

  const findingIds = new Set();
  for (const [index, finding] of ledger.findings.entries()) {
    const prefix = `findings[${index}]`;
    const id = required(finding?.id, `${prefix}.id`);
    if (findingIds.has(id)) throw new Error(`duplicate finding: ${id}`);
    findingIds.add(id);
    if (!POLICIES.has(finding.policy)) throw new Error(`${prefix}.policy must be advisory or required`);
    if (finding.policy === 'advisory') continue;

    const transitions = finding.transitions;
    if (!Array.isArray(transitions) || transitions.length !== 5) throw new Error(`${prefix}.transitions is incomplete`);
    transition(transitions[0], 'recorded', `${prefix}.transitions[0]`, headSha);
    transition(transitions[1], 'owned', `${prefix}.transitions[1]`, headSha);
    required(transitions[1].owner, `${prefix}.transitions[1].owner`);
    required(transitions[1].action, `${prefix}.transitions[1].action`);
    if (!['corrected', 'waived'].includes(transitions[2]?.state)) throw new Error(`${prefix}.transitions[2] must be corrected or waived`);
    transition(transitions[2], transitions[2].state, `${prefix}.transitions[2]`, headSha);
    if (transitions[2].state === 'waived') required(transitions[2].rationale, `${prefix}.transitions[2].rationale`);
    transition(transitions[3], 'revalidated', `${prefix}.transitions[3]`, headSha);
    required(transitions[3].validation, `${prefix}.transitions[3].validation`);
    transition(transitions[4], 'resolved', `${prefix}.transitions[4]`, headSha);
  }
  return { admitted: true, headSha, findings: findingIds.size };
}

async function readAuthoritativeLedger(stateDirectory, repository, prNumber) {
  const directory = required(stateDirectory, 'canonical external state directory');
  if (!(await stat(directory)).isDirectory()) throw new Error('canonical external state directory does not exist');
  const key = `admission/findings/${repository}/${prNumber}.json`;
  return JSON.parse(await readFile(join(directory, key), 'utf8'));
}

export async function resolveDeclaredExternalStateDirectory({
  configPath = '.squad/config.json',
  readConfig = (path) => readFile(path, 'utf8'),
  resolveDirectory = resolveExternalStateDir,
} = {}) {
  const config = JSON.parse(await readConfig(configPath));
  if (!config || config.stateLocation !== 'external') {
    throw new Error('Squad config must declare external state');
  }
  return resolveDirectory(required(config.projectKey, 'Squad project key'), false);
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
  const [repository, value, headOption, headSha] = process.argv.slice(2);
  const prNumber = Number(value);
  if (!repository || !value || headOption !== '--head-sha' || !headSha) {
    throw new Error('usage: squad-admission-preflight.mjs <repository> <pr-number> --head-sha <live-pr-head>');
  }
  const stateDirectory = await resolveDeclaredExternalStateDirectory();
  console.log(JSON.stringify(await runAdmissionPreflight(repository, prNumber, { stateDirectory, headSha })));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
