import assert from 'node:assert/strict';
import test from 'node:test';
import { cohortEntriesSha256, validateAdmissionLedger, validateCohort } from '../admission-findings-gate.mjs';

const SHA = 'a'.repeat(40);
const PR_AUTHOR = 'implementer';

function source() {
  return { id: 1, reviewId: 'code-review', reviewer: 'reviewer', headSha: SHA, findingIds: [], requirements: [], cohortEntriesSha256: cohort().entriesSha256 };
}

function cohort() {
  const entries = [{ prNumber: 1489, order: 1, headSha: SHA }];
  return { id: '2026-09-21-1', snapshotAt: '2026-09-21T00:00:00Z', entries, entriesSha256: cohortEntriesSha256(entries) };
}

function snapshot(overrides = {}) {
  return {
    repository: 'sabbour/agentweaver', prNumber: 1489, headSha: SHA, prAuthor: PR_AUTHOR,
    ledgerAuthor: 'admission-owner', requiredSourceIds: ['code-review'],
    authorizedReviewers: { 'code-review': ['reviewer'] }, authorizedAdmissionOwners: ['admission-owner'],
    authorizedWaiverApprovers: ['maintainer'], sources: [source()], correctivePrs: {}, ...overrides,
  };
}

function ledger(overrides = {}) {
  return {
    kind: 'agentweaver.admission-findings-ledger/v1', repository: 'sabbour/agentweaver',
    prNumber: 1489, headSha: SHA, admissionOwner: 'admission-owner',
    cohort: cohort(),
    reviewerSources: [{ id: 'code-review', reviewId: 'code-review', reviewer: 'reviewer', headSha: SHA, findingIds: [], evidence: 'review #1' }],
    findings: [], ...overrides,
  };
}

function requiredFinding(action = {}) {
  return {
    id: 'F-1', severity: 'medium', policy: 'required', summary: 'Must fix', sourceIds: ['code-review'],
    transitions: ['recorded', 'owned', 'waived', 'revalidated', 'resolved'].map((state) => ({
      state, actor: state === 'revalidated' ? 'independent-reviewer' : 'admission-owner',
      at: '2026-09-21T00:00:00Z', headSha: SHA, evidence: `${state} evidence`,
      ...(state === 'owned' ? { owner: 'tank', action: 'prepare waiver decision' } : {}),
      ...(state === 'waived' ? { rationale: 'Accepted risk', approvedBy: 'maintainer' } : {}),
      ...action,
    })),
  };
}

function withFinding(finding = requiredFinding()) {
  const review = source();
  review.findingIds = ['F-1'];
  review.requirements = [{ id: 'F-1', severity: 'medium', policy: 'required' }];
  return [ledger({ reviewerSources: [{ ...ledger().reviewerSources[0], findingIds: ['F-1'] }], findings: [finding] }), snapshot({ sources: [review] })];
}

test('accepts a valid empty ledger', () => {
  assert.deepEqual(validateAdmissionLedger(ledger(), snapshot()), { admitted: true, headSha: SHA, findings: 0 });
});

test('accepts a fully resolved, independently waived required finding', () => {
  const [candidate, evidence] = withFinding();
  assert.equal(validateAdmissionLedger(candidate, evidence).findings, 1);
});

test('1481 regression: earlier-SHA unactioned required medium finding blocks readiness and merge', () => {
  const [candidate, evidence] = withFinding();
  candidate.findings[0].transitions = [candidate.findings[0].transitions[0]];
  candidate.findings[0].transitions[0].headSha = 'b'.repeat(40);
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /stale|skipped/u);
});

test('requires immutable non-empty cohort entries bound to the candidate PR, SHA, and order', () => {
  assert.throws(() => validateAdmissionLedger(ledger({ cohort: { id: 'cohort', snapshotAt: 'now' } }), snapshot()), /cohort\.entries must be an array/u);
  const empty = { id: 'cohort', snapshotAt: 'now', entries: [], entriesSha256: cohortEntriesSha256([]) };
  assert.throws(() => validateAdmissionLedger(ledger({ cohort: empty }), snapshot()), /must not be empty/u);
  const invalid = cohort();
  invalid.entries[0].headSha = 'b'.repeat(40);
  assert.throws(() => validateAdmissionLedger(ledger({ cohort: invalid }), snapshot()), /entriesSha256|candidate PR/u);
  const missingCandidate = { ...cohort(), entries: [{ prNumber: 1490, order: 1, headSha: SHA }] };
  missingCandidate.entriesSha256 = cohortEntriesSha256(missingCandidate.entries);
  assert.throws(() => validateAdmissionLedger(ledger({ cohort: missingCandidate }), snapshot()), /candidate PR/u);
  const altered = cohort();
  altered.entries.push({ prNumber: 1490, order: 2, headSha: SHA });
  altered.entriesSha256 = cohortEntriesSha256(altered.entries);
  assert.throws(() => validateAdmissionLedger(ledger({ cohort: altered }), snapshot()), /does not bind immutable cohort entries/u);
});

test('rejects missing sources, omitted IDs, duplicate IDs, downgrade, and reordered transitions', () => {
  assert.throws(() => validateAdmissionLedger(ledger({ reviewerSources: [] }), snapshot()), /required reviewer source/u);
  let [candidate, evidence] = withFinding();
  candidate.findings[0].id = 'F-2';
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /omitted/u);
  [candidate, evidence] = withFinding();
  candidate.findings.push(structuredClone(candidate.findings[0]));
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /duplicate/u);
  [candidate, evidence] = withFinding();
  candidate.findings[0].severity = 'low';
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /downgraded/u);
  [candidate, evidence] = withFinding();
  [candidate.findings[0].transitions[1], candidate.findings[0].transitions[2]] = [candidate.findings[0].transitions[2], candidate.findings[0].transitions[1]];
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /must be owned/u);
  [candidate, evidence] = withFinding();
  delete candidate.findings[0].transitions[1].action;
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /action must be a non-empty string/u);
});

test('rejects self-approved waiver, non-independent revalidation, and unmerged corrective PR', () => {
  let [candidate, evidence] = withFinding();
  candidate.findings[0].transitions[2].approvedBy = PR_AUTHOR;
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /independent authorized/u);
  [candidate, evidence] = withFinding();
  candidate.findings[0].transitions[3].actor = 'admission-owner';
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /revalidation must be independent/u);
  [candidate, evidence] = withFinding();
  candidate.findings[0].transitions[2] = { ...candidate.findings[0].transitions[2], state: 'corrective-pr', correctivePr: 99 };
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /not merged and incorporated/u);
});

test('rejects unauthorized reviewer sources and ledger authors', () => {
  let [candidate, evidence] = withFinding();
  candidate.reviewerSources[0].reviewer = 'other-reviewer';
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /not authorized/u);
  [candidate, evidence] = withFinding();
  evidence.ledgerAuthor = 'untrusted-owner';
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /cannot author or own/u);
  [candidate, evidence] = withFinding();
  evidence.requiredSourceIds = [];
  assert.throws(() => validateAdmissionLedger(candidate, evidence), /configuration is missing/u);
});

test('accepts an incorporated merged corrective PR', () => {
  const [candidate, evidence] = withFinding();
  candidate.findings[0].transitions[2] = {
    ...candidate.findings[0].transitions[2],
    state: 'corrective-pr',
    correctivePr: 1490,
  };
  evidence.correctivePrs = { 1490: { merged: true, incorporated: true } };
  assert.equal(validateAdmissionLedger(candidate, evidence).admitted, true);
});

test('requires a complete terminal Ralph cohort', () => {
  const cohort = { id: 'cohort-1', snapshotAt: '2026-09-21T00:00:00Z', entries: [
    { prNumber: 1, order: 1, headSha: SHA, state: 'confirmed-merged', owner: 'ralph', action: 'squash merged', evidence: 'merge SHA', terminalAt: '2026-09-21T00:00:00Z' },
    { prNumber: 2, order: 2, headSha: SHA, state: 'owned-blocker', owner: 'tank', action: 'fix source', evidence: 'issue comment', terminalAt: '2026-09-21T00:00:00Z' },
  ] };
  assert.equal(validateCohort(cohort).entries, 2);
  cohort.entries[1].state = 'ready';
  assert.throws(() => validateCohort(cohort), /Confirmed Merged or Owned blocker/u);
});
