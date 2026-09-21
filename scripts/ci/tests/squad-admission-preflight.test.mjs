import assert from 'node:assert/strict';
import test from 'node:test';
import { KIND, validateAdmissionPreflight } from '../squad-admission-preflight.mjs';

const SHA = 'a'.repeat(40);
const expected = { repository: 'sabbour/agentweaver', prNumber: 1489, headSha: SHA };
const event = (state, extra = {}) => ({ state, actor: 'ralph', at: '2026-09-21T00:00:00Z', headSha: SHA, evidence: state, ...extra });
const ledger = (findings = []) => ({ kind: KIND, ...expected, coordinatorOwner: 'ralph', reviewerSources: [{ id: 'code', reviewer: 'smith', headSha: SHA, evidence: 'PR comment' }], findings });

test('admits empty and fully resolved local coordinator ledgers', () => {
  assert.equal(validateAdmissionPreflight(ledger(), expected).admitted, true);
  const finding = { id: 'F-1', policy: 'required', transitions: [
    event('recorded'), event('owned', { owner: 'tank', action: 'fix validator' }),
    event('corrected'), event('revalidated', { validation: 'node --test focused suite' }), event('resolved'),
  ] };
  assert.equal(validateAdmissionPreflight(ledger([finding]), expected).findings, 1);
});

test('blocks stale, omitted ownership, skipped resolution, and duplicate findings', () => {
  const finding = { id: 'F-1', policy: 'required', transitions: [event('recorded'), event('owned', { owner: 'tank', action: 'fix' }), event('corrected'), event('revalidated', { validation: 'test' }), event('resolved')] };
  finding.transitions[3].headSha = 'b'.repeat(40);
  assert.throws(() => validateAdmissionPreflight(ledger([finding]), expected), /stale/u);
  const missingOwner = structuredClone(finding);
  missingOwner.transitions[3].headSha = SHA;
  delete missingOwner.transitions[1].owner;
  assert.throws(() => validateAdmissionPreflight(ledger([missingOwner]), expected), /owner/u);
  assert.throws(() => validateAdmissionPreflight(ledger([{ ...missingOwner, id: 'F-2', transitions: missingOwner.transitions.slice(0, 4) }]), expected), /incomplete/u);
  const duplicate = { id: 'F-1', policy: 'required', transitions: [event('recorded'), event('owned', { owner: 'tank', action: 'fix' }), event('corrected'), event('revalidated', { validation: 'test' }), event('resolved')] };
  assert.throws(() => validateAdmissionPreflight(ledger([duplicate, { ...duplicate }]), expected), /duplicate/u);
});
