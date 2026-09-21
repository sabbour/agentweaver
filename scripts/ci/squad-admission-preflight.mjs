#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export const KIND = 'agentweaver.squad-admission-findings/v1';
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
  if (headSha !== exactSha(expected.headSha, 'expected.headSha')) throw new Error('ledger evidence is stale for the candidate head');
  required(ledger.coordinatorOwner, 'coordinatorOwner');
  const sources = ledger.reviewerSources;
  if (!Array.isArray(sources) || sources.length === 0) throw new Error('reviewerSources must be a non-empty array');
  const sourceIds = new Set();
  for (const [index, source] of sources.entries()) {
    const id = required(source?.id, `reviewerSources[${index}].id`);
    if (sourceIds.has(id)) throw new Error(`duplicate reviewer source: ${id}`);
    sourceIds.add(id);
    required(source.reviewer, `reviewerSources[${index}].reviewer`);
    if (exactSha(source.headSha, `reviewerSources[${index}].headSha`) !== headSha) throw new Error(`reviewerSources[${index}].headSha is stale`);
    required(source.evidence, `reviewerSources[${index}].evidence`);
  }
  const findingIds = new Set();
  for (const [index, finding] of (ledger.findings ?? []).entries()) {
    const prefix = `findings[${index}]`;
    const id = required(finding?.id, `${prefix}.id`);
    if (findingIds.has(id)) throw new Error(`duplicate finding: ${id}`);
    findingIds.add(id);
    if (finding.policy !== 'required') continue;
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

function main() {
  const [file, repository, prNumber, headSha] = process.argv.slice(2);
  if (!file || !repository || !prNumber || !headSha) throw new Error('usage: squad-admission-preflight.mjs <ledger.json> <repository> <pr-number> <head-sha>');
  console.log(JSON.stringify(validateAdmissionPreflight(JSON.parse(readFileSync(file, 'utf8')), { repository, prNumber: Number(prNumber), headSha })));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
