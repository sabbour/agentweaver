#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export const LEDGER_KIND = 'agentweaver.admission-findings-ledger/v1';
export const SOURCE_KIND = 'agentweaver.findings-source/v1';
const SHA = /^[0-9a-f]{40}$/iu;
const TERMINAL_COHORT_STATES = new Set(['confirmed-merged', 'owned-blocker']);

function fail(message) {
  throw new Error(message);
}

function string(value, field) {
  if (typeof value !== 'string' || value.trim() === '') fail(`${field} must be a non-empty string`);
  return value.trim();
}

function sha(value, field) {
  value = string(value, field);
  if (!SHA.test(value)) fail(`${field} must be a 40-character SHA`);
  return value.toLowerCase();
}

function array(value, field) {
  if (!Array.isArray(value)) fail(`${field} must be an array`);
  return value;
}

function object(value, field) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail(`${field} must be an object`);
  return value;
}

function same(left, right) {
  return left.localeCompare(right, undefined, { sensitivity: 'accent' }) === 0;
}

function unique(items, label) {
  const seen = new Set();
  for (const item of items) {
    if (seen.has(item)) fail(`duplicate ${label}: ${item}`);
    seen.add(item);
  }
  return seen;
}

function sourceReviewerPolicy(value) {
  const policy = {};
  for (const entry of value.split(';').filter(Boolean)) {
    const [sourceId, reviewers] = entry.split('=');
    if (!sourceId || !reviewers) fail('ADMISSION_FINDINGS_AUTHORIZED_REVIEWERS must use source=reviewer[,reviewer]');
    policy[sourceId] = reviewers.split(',').filter(Boolean);
  }
  return policy;
}

function transition(transitionEntry, expected, prefix, expectedHead) {
  const entry = object(transitionEntry, prefix);
  if (entry.state !== expected) fail(`${prefix}.state must be ${expected}`);
  string(entry.actor, `${prefix}.actor`);
  string(entry.at, `${prefix}.at`);
  if (sha(entry.headSha, `${prefix}.headSha`) !== expectedHead) fail(`${prefix}.headSha is stale`);
  string(entry.evidence, `${prefix}.evidence`);
  return entry;
}

export function validateCohort(cohort) {
  cohort = object(cohort, 'cohort');
  string(cohort.id, 'cohort.id');
  string(cohort.snapshotAt, 'cohort.snapshotAt');
  const entries = array(cohort.entries, 'cohort.entries');
  const numbers = [];
  for (const [index, entry] of entries.entries()) {
    const prefix = `cohort.entries[${index}]`;
    object(entry, prefix);
    if (!Number.isInteger(entry.prNumber) || entry.prNumber < 1) fail(`${prefix}.prNumber must be positive`);
    if (!Number.isInteger(entry.order) || entry.order < 1) fail(`${prefix}.order must be positive`);
    if (!TERMINAL_COHORT_STATES.has(entry.state)) fail(`${prefix}.state must be Confirmed Merged or Owned blocker`);
    string(entry.owner, `${prefix}.owner`);
    string(entry.action, `${prefix}.action`);
    string(entry.evidence, `${prefix}.evidence`);
    string(entry.terminalAt, `${prefix}.terminalAt`);
    numbers.push(entry.prNumber);
  }
  unique(numbers, 'cohort PR number');
  const orders = entries.map((entry) => entry.order).sort((a, b) => a - b);
  if (orders.some((order, index) => order !== index + 1)) fail('cohort entries must have contiguous ordered positions');
  return { cohortId: cohort.id, entries: entries.length };
}

export function validateAdmissionLedger(ledger, snapshot) {
  ledger = object(ledger, 'ledger');
  snapshot = object(snapshot, 'snapshot');
  if (ledger.kind !== LEDGER_KIND) fail(`kind must be ${LEDGER_KIND}`);
  const repository = string(ledger.repository, 'repository');
  if (repository !== string(snapshot.repository, 'snapshot.repository')) fail('ledger repository does not match PR repository');
  if (ledger.prNumber !== snapshot.prNumber) fail('ledger PR number does not match candidate PR');
  const headSha = sha(ledger.headSha, 'headSha');
  if (headSha !== sha(snapshot.headSha, 'snapshot.headSha')) fail('ledger evidence is stale for the candidate head SHA');
  const candidateAuthor = string(snapshot.prAuthor, 'snapshot.prAuthor');
  const ledgerAuthor = string(snapshot.ledgerAuthor, 'snapshot.ledgerAuthor');
  const admissionOwner = string(ledger.admissionOwner, 'admissionOwner');
  const admissionOwners = array(snapshot.authorizedAdmissionOwners, 'snapshot.authorizedAdmissionOwners');
  if (same(candidateAuthor, ledgerAuthor) || same(candidateAuthor, admissionOwner) ||
    !admissionOwners.some((actor) => same(actor, ledgerAuthor)) ||
    !admissionOwners.some((actor) => same(actor, admissionOwner))) {
    fail('candidate author cannot author or own its admission ledger');
  }
  if (Array.isArray(ledger.cohort?.entries)) validateCohort(ledger.cohort);
  else {
    object(ledger.cohort, 'cohort');
    string(ledger.cohort.id, 'cohort.id');
    string(ledger.cohort.snapshotAt, 'cohort.snapshotAt');
  }

  const requiredSourceIds = array(snapshot.requiredSourceIds, 'snapshot.requiredSourceIds');
  const sourceRecords = new Map(array(snapshot.sources, 'snapshot.sources').map((source) => [source.reviewId, source]));
  const authorizedReviewers = object(snapshot.authorizedReviewers, 'snapshot.authorizedReviewers');
  const sources = array(ledger.reviewerSources, 'reviewerSources');
  const sourceIds = [];
  const findingIdsFromSources = [];
  const requirements = new Map();
  for (const [index, inputSource] of sources.entries()) {
    const prefix = `reviewerSources[${index}]`;
    const source = object(inputSource, prefix);
    const id = string(source.id, `${prefix}.id`);
    sourceIds.push(id);
    if (!requiredSourceIds.includes(id)) fail(`${prefix}.id is not a required reviewer source`);
    const reviewer = string(source.reviewer, `${prefix}.reviewer`);
    if (same(reviewer, candidateAuthor)) fail(`${prefix}.reviewer must be independent from the candidate author`);
    const permittedReviewers = array(authorizedReviewers[id], `authorized reviewers for ${id}`);
    if (!permittedReviewers.some((actor) => same(actor, reviewer))) fail(`${prefix}.reviewer is not authorized for ${id}`);
    if (sha(source.headSha, `${prefix}.headSha`) !== headSha) fail(`${prefix}.headSha is stale`);
    const review = sourceRecords.get(source.reviewId);
    if (!review || review.reviewId !== source.reviewId || !same(review.reviewer, reviewer) || review.headSha.toLowerCase() !== headSha) {
      fail(`${prefix} does not match an authoritative current-head GitHub review`);
    }
    const findingIds = array(source.findingIds, `${prefix}.findingIds`).map((id, findingIndex) => string(id, `${prefix}.findingIds[${findingIndex}]`));
    unique(findingIds, `${prefix} finding id`);
    if (JSON.stringify(findingIds) !== JSON.stringify(review.findingIds)) fail(`${prefix}.findingIds do not match the reviewer source`);
    for (const requirement of array(review.requirements, `${prefix} source requirements`)) {
      const findingId = string(requirement.id, `${prefix} source requirement.id`);
      if (requirements.has(findingId)) fail(`duplicate source requirement for finding ${findingId}`);
      requirements.set(findingId, { severity: requirement.severity, policy: requirement.policy });
    }
    findingIdsFromSources.push(...findingIds);
  }
  unique(sourceIds, 'reviewer source id');
  const sourceIdSet = unique(sourceIds, 'reviewer source id');
  if (requiredSourceIds.some((id) => !sourceIdSet.has(id))) fail('required reviewer source is missing');

  const findings = array(ledger.findings, 'findings');
  const findingIds = [];
  for (const [index, inputFinding] of findings.entries()) {
    const prefix = `findings[${index}]`;
    const finding = object(inputFinding, prefix);
    const id = string(finding.id, `${prefix}.id`);
    findingIds.push(id);
    if (!['low', 'medium', 'high'].includes(finding.severity)) fail(`${prefix}.severity is invalid`);
    if (!['required', 'advisory'].includes(finding.policy)) fail(`${prefix}.policy is invalid`);
    string(finding.summary, `${prefix}.summary`);
    const findingSources = array(finding.sourceIds, `${prefix}.sourceIds`).map((sourceId, sourceIndex) => string(sourceId, `${prefix}.sourceIds[${sourceIndex}]`));
    if (findingSources.length === 0 || findingSources.some((sourceId) => !sourceIdSet.has(sourceId))) fail(`${prefix}.sourceIds must name known reviewer sources`);
    const requirement = requirements.get(id);
    if (!requirement) fail(`finding ${id} was omitted from reviewer source requirements`);
    if (finding.severity !== requirement.severity || finding.policy !== requirement.policy) fail(`finding ${id} severity or policy was downgraded`);
    const states = array(finding.transitions, `${prefix}.transitions`);
    const expected = finding.policy === 'required'
      ? ['recorded', 'owned', null, 'revalidated', 'resolved']
      : ['recorded'];
    if (states.length !== expected.length) fail(`${prefix}.transitions has skipped, extra, or missing states`);
    expected.forEach((state, transitionIndex) => {
      if (state) {
        const validated = transition(states[transitionIndex], state, `${prefix}.transitions[${transitionIndex}]`, headSha);
        if (state === 'owned') {
          string(validated.owner, `${prefix}.transitions[${transitionIndex}].owner`);
          string(validated.action, `${prefix}.transitions[${transitionIndex}].action`);
        }
        return;
      }
      const action = object(states[transitionIndex], `${prefix}.transitions[${transitionIndex}]`);
      if (!['corrective-pr', 'waived'].includes(action.state)) fail(`${prefix}.transitions[${transitionIndex}].state must be corrective-pr or waived`);
      string(action.actor, `${prefix}.transitions[${transitionIndex}].actor`);
      string(action.at, `${prefix}.transitions[${transitionIndex}].at`);
      if (sha(action.headSha, `${prefix}.transitions[${transitionIndex}].headSha`) !== headSha) fail(`${prefix}.transitions[${transitionIndex}].headSha is stale`);
      string(action.evidence, `${prefix}.transitions[${transitionIndex}].evidence`);
    });
    if (finding.policy === 'required') {
      const action = states[2];
      if (action.state === 'corrective-pr') {
        if (!Number.isInteger(action.correctivePr) || !snapshot.correctivePrs?.[action.correctivePr]?.merged || !snapshot.correctivePrs[action.correctivePr].incorporated) {
          fail(`finding ${id} corrective PR is not merged and incorporated into the candidate`);
        }
      } else {
        string(action.rationale, `${prefix}.transitions[2].rationale`);
        const approver = string(action.approvedBy, `${prefix}.transitions[2].approvedBy`);
        if (same(approver, candidateAuthor) || same(approver, ledgerAuthor)) fail(`finding ${id} waiver needs an independent authorized approver`);
        if (!array(snapshot.authorizedWaiverApprovers, 'snapshot.authorizedWaiverApprovers').some((actor) => same(actor, approver))) {
          fail(`finding ${id} waiver approver is not authorized`);
        }
      }
      if (same(states[3].actor, candidateAuthor) || same(states[3].actor, ledgerAuthor)) fail(`finding ${id} revalidation must be independent`);
    }
  }
  const findingIdSet = unique(findingIds, 'finding id');
  unique(findingIdsFromSources, 'finding id in reviewer sources');
  if (findingIdsFromSources.length !== findingIds.length || findingIdsFromSources.some((id) => !findingIdSet.has(id))) {
    fail('reviewer source finding IDs do not exactly match the ledger findings');
  }
  return { admitted: true, headSha, findings: findings.length };
}

function sourcePayload(body) {
  const match = /<!--\s*agentweaver\.findings-source\/v1\s*([\s\S]*?)-->/u.exec(body);
  if (!match) return null;
  return JSON.parse(match[1]);
}

async function githubSnapshot(repository, prNumber, token) {
  const request = async (path) => {
    const response = await fetch(`https://api.github.com/repos/${repository}${path}`, {
      headers: { Accept: 'application/vnd.github+json', Authorization: `Bearer ${token}` },
    });
    if (!response.ok) fail(`GitHub API ${path} failed: ${response.status}`);
    return response.json();
  };
  const [pr, comments, reviews] = await Promise.all([
    request(`/pulls/${prNumber}`),
    request(`/issues/${prNumber}/comments?per_page=100`),
    request(`/pulls/${prNumber}/reviews?per_page=100`),
  ]);
  const ledgers = comments.filter((comment) => comment.body.includes(`<!-- ${LEDGER_KIND} -->`));
  if (ledgers.length !== 1) fail('exactly one authoritative admission ledger comment is required');
  const ledger = JSON.parse(ledgers[0].body.replace(`<!-- ${LEDGER_KIND} -->`, '').trim());
  const sources = [];
  for (const review of reviews) {
    const payload = sourcePayload(review.body ?? '');
    if (payload?.kind !== SOURCE_KIND) continue;
    sources.push({
      id: review.id,
      reviewId: payload.reviewId,
      reviewer: review.user.login,
      headSha: review.commit_id,
      findingIds: payload.findings.map((finding) => finding.id),
      requirements: payload.findings.map(({ id, severity, policy }) => ({ id, severity, policy })),
    });
  }
  const snapshot = {
    repository,
    prNumber,
    headSha: pr.head.sha,
    prAuthor: pr.user.login,
    ledgerAuthor: ledgers[0].user.login,
    requiredSourceIds: (process.env.ADMISSION_FINDINGS_REQUIRED_SOURCES ?? '').split(',').filter(Boolean),
    authorizedReviewers: sourceReviewerPolicy(process.env.ADMISSION_FINDINGS_AUTHORIZED_REVIEWERS ?? ''),
    authorizedAdmissionOwners: (process.env.ADMISSION_FINDINGS_ADMISSION_OWNERS ?? '').split(',').filter(Boolean),
    authorizedWaiverApprovers: (process.env.ADMISSION_FINDINGS_WAIVER_APPROVERS ?? '').split(',').filter(Boolean),
    sources,
    correctivePrs: {},
  };
  for (const finding of ledger.findings ?? []) {
    for (const entry of finding.transitions ?? []) {
      if (entry.state !== 'corrective-pr') continue;
      const corrective = await request(`/pulls/${entry.correctivePr}`);
      const comparison = corrective.merged_at
        ? await request(`/compare/${corrective.merge_commit_sha}...${pr.head.sha}`)
        : null;
      snapshot.correctivePrs ??= {};
      snapshot.correctivePrs[entry.correctivePr] = { merged: Boolean(corrective.merged_at), incorporated: comparison?.status === 'ahead' || comparison?.status === 'identical' };
    }
  }
  return { ledger, snapshot };
}

async function main() {
  const [mode, input] = process.argv.slice(2);
  if (mode === 'cohort') {
    console.log(JSON.stringify(validateCohort(JSON.parse(readFileSync(input, 'utf8')))));
    return;
  }
  if (mode !== 'github') fail('usage: admission-findings-gate.mjs github <pr-number> | cohort <ledger.json>');
  const { ledger, snapshot } = await githubSnapshot(process.env.GITHUB_REPOSITORY, Number(input), process.env.GITHUB_TOKEN);
  console.log(JSON.stringify(validateAdmissionLedger(ledger, snapshot)));
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
}
