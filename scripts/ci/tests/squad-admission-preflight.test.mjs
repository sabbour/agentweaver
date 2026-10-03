import assert from 'node:assert/strict';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import {
  KIND,
  resolveDeclaredExternalStateDirectory,
  runAdmissionPreflight,
  validateAdmissionPreflight,
} from '../squad-admission-preflight.mjs';

const expected = { repository: 'sabbour/agentweaver', prNumber: 1508 };
const event = (state, headSha, extra = {}) => ({
  state,
  actor: 'ralph',
  at: '2026-09-22T22:00:00Z',
  headSha,
  evidence: `${state} evidence`,
  ...extra,
});
const ledger = (headSha, findings = []) => ({ kind: KIND, ...expected, headSha, findings });
const requiredFinding = (headSha, remediation = 'corrected') => ({
  id: 'F-1',
  policy: 'required',
  transitions: [
    event('recorded', headSha),
    event('owned', headSha, { owner: 'link', action: 'fix validator' }),
    event(remediation, headSha, remediation === 'waived' ? { rationale: 'accepted by owner' } : {}),
    event('revalidated', headSha, { validation: 'node --test scripts/ci/tests/*.test.mjs' }),
    event('resolved', headSha),
  ],
});

test('validates exact advisory and required finding shapes', () => {
  const headSha = 'a'.repeat(40);
  assert.equal(validateAdmissionPreflight(ledger(headSha), { ...expected, headSha }).findings, 0);
  assert.equal(validateAdmissionPreflight(ledger(headSha, [{ id: 'A-1', policy: 'advisory' }]), { ...expected, headSha }).findings, 1);
  assert.equal(validateAdmissionPreflight(ledger(headSha, [requiredFinding(headSha)]), { ...expected, headSha }).findings, 1);
  assert.equal(validateAdmissionPreflight(ledger(headSha, [requiredFinding(headSha, 'waived')]), { ...expected, headSha }).findings, 1);
});

test('rejects unknown properties, malformed timestamps, and stale lifecycle evidence', () => {
  const headSha = 'a'.repeat(40);
  assert.throws(
    () => validateAdmissionPreflight({ ...ledger(headSha), extra: true }, { ...expected, headSha }),
    /contain exactly/u,
  );
  assert.throws(
    () => validateAdmissionPreflight(ledger(headSha, [{ id: 'A-1', policy: 'advisory', summary: 'not in v1' }]), { ...expected, headSha }),
    /contain exactly/u,
  );
  const badTime = requiredFinding(headSha);
  badTime.transitions[0].at = 'tomorrow';
  assert.throws(() => validateAdmissionPreflight(ledger(headSha, [badTime]), { ...expected, headSha }), /timestamp/u);
  const reversed = requiredFinding(headSha);
  reversed.transitions[1].at = '2026-09-22T21:59:59Z';
  assert.throws(() => validateAdmissionPreflight(ledger(headSha, [reversed]), { ...expected, headSha }), /lifecycle order/u);
  const stale = requiredFinding(headSha);
  stale.transitions[3].headSha = 'b'.repeat(40);
  assert.throws(() => validateAdmissionPreflight(ledger(headSha, [stale]), { ...expected, headSha }), /stale/u);
});

test('rejects noncanonical repositories, PR numbers, SHAs, and context mismatches', () => {
  const headSha = 'a'.repeat(40);
  for (const repository of [
    'Sabbour/agentweaver',
    'sabbour\\agentweaver',
    'sabbour／agentweaver',
    'sabbour/../agentweaver',
    'sabbour/con',
    'sabbour/repo.',
    'sabbour/répô',
  ]) {
    assert.throws(() => validateAdmissionPreflight({ ...ledger(headSha), repository }, { ...expected, headSha }), /repository/u);
  }
  assert.throws(() => validateAdmissionPreflight({ ...ledger(headSha), prNumber: 0 }, { ...expected, headSha }), /PR number|prNumber/u);
  assert.throws(() => validateAdmissionPreflight({ ...ledger(headSha), headSha: 'A'.repeat(40) }, { ...expected, headSha }), /lowercase/u);
  assert.throws(() => validateAdmissionPreflight(ledger(headSha), { ...expected, repository: 'sabbour/other', headSha }), /repository does not match/u);
  assert.throws(() => validateAdmissionPreflight(ledger(headSha), { ...expected, prNumber: 1509, headSha }), /PR number does not match/u);
});

test('resolves worktree-aware static config through the pinned SDK seams', async () => {
  const calls = [];
  const directory = await resolveDeclaredExternalStateDirectory({
    cwd: 'C:\\repo\\.worktrees\\issue-1508',
    resolveSquadDirectory: (cwd) => {
      calls.push(['resolveSquad', cwd]);
      return 'C:\\repo\\.squad';
    },
    readConfig: async (path) => {
      calls.push(['readConfig', path]);
      return JSON.stringify({ stateLocation: 'external', projectKey: 'agentweaver' });
    },
    resolveDirectory: (key, create) => {
      calls.push(['resolveExternalStateDir', key, create]);
      return 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver';
    },
  });
  assert.equal(directory, 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver');
  assert.deepEqual(calls, [
    ['resolveSquad', 'C:\\repo\\.worktrees\\issue-1508'],
    ['readConfig', 'C:\\repo\\.squad\\config.json'],
    ['resolveExternalStateDir', 'agentweaver', false],
  ]);
  await assert.rejects(() => resolveDeclaredExternalStateDirectory({
    resolveSquadDirectory: () => 'C:\\repo\\.squad',
    readConfig: async () => JSON.stringify({ stateLocation: 'repository', projectKey: 'agentweaver' }),
  }), /declare external state/u);
});

test('preflight reads canonical persisted bytes and emits only redacted evidence', async () => {
  const root = await mkdtemp(join(tmpdir(), 'agentweaver-preflight-'));
  const headSha = 'a'.repeat(40);
  const directory = join(root, 'admission', 'findings', 'sabbour', 'agentweaver');
  await mkdir(directory, { recursive: true });
  const bytes = Buffer.from(`${JSON.stringify(ledger(headSha))}\n`);
  await writeFile(join(directory, '1508.json'), bytes);
  const result = await runAdmissionPreflight(expected.repository, '1508', { stateDirectory: root, headSha });
  assert.equal(result.admitted, true);
  assert.equal(result.bytes, bytes.length);
  assert.match(result.sha256, /^[0-9a-f]{64}$/u);
  assert.equal(result.ledgerKey, 'admission/findings/sabbour/agentweaver/1508.json');
  assert.equal('stateDirectory' in result, false);
  assert.equal(JSON.stringify(result).includes(root), false);
});
