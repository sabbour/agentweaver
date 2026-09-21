#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { relative, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createInterface } from 'node:readline';
import { promisify } from 'node:util';
import { execFile } from 'node:child_process';

export const KIND = 'agentweaver.squad-admission-findings/v1';
const SHA = /^[0-9a-f]{40}$/iu;
const POLICIES = new Set(['advisory', 'required']);
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

function transition(entry, state, prefix, headSha) {
  if (!entry || entry.state !== state) throw new Error(`${prefix}.state must be ${state}`);
  required(entry.actor, `${prefix}.actor`);
  required(entry.at, `${prefix}.at`);
  if (exactSha(entry.headSha, `${prefix}.headSha`) !== headSha) throw new Error(`${prefix}.headSha is stale`);
  required(entry.evidence, `${prefix}.evidence`);
}

function isOutside(candidate, parent) {
  const path = relative(resolve(parent), resolve(candidate));
  return path !== '' && (path === '..' || path.startsWith('..\\') || path.startsWith('../'));
}

function isChild(candidate, parent) {
  const path = relative(resolve(parent), resolve(candidate));
  return path !== '' && !isOutside(candidate, parent);
}

export function assertAuthoritativeState(status, repositoryRoot, appData = process.env.APPDATA) {
  const active = /^ {2}Active squad:\s*external\s*$/imu.test(status);
  const path = /^ {2}Path:\s*(.+?)\s*$/imu.exec(status)?.[1];
  if (!active || !path || !appData) throw new Error('Squad state source cannot prove authoritative external state');
  const expectedRoot = resolve(appData, 'squad', 'projects');
  const statePath = resolve(path);
  if (!isOutside(statePath, repositoryRoot) || !isChild(statePath, expectedRoot)) {
    throw new Error('Squad state source is repository-controlled or outside the authoritative external state root');
  }
  return statePath;
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

function textResult(result) {
  const text = result?.content?.find((item) => item.type === 'text')?.text;
  return required(text, 'Squad state bridge response');
}

function openStateBridge() {
  const child = spawn('npx', ['-y', '@bradygaster/squad-cli@0.13.1', 'state-mcp'], { stdio: ['pipe', 'pipe', 'pipe'] });
  const pending = new Map();
  let nextId = 1;
  const fail = (error) => {
    for (const { reject } of pending.values()) reject(error);
    pending.clear();
  };
  child.once('error', fail);
  child.once('exit', (code) => fail(new Error(`Squad state bridge exited before preflight completed (${code ?? 'unknown'})`)));
  createInterface({ input: child.stdout }).on('line', (line) => {
    try {
      const message = JSON.parse(line);
      const request = pending.get(message.id);
      if (!request) return;
      pending.delete(message.id);
      if (message.error) request.reject(new Error(message.error.message ?? 'Squad state bridge request failed'));
      else request.resolve(message.result);
    } catch { /* Ignore non-protocol diagnostics emitted by the official bridge. */ }
  });
  const call = (method, params) => new Promise((resolveCall, reject) => {
    const id = nextId++;
    pending.set(id, { resolve: resolveCall, reject });
    child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
  });
  return {
    async read(key) {
      await call('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'agentweaver-admission-preflight', version: '1' } });
      child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' })}\n`);
      await call('tools/call', { name: 'squad_state_health', arguments: {} });
      return textResult(await call('tools/call', { name: 'squad_state_read', arguments: { key } }));
    },
    close() {
      child.kill();
    },
  };
}

export async function runAdmissionPreflight(repository, prNumber, dependencies = {}) {
  if (!/^[\w.-]+\/[\w.-]+$/u.test(repository)) throw new Error('repository must be owner/name');
  if (!Number.isSafeInteger(prNumber) || prNumber < 1) throw new Error('PR number must be a positive integer');
  const command = dependencies.command ?? runFile;
  const repositoryRoot = dependencies.repositoryRoot ?? process.cwd();
  const status = await command('npx', ['-y', '@bradygaster/squad-cli@0.13.1', 'status']);
  assertAuthoritativeState(status.stdout, repositoryRoot, dependencies.appData);

  const bridge = (dependencies.openBridge ?? openStateBridge)();
  try {
    const key = `admission/findings/${repository}/${prNumber}.json`;
    const ledger = JSON.parse(await bridge.read(key));
    const head = JSON.parse((await command('gh', ['pr', 'view', String(prNumber), '--repo', repository, '--json', 'headRefOid'])).stdout);
    const headSha = exactSha(head.headRefOid, 'live PR head');
    return validateAdmissionPreflight(ledger, { repository, prNumber, headSha });
  } finally {
    bridge.close();
  }
}

async function main() {
  const [repository, value] = process.argv.slice(2);
  const prNumber = Number(value);
  if (!repository || !value) throw new Error('usage: squad-admission-preflight.mjs <repository> <pr-number>');
  console.log(JSON.stringify(await runAdmissionPreflight(repository, prNumber)));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
