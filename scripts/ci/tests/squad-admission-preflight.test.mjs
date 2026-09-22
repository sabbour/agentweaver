import assert from 'node:assert/strict';
import test from 'node:test';
import { KIND, resolveDeclaredExternalStateDirectory, runAdmissionPreflight, validateAdmissionPreflight } from '../squad-admission-preflight.mjs';
import { REVIEW_KIND, VALIDATION_KIND } from '../squad-admission-ledger.mjs';

const expected = { repository: 'sabbour/agentweaver', prNumber: 1502 };
const worktree = 'C:\\Users\\agent\\src\\agentweaver\\.worktrees\\issue-1502';
const branch = 'squad/1502-evidence-admission';
const headSha = 'a'.repeat(40);
const target = { type: 'worktree', worktree, branch, headSha };
const review = (source, extra = {}) => ({
  kind: REVIEW_KIND,
  phase: 'implementation',
  source,
  reviewer: `${source}-reviewer`,
  verdict: 'approved',
  target,
  findings: [],
  ...extra,
});
const validation = {
  kind: VALIDATION_KIND,
  argv: ['node', '--test', 'focused.test.mjs'],
  cwd: worktree,
  worktree,
  branch,
  headSha,
  startedAt: '2026-09-22T18:00:00.000Z',
  completedAt: '2026-09-22T18:00:01.000Z',
  exitCode: 0,
  signal: null,
  result: 'passed',
  stdout: '',
  stderr: '',
  after: { cwd: worktree, worktree, branch, headSha },
};
const ledger = (extra = {}) => ({
  kind: KIND,
  ...expected,
  headSha,
  worktree,
  branch,
  requiredReviewSources: ['code-review', 'security-review', 'ponytail-review'],
  reviews: [review('code-review'), review('security-review'), review('ponytail-review')],
  validations: [validation],
  materializedAt: '2026-09-22T18:00:02.000Z',
  ...extra,
});

test('admits a complete v2 exact-head ledger', () => {
  assert.deepEqual(validateAdmissionPreflight(ledger(), { ...expected, headSha }), {
    admitted: true,
    headSha,
    findings: 0,
  });
});

test('blocks missing, v1, incomplete, and stale ledgers', async () => {
  await assert.rejects(
    () => runAdmissionPreflight(expected.repository, expected.prNumber, {
      stateDirectory: 'C:\\state',
      headSha,
      readLedger: async () => { throw Object.assign(new Error('missing'), { code: 'ENOENT' }); },
    }),
    /missing/u,
  );
  assert.throws(() => validateAdmissionPreflight({ ...ledger(), kind: 'agentweaver.squad-admission-findings/v1' }, { ...expected, headSha }), /v2/u);
  assert.throws(() => validateAdmissionPreflight({ ...ledger(), validations: [] }, { ...expected, headSha }), /validations/u);
  assert.throws(() => validateAdmissionPreflight(ledger(), { ...expected, headSha: 'b'.repeat(40) }), /stale/u);
});

test('blocks provenance mismatches and unresolved required findings', () => {
  assert.throws(() => validateAdmissionPreflight(ledger({
    validations: [{ ...validation, cwd: 'C:\\shared\\agentweaver' }],
  }), { ...expected, headSha }), /cwd does not match/u);

  const finding = { id: 'F-1', policy: 'required', summary: 'Exact-head review failed.' };
  assert.throws(() => validateAdmissionPreflight(ledger({
    reviews: [
      review('code-review', { verdict: 'rejected', findings: [finding] }),
      review('security-review'),
      review('ponytail-review'),
    ],
  }), { ...expected, headSha }), /unresolved/u);
});

test('preserves advisory and explicit waiver behavior', () => {
  const reviews = [
    review('code-review', {
      verdict: 'rejected',
      findings: [
        { id: 'F-ADV', policy: 'advisory', summary: 'Optional cleanup.' },
        { id: 'F-WAIVE', policy: 'required', summary: 'Accepted operational risk.', waiver: { actor: 'sabbour', rationale: 'Bounded and accepted.' } },
      ],
    }),
    review('security-review'),
    review('ponytail-review'),
  ];
  assert.equal(validateAdmissionPreflight(ledger({ reviews }), { ...expected, headSha }).admitted, true);
});

test('reads only the declared external state directory', async () => {
  const result = await runAdmissionPreflight(expected.repository, expected.prNumber, {
    stateDirectory: 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver',
    headSha,
    readLedger: async (stateDirectory) => {
      assert.equal(stateDirectory, 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver');
      return ledger();
    },
  });
  assert.equal(result.headSha, headSha);
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
