import { createHash } from 'node:crypto';
import { constants } from 'node:fs';
import { lstat, mkdir, open, readFile, realpath } from 'node:fs/promises';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';

export const KIND = 'agentweaver.squad-admission-findings/v1';
const SHA = /^[0-9a-f]{40}$/u;
const PR_NUMBER = /^[1-9][0-9]*$/u;
const OWNER = /^[a-z0-9](?:[a-z0-9-]{0,37}[a-z0-9])?$/u;
const REPOSITORY = /^[a-z0-9._-]+$/u;
const UTC_TIMESTAMP = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?Z$/u;
const DEVICE_ALIAS = /^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/u;
const POLICIES = new Set(['advisory', 'required']);
const BASE_TRANSITION_KEYS = ['actor', 'at', 'evidence', 'headSha', 'state'];
const TRANSITION_KEYS = {
  recorded: BASE_TRANSITION_KEYS,
  owned: [...BASE_TRANSITION_KEYS, 'action', 'owner'],
  corrected: BASE_TRANSITION_KEYS,
  waived: [...BASE_TRANSITION_KEYS, 'rationale'],
  revalidated: [...BASE_TRANSITION_KEYS, 'validation'],
  resolved: BASE_TRANSITION_KEYS,
};

function requiredString(value, field) {
  if (typeof value !== 'string' || value.length === 0 || value !== value.trim()) {
    throw new Error(`${field} must be a non-empty canonical string`);
  }
  return value;
}

function exactObject(value, keys, field) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${field} must be an object`);
  }
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) {
    throw new Error(`${field} must contain exactly: ${expected.join(', ')}`);
  }
  return value;
}

function canonicalSegment(value, field, pattern) {
  requiredString(value, field);
  if (!value.isWellFormed() || value.normalize('NFC') !== value || !/^[\x21-\x7e]+$/u.test(value)) {
    throw new Error(`${field} must use canonical lowercase ASCII`);
  }
  if (value !== value.toLowerCase() || !pattern.test(value) || value === '.' || value === '..'
    || value.endsWith('.') || value.endsWith(' ') || DEVICE_ALIAS.test(value)) {
    throw new Error(`${field} is not a canonical GitHub path segment`);
  }
  return value;
}

export function canonicalRepository(value) {
  requiredString(value, 'repository');
  const parts = value.split('/');
  if (parts.length !== 2) throw new Error('repository must be canonical owner/repo');
  return `${canonicalSegment(parts[0], 'repository owner', OWNER)}/${canonicalSegment(parts[1], 'repository name', REPOSITORY)}`;
}

export function canonicalPrNumber(value, field = 'PR number') {
  const text = typeof value === 'number' ? String(value) : value;
  if (typeof text !== 'string' || !PR_NUMBER.test(text)) {
    throw new Error(`${field} must be a canonical positive decimal integer`);
  }
  const number = Number(text);
  if (!Number.isSafeInteger(number)) throw new Error(`${field} exceeds the safe integer range`);
  return number;
}

export function canonicalSha(value, field = 'head SHA') {
  if (typeof value !== 'string' || !SHA.test(value)) {
    throw new Error(`${field} must be a lowercase 40-character SHA`);
  }
  return value;
}

function timestamp(value, field) {
  requiredString(value, field);
  const parsed = Date.parse(value);
  const normalized = value.includes('.')
    ? value.replace(/\.(\d{1,3})Z$/u, (_, fraction) => `.${fraction.padEnd(3, '0')}Z`)
    : value.replace(/Z$/u, '.000Z');
  if (!UTC_TIMESTAMP.test(value) || !Number.isFinite(parsed) || new Date(parsed).toISOString() !== normalized) {
    throw new Error(`${field} must be a valid UTC RFC 3339 timestamp`);
  }
  return value;
}

function transition(value, state, field, headSha) {
  exactObject(value, TRANSITION_KEYS[state], field);
  if (value.state !== state) throw new Error(`${field}.state must be ${state}`);
  requiredString(value.actor, `${field}.actor`);
  const at = timestamp(value.at, `${field}.at`);
  if (canonicalSha(value.headSha, `${field}.headSha`) !== headSha) throw new Error(`${field}.headSha is stale`);
  requiredString(value.evidence, `${field}.evidence`);
  if (state === 'owned') {
    requiredString(value.owner, `${field}.owner`);
    requiredString(value.action, `${field}.action`);
  } else if (state === 'waived') {
    requiredString(value.rationale, `${field}.rationale`);
  } else if (state === 'revalidated') {
    requiredString(value.validation, `${field}.validation`);
  }
  return Date.parse(at);
}

export function validateAdmissionLedger(ledger, expected) {
  exactObject(ledger, ['kind', 'repository', 'prNumber', 'headSha', 'findings'], 'ledger');
  if (ledger.kind !== KIND) throw new Error(`kind must be ${KIND}`);
  const repository = canonicalRepository(ledger.repository);
  const prNumber = canonicalPrNumber(ledger.prNumber, 'ledger.prNumber');
  const headSha = canonicalSha(ledger.headSha, 'ledger.headSha');
  if (expected) {
    if (repository !== canonicalRepository(expected.repository)) throw new Error('repository does not match');
    if (prNumber !== canonicalPrNumber(expected.prNumber, 'expected.prNumber')) throw new Error('PR number does not match');
    if (headSha !== canonicalSha(expected.headSha, 'expected.headSha')) {
      throw new Error('ledger evidence is stale for the live PR head');
    }
  }
  if (!Array.isArray(ledger.findings)) throw new Error('findings must be an array');

  const findingIds = new Set();
  for (const [index, finding] of ledger.findings.entries()) {
    const field = `findings[${index}]`;
    if (!finding || !POLICIES.has(finding.policy)) throw new Error(`${field}.policy must be advisory or required`);
    exactObject(finding, finding.policy === 'advisory' ? ['id', 'policy'] : ['id', 'policy', 'transitions'], field);
    const id = requiredString(finding.id, `${field}.id`);
    if (findingIds.has(id)) throw new Error(`duplicate finding: ${id}`);
    findingIds.add(id);
    if (finding.policy === 'advisory') continue;
    if (!Array.isArray(finding.transitions) || finding.transitions.length !== 5) {
      throw new Error(`${field}.transitions is incomplete`);
    }
    const times = [
      transition(finding.transitions[0], 'recorded', `${field}.transitions[0]`, headSha),
      transition(finding.transitions[1], 'owned', `${field}.transitions[1]`, headSha),
    ];
    const remediation = finding.transitions[2]?.state;
    if (remediation !== 'corrected' && remediation !== 'waived') {
      throw new Error(`${field}.transitions[2].state must be corrected or waived`);
    }
    times.push(
      transition(finding.transitions[2], remediation, `${field}.transitions[2]`, headSha),
      transition(finding.transitions[3], 'revalidated', `${field}.transitions[3]`, headSha),
      transition(finding.transitions[4], 'resolved', `${field}.transitions[4]`, headSha),
    );
    if (times.some((time, transitionIndex) => transitionIndex > 0 && time < times[transitionIndex - 1])) {
      throw new Error(`${field}.transitions timestamps must follow lifecycle order`);
    }
  }
  return { repository, prNumber, headSha, findings: findingIds.size };
}

export function parseLedgerBytes(bytes, field = 'ledger') {
  if (!Buffer.isBuffer(bytes)) bytes = Buffer.from(bytes);
  if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    throw new Error(`${field} must be UTF-8 without a BOM`);
  }
  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    throw new Error(`${field} must be valid UTF-8`);
  }
  try {
    return JSON.parse(text);
  } catch {
    throw new Error(`${field} must be valid JSON`);
  }
}

export function ledgerEvidence(bytes) {
  return {
    sha256: createHash('sha256').update(bytes).digest('hex'),
    bytes: bytes.length,
  };
}

export function admissionLedgerKey(repository, prNumber) {
  return `admission/findings/${canonicalRepository(repository)}/${canonicalPrNumber(prNumber)}.json`;
}

function contained(root, candidate) {
  const path = relative(root, candidate);
  return path === '' || (!path.startsWith(`..${sep}`) && path !== '..' && !isAbsolute(path));
}

async function safeDirectory(root, target, create) {
  const absoluteRoot = resolve(root);
  const absoluteTarget = resolve(target);
  if (!contained(absoluteRoot, absoluteTarget)) throw new Error('ledger path escapes the canonical external state directory');
  const rootStat = await lstat(absoluteRoot);
  if (rootStat.isSymbolicLink() || !rootStat.isDirectory()) {
    throw new Error('canonical external state directory must be a real directory');
  }
  const physicalRoot = await realpath(absoluteRoot);
  let current = absoluteRoot;
  const segments = relative(absoluteRoot, absoluteTarget).split(sep).filter(Boolean);
  for (const segment of segments) {
    current = join(current, segment);
    if (create) {
      try {
        await mkdir(current, { mode: 0o700 });
      } catch (error) {
        if (error?.code !== 'EEXIST') throw error;
      }
    }
    const stat = await lstat(current);
    if (stat.isSymbolicLink() || !stat.isDirectory()) throw new Error('ledger path contains a symlink, junction, or non-directory component');
    if (!contained(physicalRoot, await realpath(current))) throw new Error('ledger path escapes the canonical external state directory');
  }
  return absoluteTarget;
}

export async function resolveLedgerPath(stateDirectory, repository, prNumber, { createParent = false } = {}) {
  const key = admissionLedgerKey(repository, prNumber);
  const destination = resolve(stateDirectory, ...key.split('/'));
  await safeDirectory(stateDirectory, dirname(destination), createParent);
  return { key, destination };
}

export async function readAdmissionLedger(stateDirectory, repository, prNumber) {
  const { key, destination } = await resolveLedgerPath(stateDirectory, repository, prNumber);
  const stat = await lstat(destination);
  if (stat.isSymbolicLink() || !stat.isFile()) throw new Error('admission ledger must be a regular file');
  const flags = constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0);
  const handle = await open(destination, flags);
  let bytes;
  try {
    const opened = await handle.stat();
    if (!opened.isFile()) throw new Error('admission ledger must be a regular file');
    bytes = await handle.readFile();
  } finally {
    await handle.close();
  }
  const ledger = parseLedgerBytes(bytes);
  return { key, destination, bytes, ledger };
}

export async function resolveDeclaredExternalStateDirectory({
  cwd = process.cwd(),
  resolveSquadDirectory,
  readConfig,
  resolveDirectory,
} = {}) {
  const sdk = await import('@bradygaster/squad-sdk');
  const squadDirectory = (resolveSquadDirectory ?? sdk.resolveSquad)(cwd);
  if (!squadDirectory) throw new Error('unable to resolve static Squad config from the current worktree');
  const configBytes = readConfig
    ? await readConfig(join(squadDirectory, 'config.json'))
    : await readFile(join(squadDirectory, 'config.json'));
  const config = parseLedgerBytes(Buffer.isBuffer(configBytes) ? configBytes : Buffer.from(configBytes), 'Squad config');
  if (config?.stateLocation !== 'external') throw new Error('Squad config must declare external state');
  const projectKey = requiredString(config.projectKey, 'Squad project key');
  return (resolveDirectory ?? sdk.resolveExternalStateDir)(projectKey, false);
}
