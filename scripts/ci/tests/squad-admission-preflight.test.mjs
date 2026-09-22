import assert from 'node:assert/strict';
import test from 'node:test';
import { execFile } from 'node:child_process';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { KIND, runAdmissionPreflight, validateAdmissionPreflight } from '../squad-admission-preflight.mjs';
import { resolveAdmissionAuthority } from '../squad-admission-authority.mjs';
import { REVIEW_KIND, VALIDATION_KIND } from '../squad-admission-ledger.mjs';

const execFileAsync = promisify(execFile);
const expected = { repository: 'sabbour/agentweaver', prNumber: 1502 };
const worktree = 'C:\\Users\\agent\\src\\agentweaver\\.worktrees\\issue-1502';
const branch = 'squad/1502-evidence-admission';
const headSha = 'a'.repeat(40);
const baseSha = 'b'.repeat(40);
const trustedRuntime = {
  kind: 'agentweaver.squad-admission-runtime/v2',
  source: { ref: 'refs/remotes/origin/dev', commit: 'c'.repeat(40) },
  files: Object.fromEntries([
    'scripts/ci/squad-admission-launcher.mjs',
    'scripts/ci/squad-admission-authority.mjs',
    'scripts/ci/squad-admission-ledger.mjs',
    'scripts/ci/squad-admission-preflight.mjs',
  ].map((path, index) => [path, {
    objectId: String(index + 1).repeat(40),
    digest: `sha256:${String.fromCharCode(97 + index).repeat(64)}`,
  }])),
  aggregateDigest: `sha256:${'f'.repeat(64)}`,
};
const target = { type: 'worktree', worktree, branch, headSha };
const reviewers = {
  'code-review': 'smith',
  'security-review': 'seraph',
  'ponytail-review': 'ponytail-reviewer',
};
const review = (source, extra = {}) => ({
  kind: REVIEW_KIND,
  phase: 'implementation',
  source,
  reviewer: reviewers[source],
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
  baseSha,
  trustedRuntime,
  requiredReviewSources: ['code-review', 'security-review', 'ponytail-review'],
  reviews: [review('code-review'), review('security-review'), review('ponytail-review')],
  validations: [validation],
  materializedAt: '2026-09-22T18:00:02.000Z',
  ...extra,
});
const authority = (teamRoot, stateBackend = 'local') => ({
  teamRoot,
  stateBackend,
});
const fullPolicyRun = async () => ({
  exitCode: 0,
  stdout: 'M\0scripts/ci/squad-admission-preflight.mjs\0',
  stderr: '',
});

const admissionExpected = (extra = {}) => ({
  ...expected,
  headSha,
  baseSha,
  trustedRuntime,
  ...extra,
});

test('admits a complete v3 exact-head ledger', () => {
  assert.deepEqual(validateAdmissionPreflight(ledger(), admissionExpected()), {
    admitted: true,
    headSha,
    findings: 0,
  });
});

test('blocks missing, v1, incomplete, and stale ledgers', async () => {
  await assert.rejects(
    () => runAdmissionPreflight(expected.repository, expected.prNumber, {
      stateDirectory: 'C:\\state',
      authority: authority('C:\\state'),
      headSha,
      baseSha,
      trustedRuntime,
      policyRun: fullPolicyRun,
      readLedger: async () => { throw Object.assign(new Error('missing'), { code: 'ENOENT' }); },
    }),
    /missing/u,
  );
  assert.throws(() => validateAdmissionPreflight({ ...ledger(), kind: 'agentweaver.squad-admission-findings/v2' }, admissionExpected()), /v3/u);
  assert.throws(() => validateAdmissionPreflight({ ...ledger(), validations: [] }, admissionExpected()), /validations/u);
  assert.throws(() => validateAdmissionPreflight(ledger(), admissionExpected({ headSha: 'f'.repeat(40) })), /stale/u);
});

test('blocks provenance mismatches and unresolved required findings', () => {
  assert.throws(() => validateAdmissionPreflight(ledger({
    validations: [{ ...validation, cwd: 'C:\\shared\\agentweaver' }],
  }), admissionExpected()), /cwd does not match/u);

  const finding = { id: 'F-1', policy: 'required', summary: 'Exact-head review failed.' };
  assert.throws(() => validateAdmissionPreflight(ledger({
    reviews: [
      review('code-review', { verdict: 'rejected', findings: [finding] }),
      review('security-review'),
      review('ponytail-review'),
    ],
  }), admissionExpected()), /missing required exact-head approval/u);
});

test('blocks ledgers recorded under a different trusted runtime or base', () => {
  assert.throws(() => validateAdmissionPreflight(ledger({
    trustedRuntime: {
      ...trustedRuntime,
      aggregateDigest: `sha256:${'a'.repeat(64)}`,
    },
  }), admissionExpected()), /trusted runtime identity/u);
  assert.throws(() => validateAdmissionPreflight(ledger({
    baseSha: 'f'.repeat(40),
  }), admissionExpected()), /trusted base SHA/u);
});

test('rejects a rejected review that omits its findings', () => {
  assert.throws(() => validateAdmissionPreflight(ledger({
    reviews: [
      review('code-review', { verdict: 'rejected' }),
      review('security-review'),
      review('ponytail-review'),
    ],
  }), admissionExpected()), /must identify why the review was rejected/u);
});

test('blocks unresolved required findings even when a review says approved', () => {
  assert.throws(() => validateAdmissionPreflight(ledger({
    reviews: [
      review('code-review', { findings: [{ id: 'F-APPROVED', policy: 'required', summary: 'Still unresolved.' }] }),
      review('security-review'),
      review('ponytail-review'),
    ],
  }), admissionExpected()), /unresolved/u);
});

test('admits an older rejection only after corrective approval at the final head', () => {
  const finding = { id: 'F-1', policy: 'required', summary: 'Exact-head review failed.' };
  const reviews = [
    review('code-review', {
      verdict: 'rejected',
      target: { ...target, headSha: 'b'.repeat(40) },
      findings: [finding],
    }),
    review('code-review', { correctiveOf: 'F-1', findings: [finding] }),
    review('security-review'),
    review('ponytail-review'),
  ];
  assert.equal(validateAdmissionPreflight(ledger({ reviews }), admissionExpected()).admitted, true);
});

test('preserves advisory and explicit waiver behavior', () => {
  const advisory = { id: 'F-ADV', policy: 'advisory', summary: 'Optional cleanup.' };
  const waived = { id: 'F-WAIVE', policy: 'required', summary: 'Accepted operational risk.', waiver: { actor: 'sabbour', rationale: 'Bounded and accepted.' } };
  const reviews = [
    review('code-review', {
      verdict: 'rejected',
      target: { ...target, headSha: 'b'.repeat(40) },
      findings: [advisory, waived],
    }),
    review('code-review', { correctiveOf: 'F-WAIVE', findings: [waived] }),
    review('security-review'),
    review('ponytail-review'),
  ];
  assert.equal(validateAdmissionPreflight(ledger({ reviews }), admissionExpected()).admitted, true);
});

test('reads only the declared external state directory', async () => {
  const result = await runAdmissionPreflight(expected.repository, expected.prNumber, {
    stateDirectory: 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver',
    authority: authority('C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver'),
    headSha,
    baseSha,
    trustedRuntime,
    policyRun: fullPolicyRun,
    readLedger: async (configuredAuthority) => {
      assert.equal(configuredAuthority.teamRoot, 'C:\\Users\\agent\\AppData\\Roaming\\squad\\projects\\agentweaver');
      return ledger();
    },
  });
  assert.equal(result.headSha, headSha);
});

test('rejects an unconfigured repository authority', async () => {
  const error = Object.assign(new Error('missing config'), { code: 'ENOENT' });
  await assert.rejects(() => resolveAdmissionAuthority({
    cwd: 'C:\\repo',
    env: { APPDATA: 'C:\\Users\\agent\\AppData\\Roaming' },
    platform: 'win32',
  }, {
    realpath: async (value) => value,
    run: async () => ({ exitCode: 0, stdout: 'C:\\repo\\.git\n', stderr: '' }),
    readFile: async (path) => {
      throw error;
    },
  }), /missing config/u);
});

test('candidate checkout preflight CLI cannot execute admission', async () => {
  const teamRoot = await mkdtemp(join(tmpdir(), 'agentweaver-preflight-'));
  const ledgerPath = join(teamRoot, 'admission', 'findings', 'sabbour', 'agentweaver', '1502.json');
  await mkdir(dirname(ledgerPath), { recursive: true });
  await writeFile(ledgerPath, JSON.stringify(ledger()));
  const script = fileURLToPath(new URL('../squad-admission-preflight.mjs', import.meta.url));
  const cwd = fileURLToPath(new URL('../../../', import.meta.url));
  await assert.rejects(() => execFileAsync(process.execPath, [
      script,
      expected.repository,
      String(expected.prNumber),
      '--head-sha',
      headSha,
      '--team-root',
      teamRoot,
      '--state-backend',
      'local',
  ], { cwd }),
  /candidate checkout admission code is evidence only/u);
});
