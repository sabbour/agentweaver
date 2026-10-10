#!/usr/bin/env node
import { mkdir, readFile, realpath, rename, unlink, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { randomUUID } from 'node:crypto';
import {
  REQUIRED_REVIEW_SOURCES,
  REVIEWER_IDENTITIES,
  WAIVER_ACTORS,
  assertAdmissionAuthority,
  resolveAdmissionAuthority,
  resolveAdmissionReviewPolicy,
} from './squad-admission-authority.mjs';

export const LEDGER_KIND = 'agentweaver.squad-admission-findings/v3';
export const REVIEW_KIND = 'agentweaver.squad-review/v2';
export const VALIDATION_KIND = 'agentweaver.validation-evidence/v1';
const SHA = /^[0-9a-f]{40}$/iu;
const DIGEST = /^sha256:[0-9a-f]{64}$/iu;
const PHASES = new Set(['design', 'implementation']);
const POLICIES = new Set(['advisory', 'required']);
const VERDICTS = new Set(['approved', 'rejected']);
const RUNTIME_KIND = 'agentweaver.squad-admission-runtime/v2';

function required(value, field) {
  if (typeof value !== 'string' || value.trim() === '') throw new Error(`${field} must be a non-empty string`);
  return value.trim();
}

function sha(value, field) {
  value = required(value, field).toLowerCase();
  if (!SHA.test(value)) throw new Error(`${field} must be a 40-character SHA`);
  return value;
}

function absolute(value, field) {
  value = required(value, field);
  if (!isAbsolute(value)) throw new Error(`${field} must be absolute`);
  return value;
}

function validateTrustedRuntime(runtime, field = 'trustedRuntime') {
  if (!runtime || runtime.kind !== RUNTIME_KIND) throw new Error(`${field}.kind must be ${RUNTIME_KIND}`);
  required(runtime.source?.ref, `${field}.source.ref`);
  sha(runtime.source?.commit, `${field}.source.commit`);
  if (!runtime.files || typeof runtime.files !== 'object' || Array.isArray(runtime.files)) {
    throw new Error(`${field}.files must record trusted source objects`);
  }
  const files = {};
  for (const [path, identity] of Object.entries(runtime.files)) {
    required(path, `${field}.files path`);
    const objectId = required(identity?.objectId, `${field}.files[${path}].objectId`).toLowerCase();
    if (!/^[0-9a-f]{40,64}$/u.test(objectId)) throw new Error(`${field}.files[${path}].objectId must be a Git object ID`);
    const fileDigest = required(identity?.digest, `${field}.files[${path}].digest`).toLowerCase();
    if (!DIGEST.test(fileDigest)) throw new Error(`${field}.files[${path}].digest must be a sha256 digest`);
    files[path] = { objectId, digest: fileDigest };
  }
  if (Object.keys(files).length < 4) throw new Error(`${field}.files must record the launcher and policy modules`);
  const aggregate = required(runtime.aggregateDigest, `${field}.aggregateDigest`).toLowerCase();
  if (!DIGEST.test(aggregate)) {
    throw new Error(`${field}.aggregateDigest must be a sha256 digest`);
  }
  return {
    kind: runtime.kind,
    source: { ref: runtime.source.ref, commit: runtime.source.commit.toLowerCase() },
    files,
    aggregateDigest: aggregate,
  };
}

function targetLineage(target) {
  return target.type === 'artifact'
    ? `${target.type}:${target.artifact}`
    : `${target.type}:${target.worktree}:${target.branch}`;
}

export function validateReviewOutput(review, field = 'review') {
  if (!review || review.kind !== REVIEW_KIND) throw new Error(`${field}.kind must be ${REVIEW_KIND}`);
  if (!PHASES.has(review.phase)) throw new Error(`${field}.phase must be design or implementation`);
  required(review.source, `${field}.source`);
  required(review.reviewer, `${field}.reviewer`);
  if (!VERDICTS.has(review.verdict)) throw new Error(`${field}.verdict must be approved or rejected`);
  if (!review.target || typeof review.target !== 'object') throw new Error(`${field}.target is required`);

  if (review.phase === 'design') {
    if (review.target.type !== 'artifact') throw new Error(`${field}.target.type must be artifact for design review`);
    required(review.target.artifact, `${field}.target.artifact`);
    if (!DIGEST.test(required(review.target.digest, `${field}.target.digest`))) {
      throw new Error(`${field}.target.digest must be a sha256 digest`);
    }
  } else {
    if (review.target.type !== 'worktree') throw new Error(`${field}.target.type must be worktree for implementation review`);
    absolute(review.target.worktree, `${field}.target.worktree`);
    required(review.target.branch, `${field}.target.branch`);
    sha(review.target.headSha, `${field}.target.headSha`);
  }

  if (!Array.isArray(review.findings)) throw new Error(`${field}.findings must be an array`);
  if (review.verdict === 'rejected' && review.findings.length === 0) {
    throw new Error(`${field}.findings must identify why the review was rejected`);
  }
  const ids = new Set();
  for (const [index, finding] of review.findings.entries()) {
    const prefix = `${field}.findings[${index}]`;
    const id = required(finding?.id, `${prefix}.id`);
    if (ids.has(id)) throw new Error(`${field} contains duplicate finding ${id}`);
    ids.add(id);
    if (!POLICIES.has(finding.policy)) throw new Error(`${prefix}.policy must be advisory or required`);
    required(finding.summary, `${prefix}.summary`);
    if (finding.waiver !== undefined) {
      if (finding.policy !== 'required') throw new Error(`${prefix}.waiver is only valid for required findings`);
      required(finding.waiver.actor, `${prefix}.waiver.actor`);
      required(finding.waiver.rationale, `${prefix}.waiver.rationale`);
    }
  }

  if (review.correctiveOf !== undefined) {
    const findingId = required(review.correctiveOf, `${field}.correctiveOf`);
    if (review.findings.length !== 1 || review.findings[0].id !== findingId) {
      throw new Error(`${field} corrective re-review must preserve its finding ID`);
    }
  }
  return review;
}

export function validateValidationEvidence(evidence, expected, field = 'validation') {
  if (!evidence || evidence.kind !== VALIDATION_KIND) throw new Error(`${field}.kind must be ${VALIDATION_KIND}`);
  if (!Array.isArray(evidence.argv) || evidence.argv.length === 0) throw new Error(`${field}.argv must be a non-empty array`);
  if (absolute(evidence.cwd, `${field}.cwd`) !== expected.worktree) throw new Error(`${field}.cwd does not match the candidate worktree`);
  if (absolute(evidence.worktree, `${field}.worktree`) !== expected.worktree) throw new Error(`${field}.worktree does not match the candidate worktree`);
  if (required(evidence.branch, `${field}.branch`) !== expected.branch) throw new Error(`${field}.branch does not match the candidate branch`);
  if (sha(evidence.headSha, `${field}.headSha`) !== expected.headSha) throw new Error(`${field}.headSha does not match the candidate SHA`);
  required(evidence.startedAt, `${field}.startedAt`);
  required(evidence.completedAt, `${field}.completedAt`);
  if (evidence.exitCode !== 0 || evidence.result !== 'passed') throw new Error(`${field} did not pass`);
  if (!evidence.after || evidence.after.cwd !== expected.worktree || evidence.after.worktree !== expected.worktree
    || evidence.after.branch !== expected.branch || evidence.after.headSha !== expected.headSha) {
    throw new Error(`${field}.after does not match the candidate provenance`);
  }
  return evidence;
}

function validateCorrectiveReviews(reviews) {
  const initialFindings = new Map();
  const correctiveFindings = new Set();
  for (const review of reviews.filter((entry) => entry.correctiveOf === undefined)) {
    for (const finding of review.findings) {
      const key = `${review.phase}:${review.source}:${targetLineage(review.target)}:${finding.id}`;
      if (initialFindings.has(key)) throw new Error(`duplicate initial finding ${finding.id} from ${review.source}`);
      initialFindings.set(key, { review, finding });
    }
  }
  for (const review of reviews.filter((entry) => entry.correctiveOf !== undefined)) {
    const key = `${review.phase}:${review.source}:${targetLineage(review.target)}:${review.correctiveOf}`;
    if (!initialFindings.has(key)) throw new Error(`corrective re-review ${review.correctiveOf} does not match its original phase, source, and target`);
    if (correctiveFindings.has(key)) throw new Error(`corrective re-review ${review.correctiveOf} has conflicting results`);
    correctiveFindings.add(key);
  }
}

function assertRequiredReviewSources(sources, requiredSources) {
  if (!Array.isArray(sources)) throw new Error('requiredReviewSources must match configured reviewer policy');
  if (sources.length !== requiredSources.length
    || sources.some((source, index) => source !== requiredSources[index])) {
    throw new Error('requiredReviewSources must match configured reviewer policy');
  }
}

export function materializeLedger(input, {
  requiredReviewSources = REQUIRED_REVIEW_SOURCES,
  reviewerIdentities = REVIEWER_IDENTITIES,
  waiverActors = WAIVER_ACTORS,
  trustedRuntime,
  baseSha,
} = {}) {
  if (!input || typeof input !== 'object') throw new Error('materialization input is required');
  const repository = required(input.repository, 'repository');
  if (!/^[\w.-]+\/[\w.-]+$/u.test(repository)) throw new Error('repository must be owner/name');
  if (!Number.isSafeInteger(input.prNumber) || input.prNumber < 1) throw new Error('prNumber must be a positive integer');
  const candidate = {
    worktree: absolute(input.worktree, 'worktree'),
    branch: required(input.branch, 'branch'),
    headSha: sha(input.headSha, 'headSha'),
  };
  const runtime = validateTrustedRuntime(trustedRuntime);
  const trustedBaseSha = sha(baseSha, 'trusted base SHA');
  assertRequiredReviewSources(input.requiredReviewSources, requiredReviewSources);
  const requiredSources = [...requiredReviewSources];
  if (!Array.isArray(input.reviews)) throw new Error('reviews must be an array');
  if (!Array.isArray(input.validations) || input.validations.length === 0) throw new Error('validations must contain structured exact-head evidence');

  const reviews = input.reviews.map((review, index) => validateReviewOutput(review, `reviews[${index}]`));
  const allowedWaiverActors = new Set(waiverActors.map((actor) => required(actor, 'waiver actor').toLowerCase()));
  for (const review of reviews) {
    for (const finding of review.findings) {
      if (finding.waiver !== undefined && !allowedWaiverActors.has(finding.waiver.actor.toLowerCase())) {
        throw new Error(`waiver actor ${finding.waiver.actor} is not authorized by configured policy`);
      }
    }
  }
  const implementationReviews = reviews.filter((review) => review.phase === 'implementation');
  for (const [index, review] of implementationReviews.entries()) {
    if (review.target.worktree !== candidate.worktree || review.target.branch !== candidate.branch) {
      throw new Error(`implementation review ${index} does not match the candidate worktree lineage`);
    }
    if ((review.verdict === 'approved' || review.correctiveOf !== undefined) && review.target.headSha !== candidate.headSha) {
      throw new Error(`implementation review ${index} approval does not match the candidate SHA`);
    }
  }
  validateCorrectiveReviews(reviews);
  for (const source of requiredSources) {
    const allowedReviewers = new Set((reviewerIdentities[source] ?? [])
      .map((reviewer) => required(reviewer, `configured reviewer for ${source}`).toLowerCase()));
    if (allowedReviewers.size === 0) throw new Error(`configured reviewer policy is missing identities for: ${source}`);
    const sourceReviews = implementationReviews.filter((review) => review.source === source);
    if (sourceReviews.some((review) => !allowedReviewers.has(review.reviewer.toLowerCase()))) {
      throw new Error(`reviewer identity is not authorized for required source: ${source}`);
    }
    const exactHeadReviews = sourceReviews.filter((review) => review.target.headSha === candidate.headSha);
    if (!exactHeadReviews.some((review) => review.verdict === 'approved')) {
      throw new Error(`missing required exact-head approval from: ${source}`);
    }
    if (exactHeadReviews.some((review) => review.verdict === 'rejected')) {
      throw new Error(`required review source ${source} has a conflicting exact-head rejection`);
    }
  }
  const requiredReviewers = requiredSources.map((source) => required(
    implementationReviews.find((review) => review.source === source
      && review.target.headSha === candidate.headSha && review.verdict === 'approved')?.reviewer,
    `required reviewer for ${source}`,
  ).toLowerCase());
  if (new Set(requiredReviewers).size !== requiredReviewers.length) {
    throw new Error('required review sources must be independently issued by distinct reviewers');
  }
  const validations = input.validations.map((entry, index) => validateValidationEvidence(entry, candidate, `validations[${index}]`));

  return {
    kind: LEDGER_KIND,
    repository,
    prNumber: input.prNumber,
    headSha: candidate.headSha,
    worktree: candidate.worktree,
    branch: candidate.branch,
    baseSha: trustedBaseSha,
    trustedRuntime: runtime,
    requiredReviewSources: requiredSources,
    reviews,
    validations,
    materializedAt: required(input.materializedAt ?? new Date().toISOString(), 'materializedAt'),
  };
}

export function validateAdmissionLedger(ledger, expected) {
  if (!ledger || ledger.kind !== LEDGER_KIND) throw new Error(`kind must be ${LEDGER_KIND}`);
  const recordedRuntime = validateTrustedRuntime(ledger.trustedRuntime, 'ledger.trustedRuntime');
  const expectedRuntime = validateTrustedRuntime(expected.trustedRuntime, 'expected.trustedRuntime');
  if (JSON.stringify(recordedRuntime) !== JSON.stringify(expectedRuntime)) {
    throw new Error('ledger trusted runtime identity does not match the installed authority');
  }
  if (sha(ledger.baseSha, 'ledger.baseSha') !== sha(expected.baseSha, 'expected.baseSha')) {
    throw new Error('ledger trusted base SHA does not match');
  }
  const materialized = materializeLedger(ledger, {
    requiredReviewSources: expected.requiredReviewSources ?? REQUIRED_REVIEW_SOURCES,
    reviewerIdentities: expected.reviewerIdentities ?? REVIEWER_IDENTITIES,
    waiverActors: expected.waiverActors ?? WAIVER_ACTORS,
    trustedRuntime: expected.trustedRuntime,
    baseSha: expected.baseSha,
  });
  if (materialized.repository !== expected.repository) throw new Error('repository does not match');
  if (materialized.prNumber !== expected.prNumber) throw new Error('PR number does not match');
  if (materialized.headSha !== sha(expected.headSha, 'expected.headSha')) throw new Error('ledger evidence is stale for the live PR head');
  if (materialized.baseSha !== sha(expected.baseSha, 'expected.baseSha')) throw new Error('ledger trusted base SHA does not match');

  for (const review of materialized.reviews.filter((entry) => entry.phase === 'implementation' && entry.correctiveOf === undefined)) {
    const unresolved = review.findings.some((finding) => finding.policy === 'required'
      && finding.waiver === undefined
      && !materialized.reviews.some((entry) => entry.correctiveOf === finding.id
        && entry.phase === review.phase && entry.source === review.source
        && targetLineage(entry.target) === targetLineage(review.target)
        && entry.target.headSha === materialized.headSha && entry.verdict === 'approved'));
    if (unresolved) throw new Error(`required finding from ${review.source} is unresolved`);
  }
  return { admitted: true, headSha: materialized.headSha, findings: materialized.reviews.reduce((count, review) => count + review.findings.length, 0) };
}

function localAdapter(teamRoot) {
  return {
    async writeAtomic(key, value) {
      const path = join(teamRoot, key);
      const temporary = `${path}.${randomUUID()}.tmp`;
      await mkdir(dirname(path), { recursive: true });
      try {
        await writeFile(temporary, value, { encoding: 'utf8', flag: 'wx' });
        await rename(temporary, path);
      } catch (error) {
        await unlink(temporary).catch(() => {});
        throw error;
      }
    },
    read: (key) => readFile(join(teamRoot, key), 'utf8'),
  };
}

export async function materializeAdmissionLedger(input, {
  authority,
  teamRoot,
  stateBackend,
  stateAdapter,
  cwd = process.cwd(),
  policyRun,
  baseSha,
  trustedRuntime,
} = {}) {
  const configuredAuthority = authority ?? await resolveAdmissionAuthority({ cwd });
  const trusted = await assertAdmissionAuthority(configuredAuthority, { teamRoot, stateBackend });
  const requiredReviewSources = await resolveAdmissionReviewPolicy(
    { cwd, headSha: input.headSha, baseSha },
    { run: policyRun },
  );
  const backend = trusted.stateBackend;
  const root = trusted.teamRoot;
  const adapter = backend === 'local' || backend === 'worktree'
    ? localAdapter(await realpath(root))
    : stateAdapter;
  if (!adapter || typeof adapter.writeAtomic !== 'function' || typeof adapter.read !== 'function') {
    throw new Error(`state backend ${backend} requires an explicit same-backend atomic adapter`);
  }

  const ledger = materializeLedger(input, {
    requiredReviewSources,
    trustedRuntime,
    baseSha,
  });
  const key = `admission/findings/${ledger.repository}/${ledger.prNumber}.json`;
  await adapter.writeAtomic(key, `${JSON.stringify(ledger, null, 2)}\n`);
  const persisted = JSON.parse(await adapter.read(key));
  validateAdmissionLedger(persisted, {
    ...ledger,
    requiredReviewSources,
    trustedRuntime,
    baseSha,
  });
  return { key, ledger: persisted, stateBackend: backend, teamRoot: root };
}

async function main() {
  throw new Error('candidate checkout admission code is evidence only; invoke the launcher extracted from the fetched trusted base');
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
