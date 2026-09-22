import assert from 'node:assert/strict';
import test from 'node:test';
import { mkdtemp, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import {
  LEDGER_KIND,
  REVIEW_KIND,
  VALIDATION_KIND,
  materializeAdmissionLedger,
  materializeLedger,
  validateReviewOutput,
} from '../squad-admission-ledger.mjs';
import {
  assertAdmissionAuthority,
  requiredReviewSourcesForChanges,
  resolveAdmissionReviewPolicy,
} from '../squad-admission-authority.mjs';

const worktree = 'C:\\src\\agentweaver\\.worktrees\\issue-1502';
const branch = 'squad/1502-evidence-admission';
const headSha = 'a'.repeat(40);
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
const validation = (extra = {}) => ({
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
  ...extra,
});
const input = (extra = {}) => ({
  repository: 'sabbour/agentweaver',
  prNumber: 1502,
  worktree,
  branch,
  headSha,
  requiredReviewSources: ['code-review', 'security-review', 'ponytail-review'],
  reviews: [review('code-review'), review('security-review'), review('ponytail-review')],
  validations: [validation()],
  materializedAt: '2026-09-22T18:00:02.000Z',
  ...extra,
});
const authority = (teamRoot, stateBackend = 'local') => ({
  teamRoot,
  stateBackend,
  requiredReviewSources: ['code-review', 'security-review', 'ponytail-review'],
});

test('materializes only explicit structured review and validation evidence', () => {
  const ledger = materializeLedger(input());
  assert.equal(ledger.kind, LEDGER_KIND);
  assert.equal(ledger.reviews.length, 3);
  assert.equal(ledger.validations.length, 1);
  assert.equal('findings' in ledger, false);
});

test('rejects missing reviews and validation provenance mismatches', () => {
  assert.throws(() => materializeLedger(input({
    reviews: [review('code-review'), review('security-review')],
  })), /missing required exact-head approval from: ponytail-review/u);
  assert.throws(() => materializeLedger(input({
    validations: [validation({ headSha: 'b'.repeat(40) })],
  })), /candidate SHA/u);
  assert.throws(() => materializeLedger(input({
    reviews: [review('code-review', { target: { ...target, branch: 'dev' } }), review('security-review'), review('ponytail-review')],
  })), /candidate worktree lineage/u);
});

test('rejects a caller-declared reviewer policy that lowers configured requirements', () => {
  assert.throws(() => materializeLedger(input({
    requiredReviewSources: ['code-review'],
    reviews: [review('code-review')],
  })), /configured reviewer policy/u);
});

test('repository policy permits one focused review only for one low-risk document', () => {
  assert.deepEqual(requiredReviewSourcesForChanges(['docs/guide/validation.md']), ['code-review']);
  assert.deepEqual(requiredReviewSourcesForChanges(['CONTRIBUTING.md']), [
    'code-review',
    'security-review',
    'ponytail-review',
  ]);
  assert.equal(requiredReviewSourcesForChanges(['.github/skills/reviewer-protocol/SKILL.md']).length, 3);
  assert.equal(requiredReviewSourcesForChanges(['docs/guide/validation.md', 'README.md']).length, 3);
});

test('includes deleted and type-changed paths when resolving reviewer policy', async () => {
  const calls = [];
  const sources = await resolveAdmissionReviewPolicy({
    cwd: worktree,
    headSha,
  }, {
    run: async (argv) => {
      calls.push(argv);
      return { exitCode: 0, stdout: 'docs/guide/validation.md\nscripts/ci/deleted-control.mjs\n', stderr: '' };
    },
  });
  assert.equal(sources.length, 3);
  assert.deepEqual(calls[0], ['git', 'diff', '--name-only', 'origin/dev...aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa']);
});

test('requires distinct reviewers for each configured reviewer class', () => {
  assert.throws(() => materializeLedger(input({
    reviews: [
      review('code-review', { reviewer: 'same-reviewer' }),
      review('security-review', { reviewer: 'same-reviewer' }),
      review('ponytail-review'),
    ],
  }), {
    reviewerIdentities: {
      'code-review': ['same-reviewer'],
      'security-review': ['same-reviewer'],
      'ponytail-review': ['ponytail-reviewer'],
    },
  }), /independently issued/u);
});

test('rejects invented reviewer and waiver identities', () => {
  assert.throws(() => materializeLedger(input({
    reviews: [
      review('code-review', { reviewer: 'invented-reviewer' }),
      review('security-review'),
      review('ponytail-review'),
    ],
  })), /not authorized for required source: code-review/u);
  assert.throws(() => materializeLedger(input({
    reviews: [
      review('code-review', {
        findings: [{
          id: 'F-WAIVE',
          policy: 'required',
          summary: 'Caller-authored waiver.',
          waiver: { actor: 'invented-actor', rationale: 'Self-authorized.' },
        }],
      }),
      review('security-review'),
      review('ponytail-review'),
    ],
  })), /waiver actor invented-actor is not authorized/u);
});

test('design review targets its artifact and requires no implementation evidence', () => {
  assert.doesNotThrow(() => validateReviewOutput({
    kind: REVIEW_KIND,
    phase: 'design',
    source: 'code-review',
    reviewer: 'smith',
    verdict: 'approved',
    target: { type: 'artifact', artifact: 'designs/1502.md', digest: `sha256:${'b'.repeat(64)}` },
    findings: [],
  }));
  assert.throws(() => validateReviewOutput({
    ...review('code-review'),
    phase: 'design',
  }), /artifact for design review/u);
});

test('corrective re-review preserves finding ID, phase, source, and target type', () => {
  const finding = { id: 'F-1', policy: 'required', summary: 'Ledger is not materialized.' };
  const initial = review('code-review', {
    verdict: 'rejected',
    target: { ...target, headSha: 'b'.repeat(40) },
    findings: [finding],
  });
  const corrective = review('code-review', { correctiveOf: 'F-1', findings: [finding] });
  assert.doesNotThrow(() => materializeLedger(input({
    reviews: [initial, corrective, review('security-review'), review('ponytail-review')],
  })));
  assert.throws(() => materializeLedger(input({
    reviews: [
      initial,
      { ...corrective, source: 'security-review' },
      review('security-review'),
      review('ponytail-review'),
    ],
  })), /does not match its original phase, source, and target/u);
  assert.throws(() => materializeLedger(input({
    reviews: [
      initial,
      { ...corrective, target: { ...target, headSha: 'c'.repeat(40) } },
      review('security-review'),
      review('ponytail-review'),
    ],
  })), /approval does not match the candidate SHA/u);
});

test('writes atomically and validates by reading from the same backend', async () => {
  const teamRoot = await mkdtemp(join(tmpdir(), 'agentweaver-ledger-'));
  const result = await materializeAdmissionLedger(input({ worktree: teamRoot, reviews: [
    review('code-review', { target: { ...target, worktree: teamRoot } }),
    review('security-review', { target: { ...target, worktree: teamRoot } }),
    review('ponytail-review', { target: { ...target, worktree: teamRoot } }),
  ], validations: [validation({
    cwd: teamRoot,
    worktree: teamRoot,
    after: { cwd: teamRoot, worktree: teamRoot, branch, headSha },
  })] }), {
    authority: authority(teamRoot),
    teamRoot,
    stateBackend: 'local',
  });
  const persisted = JSON.parse(await readFile(join(teamRoot, result.key), 'utf8'));
  assert.equal(persisted.kind, LEDGER_KIND);
  assert.equal(result.stateBackend, 'local');
});

test('requires an explicit adapter for non-local state', async () => {
  const teamRoot = await mkdtemp(join(tmpdir(), 'agentweaver-non-local-'));
  await assert.rejects(() => materializeAdmissionLedger(input(), {
    authority: authority(teamRoot, 'two-layer'),
    teamRoot,
    stateBackend: 'two-layer',
  }), /explicit same-backend atomic adapter/u);
});

test('uses one injected runtime adapter for non-local write and read validation', async () => {
  const teamRoot = await mkdtemp(join(tmpdir(), 'agentweaver-non-local-'));
  const values = new Map();
  const stateAdapter = {
    async writeAtomic(key, value) { values.set(key, value); },
    async read(key) { return values.get(key); },
  };
  const result = await materializeAdmissionLedger(input(), {
    authority: authority(teamRoot, 'two-layer'),
    teamRoot,
    stateBackend: 'two-layer',
    stateAdapter,
  });
  assert.equal(JSON.parse(await stateAdapter.read(result.key)).kind, LEDGER_KIND);
});

test('rejects a materialization root that differs from configured authority', async () => {
  const configuredRoot = await mkdtemp(join(tmpdir(), 'agentweaver-authority-'));
  const alternateRoot = await mkdtemp(join(tmpdir(), 'agentweaver-forged-'));
  await assert.rejects(() => materializeAdmissionLedger(input(), {
    authority: authority(configuredRoot),
    teamRoot: alternateRoot,
    stateBackend: 'local',
  }), /does not match configured authority/u);
});

test('rejects caller backend and canonical-path substitutions', async () => {
  const teamRoot = await mkdtemp(join(tmpdir(), 'agentweaver-authority-'));
  await assert.rejects(() => materializeAdmissionLedger(input(), {
    authority: authority(teamRoot, 'two-layer'),
    teamRoot,
    stateBackend: 'local',
  }), /state backend does not match configured authority/u);
  await assert.rejects(() => assertAdmissionAuthority(
    authority(teamRoot),
    { teamRoot, stateBackend: 'local' },
    {
    realpath: async () => `${teamRoot}-substituted`,
    },
  ), /symlink, reparse point, or canonical-path substitution/u);
});

test('rejects conflicting corrective results for one finding', () => {
  const finding = { id: 'F-1', policy: 'required', summary: 'Needs one bounded correction.' };
  assert.throws(() => materializeLedger(input({
    reviews: [
      review('code-review', { verdict: 'rejected', target: { ...target, headSha: 'b'.repeat(40) }, findings: [finding] }),
      review('code-review', { correctiveOf: 'F-1', findings: [finding] }),
      review('code-review', { correctiveOf: 'F-1', verdict: 'rejected', findings: [finding] }),
      review('security-review'),
      review('ponytail-review'),
    ],
  })), /conflicting results/u);
});

test('rejects conflicting exact-head outcomes from one required source', () => {
  assert.throws(() => materializeLedger(input({
    reviews: [
      review('code-review'),
      review('code-review', {
        verdict: 'rejected',
        findings: [{ id: 'F-ADVISORY', policy: 'advisory', summary: 'Still rejected.' }],
      }),
      review('security-review'),
      review('ponytail-review'),
    ],
  })), /conflicting exact-head rejection/u);
});
