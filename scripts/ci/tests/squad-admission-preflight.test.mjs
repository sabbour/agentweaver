import assert from 'node:assert/strict';
import test from 'node:test';
import { KIND, resolveDeclaredExternalStateDirectory, runAdmissionPreflight, validateAdmissionPreflight } from '../squad-admission-preflight.mjs';

const expected = { repository: 'sabbour/agentweaver', prNumber: 1489 };
const event = (state, headSha, extra = {}) => ({ state, actor: 'ralph', at: '2026-09-21T00:00:00Z', headSha, evidence: state, ...extra });
const ledger = (headSha, findings = []) => ({ kind: KIND, ...expected, headSha, findings });

test('admits empty, advisory, and fully resolved authoritative ledgers', () => {
  const headSha = 'a'.repeat(40);
  assert.equal(validateAdmissionPreflight(ledger(headSha), { ...expected, headSha }).admitted, true);
  assert.equal(validateAdmissionPreflight(ledger(headSha, [{ id: 'F-0', policy: 'advisory' }]), { ...expected, headSha }).findings, 1);
  const finding = { id: 'F-1', policy: 'required', transitions: [
    event('recorded', headSha), event('owned', headSha, { owner: 'neo', action: 'fix validator' }),
    event('corrected', headSha), event('revalidated', headSha, { validation: 'node --test focused suite' }), event('resolved', headSha),
  ] };
  assert.equal(validateAdmissionPreflight(ledger(headSha, [finding]), { ...expected, headSha }).findings, 1);
});

test('blocks unknown policies and incomplete required findings', () => {
  const headSha = 'a'.repeat(40);
  assert.throws(() => validateAdmissionPreflight(ledger(headSha, [{ id: 'F-1' }]), { ...expected, headSha }), /policy/u);
  assert.throws(() => validateAdmissionPreflight(ledger(headSha, [{ id: 'F-1', policy: 'blocking' }]), { ...expected, headSha }), /policy/u);
  assert.throws(() => validateAdmissionPreflight(ledger(headSha, [{ id: 'F-1', policy: 'required', transitions: [] }]), { ...expected, headSha }), /incomplete/u);
});

test('blocks stale evidence in every required-finding lifecycle transition', () => {
  const headSha = 'a'.repeat(40);
  const staleHeadSha = 'b'.repeat(40);
  const transitions = (remediation = 'corrected') => [
    event('recorded', headSha),
    event('owned', headSha, { owner: 'neo', action: 'fix validator' }),
    event(remediation, headSha, remediation === 'waived' ? { rationale: 'accepted risk' } : {}),
    event('revalidated', headSha, { validation: 'node --test focused suite' }),
    event('resolved', headSha),
  ];

  for (const [index, state, remediation] of [
    [0, 'recorded'],
    [1, 'owned'],
    [2, 'corrected', 'corrected'],
    [2, 'waived', 'waived'],
    [3, 'revalidated'],
    [4, 'resolved'],
  ]) {
    const findingTransitions = transitions(remediation);
    findingTransitions[index] = { ...findingTransitions[index], headSha: staleHeadSha };
    assert.throws(
      () => validateAdmissionPreflight(ledger(headSha, [{ id: `F-${state}`, policy: 'required', transitions: findingTransitions }]), { ...expected, headSha }),
      new RegExp(`transitions\\[${index}\\]\\.headSha is stale`, 'u'),
      `${state} evidence must match the live PR head`,
    );
  }
});

test('reads the declared external state directory and rejects stale ledger evidence', async () => {
  const headSha = 'a'.repeat(40);
  const result = await runAdmissionPreflight('sabbour/agentweaver', 1489, {
    stateDirectory: 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver',
    headSha,
    readLedger: async (stateDirectory) => {
      assert.equal(stateDirectory, 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver');
      return ledger(headSha);
    },
  });
  assert.equal(result.headSha, headSha);
  await assert.rejects(() => runAdmissionPreflight('sabbour/agentweaver', 1489, {
    stateDirectory: 'C:\\candidate\\.squad',
    headSha,
    readLedger: async () => ledger('b'.repeat(40)),
  }), /stale/u);
});

test('uses the pinned Squad resolver for declared external state', async () => {
  let projectKey;
  const directory = await resolveDeclaredExternalStateDirectory({
    readConfig: async () => JSON.stringify({ stateLocation: 'external', projectKey: 'agentweaver' }),
    resolveDirectory: (key, create) => {
      projectKey = key;
      assert.equal(create, false);
      return 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver';
    },
  });
  assert.equal(projectKey, 'agentweaver');
  assert.equal(directory, 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver');
  await assert.rejects(() => resolveDeclaredExternalStateDirectory({
    readConfig: async () => JSON.stringify({ stateLocation: 'repository', projectKey: 'agentweaver' }),
  }), /declare external state/u);
});
