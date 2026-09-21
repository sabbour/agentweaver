import assert from 'node:assert/strict';
import test from 'node:test';
import { KIND, assertAuthoritativeState, runAdmissionPreflight, validateAdmissionPreflight } from '../squad-admission-preflight.mjs';

const SHA = 'a'.repeat(40);
const expected = { repository: 'sabbour/agentweaver', prNumber: 1489, headSha: SHA };
const event = (state, extra = {}) => ({ state, actor: 'ralph', at: '2026-09-21T00:00:00Z', headSha: SHA, evidence: state, ...extra });
const ledger = (findings = []) => ({ kind: KIND, ...expected, findings });
const trustedStatus = 'Squad Status\n\n  Active squad: external\n  Path:         C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver\n';

test('admits empty, advisory, and fully resolved authoritative ledgers', () => {
  assert.equal(validateAdmissionPreflight(ledger(), expected).admitted, true);
  assert.equal(validateAdmissionPreflight(ledger([{ id: 'F-0', policy: 'advisory' }]), expected).findings, 1);
  const finding = { id: 'F-1', policy: 'required', transitions: [
    event('recorded'), event('owned', { owner: 'neo', action: 'fix validator' }),
    event('corrected'), event('revalidated', { validation: 'node --test focused suite' }), event('resolved'),
  ] };
  assert.equal(validateAdmissionPreflight(ledger([finding]), expected).findings, 1);
});

test('blocks unknown policies and incomplete required findings', () => {
  assert.throws(() => validateAdmissionPreflight(ledger([{ id: 'F-1' }]), expected), /policy/u);
  assert.throws(() => validateAdmissionPreflight(ledger([{ id: 'F-1', policy: 'blocking' }]), expected), /policy/u);
  assert.throws(() => validateAdmissionPreflight(ledger([{ id: 'F-1', policy: 'required', transitions: [] }]), expected), /incomplete/u);
});

test('accepts only externally resolved official Squad state', () => {
  assert.equal(assertAuthoritativeState(trustedStatus, 'C:\\repo', 'C:\\Users\\agent\\AppData\\Roaming'), 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver');
  assert.throws(() => assertAuthoritativeState('  Active squad: repo\n  Path:         C:\\repo\\.squad\n', 'C:\\repo', 'C:\\Users\\agent\\AppData\\Roaming'), /cannot prove/u);
  assert.throws(() => assertAuthoritativeState('  Active squad: external\n  Path:         C:\\repo\\.squad\n', 'C:\\repo', 'C:\\Users\\agent\\AppData\\Roaming'), /repository-controlled/u);
});

test('resolves the live PR head immediately before validating the external ledger', async () => {
  const calls = [];
  const result = await runAdmissionPreflight('sabbour/agentweaver', 1489, {
    repositoryRoot: 'C:\\repo',
    appData: 'C:\\Users\\agent\\AppData\\Roaming',
    command: async (file, args) => {
      calls.push([file, args]);
      if (file === 'npx') return { stdout: trustedStatus };
      return { stdout: JSON.stringify({ headRefOid: SHA }) };
    },
    openBridge: () => ({ read: async () => { calls.push(['bridge', []]); return JSON.stringify(ledger()); }, close() {} }),
  });
  assert.equal(result.headSha, SHA);
  assert.deepEqual(calls[2], ['gh', ['pr', 'view', '1489', '--repo', 'sabbour/agentweaver', '--json', 'headRefOid']]);
  await assert.rejects(() => runAdmissionPreflight('sabbour/agentweaver', 1489, {
    repositoryRoot: 'C:\\repo', appData: 'C:\\Users\\agent\\AppData\\Roaming',
    command: async (file) => file === 'npx' ? { stdout: trustedStatus } : { stdout: JSON.stringify({ headRefOid: 'b'.repeat(40) }) },
    openBridge: () => ({ read: async () => JSON.stringify(ledger()), close() {} }),
  }), /stale/u);
});
