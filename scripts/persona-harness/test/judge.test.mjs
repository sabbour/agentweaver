// test/judge.test.mjs — tests for the LLM-judge PROMPT ASSEMBLER and the
// META-AGGREGATION rollup. These test the driver/formatter logic only; the actual
// P0/P1 judging is done by a real LLM and is deliberately NOT tested here.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

import {
  normalizeTurns,
  extractSpecFromGetSpec,
  extractRevise,
  assembleJudgePrompt,
  VERDICT_SCHEMA,
} from '../lib/judge.mjs';
import { aggregate, findingKey, renderRollup, validateVerdict } from '../lib/meta-aggregate.mjs';

// ---- fixtures: a v1 transcript (spec IS response.body) and a v1.1 transcript
// (spec is response.body.spec; revise carries the rich objectiveRevision block). ----
const v1Transcript = {
  schema: 'agentweaver.persona-transcript/v1',
  brief: 'priya',
  target: 'https://staging.example',
  drivenAs: 'driving as tester',
  personaSummary: 'stopped at gate after two revisions.',
  pushbackCount: 2,
  pushbackRequirementMet: true,
  turns: [
    { n: 1, actor: 'harness', thought: 'sign in', action: 'init', request: {}, response: { status: 200, body: { status: 'signed_in' } }, note: 'signed in' },
    { n: 2, actor: 'persona', thought: 'submit messy batch', action: 'submit-goal', request: { body: { goal: 'triage' } }, response: { status: 201, body: { runId: 'r1' } }, note: 'run created' },
    { n: 3, actor: 'persona', thought: 'see the draft', action: 'get-spec', request: {}, response: { status: 200, body: { goal: 'g', desiredOutcome: 'first draft', scope: 's', assumptions: [], status: 'awaiting_confirmation' } }, note: 'draft 1' },
    { n: 4, actor: 'persona', thought: 'duplicate not flagged — push back', action: 'revise-spec (pushback)', request: { body: { feedback: '4821 and 4822 are the same Contoso issue' } }, response: { status: 200, body: { goal: 'g', desiredOutcome: 'first draft', scope: 's', assumptions: [], status: 'awaiting_confirmation' } }, note: 'prior spec returned' },
    { n: 5, actor: 'persona', thought: 'see redraft', action: 'get-spec', request: {}, response: { status: 200, body: { goal: 'g', desiredOutcome: 'second draft with duplicate flagged', scope: 's', assumptions: [], status: 'awaiting_confirmation' } }, note: 'draft 2' },
  ],
};

const v1_1Transcript = {
  schema: 'agentweaver.persona-transcript/v1.1',
  brief: 'maya',
  sessionId: 'sess-1',
  target: 'https://staging.example',
  p0Objective: { objectivePass: true, allApiCallsSucceeded: true, pushbacksAppliedSuccessfully: 2 },
  turns: [
    { n: 1, actor: 'harness', thought: 'init', action: 'init', request: {}, response: { status: 200, body: { status: 'signed_in' } }, latencyMs: 100, upstreamMs: 40, outcome: 'ok', note: 'ok' },
    {
      n: 2, actor: 'persona', thought: 'push back on missing competitors', action: 'revise-spec (pushback)',
      request: { body: { feedback: 'name the three competitors explicitly' } },
      response: {
        status: 200,
        body: {
          reviseCall: { status: 200 },
          preRevisionSpec: { responseBody: { desiredOutcome: 'brief v1', status: 'awaiting_confirmation' } },
          postRevisionPolls: [{ status: 200 }],
          finalSpec: { desiredOutcome: 'brief v2 with named competitors', status: 'awaiting_confirmation' },
          objectiveRevision: { appliedSuccessfully: true, specReachedSettledState: true, specChanged: true },
        },
      },
      latencyMs: 900, upstreamMs: 300, outcome: 'ok', note: 'revised',
    },
    { n: 3, actor: 'persona', thought: 'see redraft', action: 'get-spec', request: {}, response: { status: 200, body: { settled: true, polls: [{ status: 200 }], spec: { desiredOutcome: 'brief v2 with named competitors', status: 'awaiting_confirmation' } } }, latencyMs: 120, upstreamMs: 50, outcome: 'ok', note: 'draft 2' },
  ],
};

test('extractSpecFromGetSpec unwraps v1 (body is the spec) and v1.1 ({settled,polls,spec})', () => {
  const v1Spec = extractSpecFromGetSpec(v1Transcript.turns[2]);
  assert.equal(v1Spec.desiredOutcome, 'first draft');
  const v11Spec = extractSpecFromGetSpec(v1_1Transcript.turns[2]);
  assert.equal(v11Spec.desiredOutcome, 'brief v2 with named competitors');
  assert.equal(v11Spec.polls, undefined); // unwrapped, not the envelope
});

test('extractRevise captures feedback + before/after across both shapes', () => {
  const v1 = extractRevise(v1Transcript.turns[3]);
  assert.equal(v1.shape, 'v1');
  assert.match(v1.feedback, /4821 and 4822/);
  assert.equal(v1.before.desiredOutcome, 'first draft');
  assert.equal(v1.after, null); // v1: after lands on the next get-spec

  const v11 = extractRevise(v1_1Transcript.turns[1]);
  assert.equal(v11.shape, 'v1.1');
  assert.match(v11.feedback, /three competitors/);
  assert.equal(v11.before.desiredOutcome, 'brief v1');
  assert.equal(v11.after.desiredOutcome, 'brief v2 with named competitors');
  assert.equal(v11.objectiveRevision.appliedSuccessfully, true);
});

test('normalizeTurns classifies actions and attaches spec/pushback evidence', () => {
  const d = normalizeTurns(v1Transcript);
  assert.equal(d.length, 5);
  assert.deepEqual(d.map((x) => x.kind), ['init', 'submit-goal', 'get-spec', 'revise-spec', 'get-spec']);
  assert.equal(d[2].specDraft.desiredOutcome, 'first draft');
  assert.ok(d[3].pushback.feedback.includes('4821'));
  assert.equal(d[0].intent, 'sign in'); // thought -> intent
});

test('assembleJudgePrompt embeds playbook, brief, authored criteria, verbatim spec, and the verdict schema', () => {
  const prompt = assembleJudgePrompt(v1Transcript, {
    judgeMd: '# JUDGE PLAYBOOK\nP0 objective vs P1 subjective vs CANNOT_DETERMINE.',
    briefText: '# Priya brief\nyou push back at least twice.',
    authoredText: '# Priya authored\n## Success looks like\n- duplicates flagged',
    transcriptFile: 'transcripts/priya.json',
  });
  // playbook + brief + authored criteria all present
  assert.match(prompt, /JUDGE PLAYBOOK/);
  assert.match(prompt, /Priya brief/);
  assert.match(prompt, /Success looks like/);
  // verbatim drafted spec content is surfaced for the judge
  assert.match(prompt, /second draft with duplicate flagged/);
  // pushback feedback is surfaced verbatim
  assert.match(prompt, /4821 and 4822/);
  assert.match(prompt, /"status": "signed_in"/);
  assert.match(prompt, /"goal": "triage"/);
  // machine-readable verdict schema is requested for meta-aggregation
  assert.match(prompt, new RegExp(VERDICT_SCHEMA.replace(/[/.]/g, '\\$&')));
  // it must NOT pre-decide a verdict — no literal "PASS"/"FAIL" assertion, only the template placeholders
  assert.match(prompt, /PASS \| FAIL/);
});

test('assembleJudgePrompt surfaces v1.1 p0Objective block and rich before/after', () => {
  const prompt = assembleJudgePrompt(v1_1Transcript, { judgeMd: 'x', briefText: 'y', authoredText: 'z', transcriptFile: 'transcripts/maya.json' });
  assert.match(prompt, /brief v1/);
  assert.match(prompt, /brief v2 with named competitors/);
  assert.match(prompt, /objectiveRevision/);
  assert.match(prompt, /"pushbacksAppliedSuccessfully": 2/);
});

// ---- meta-aggregation ----
const verdicts = [
  {
    schema: VERDICT_SCHEMA, persona: 'priya',
    p0: { verdict: 'PASS', mechanics: { projectCreated: true, teamAssembled: true, specSettled: true } },
    p1: { verdict: 'PASS' },
    pushback: { count: 2, requirementMet: true },
    findings: [{ title: 'Coordinator spec revision can silently regress an already-established requirement', kind: 'P1', recurring: true, relatedIssue: '#315' }],
    cannotDetermine: [],
  },
  {
    schema: VERDICT_SCHEMA, persona: 'jordan',
    p0: { verdict: 'PASS', mechanics: { projectCreated: true, teamAssembled: true, specSettled: true } },
    p1: { verdict: 'PARTIAL' },
    pushback: { count: 3, requirementMet: true },
    findings: [{ title: 'Spec revision regressed the image-publish requirement', kind: 'P1', relatedIssue: '#315' }],
    cannotDetermine: ['whether the downstream deploy rung would succeed (run was bounded at the gate)'],
  },
  {
    schema: VERDICT_SCHEMA, persona: 'maya',
    p0: { verdict: 'PASS', mechanics: { projectCreated: true, teamAssembled: true, specSettled: true } },
    p1: { verdict: 'PASS' },
    pushback: { count: 2, requirementMet: true },
    findings: [
      { title: 'Revision dropped named competitors from an earlier pushback', kind: 'P1', relatedIssue: '#315' },
      { title: 'No lever to separate internal analysis from customer-facing output', kind: 'capability-gap' },
    ],
    cannotDetermine: [],
  },
];

test('findingKey groups the same finding across personas via relatedIssue', () => {
  assert.equal(findingKey({ relatedIssue: '#315', title: 'a' }), 'issue:315');
  assert.equal(findingKey({ relatedIssue: '315', title: 'b' }), 'issue:315');
});

test('validateVerdict rejects non-conforming verdict JSON', () => {
  const bad = {
    schema: 'agentweaver.persona-meta-aggregate/v1',
    p0: { verdict: 'PASS' },
  };
  const result = validateVerdict(bad);
  assert.equal(result.ok, false);
  assert.match(result.errors.join('\n'), /schema must equal/);
  assert.match(result.errors.join('\n'), /p1\.verdict must be a string/);
});

test('aggregate computes invariants, recurring #315, capability gaps and pushback compliance', () => {
  const agg = aggregate(verdicts);
  assert.equal(agg.runs, 3);
  // all three P0 passed -> allGreen and the shared mechanics are invariants
  assert.equal(agg.p0.allGreen, true);
  const invMechanics = agg.invariants.map((i) => i.mechanic).sort();
  assert.deepEqual(invMechanics, ['projectCreated', 'specSettled', 'teamAssembled']);
  // #315 recurred across all three personas -> single recurring finding grouped by issue
  const rec315 = agg.recurringFindings.find((f) => f.relatedIssue === '#315');
  assert.ok(rec315, 'expected a recurring #315 finding');
  assert.deepEqual(rec315.personas.sort(), ['jordan', 'maya', 'priya']);
  // capability gap surfaced
  assert.equal(agg.capabilityGaps.length, 1);
  // P1 diverged (PASS vs PARTIAL) -> flagged as a divergence signal
  assert.equal(agg.p1.divergent, true);
  // pushback >=2 met by everyone
  assert.equal(agg.pushback.requirementMetAll, true);
  // cannot-determine union carried through
  assert.equal(agg.cannotDetermine.length, 1);
});

test('renderRollup produces a readable batch report citing the recurring finding', () => {
  const text = renderRollup(aggregate(verdicts));
  assert.match(text, /BATCH: 3 run\(s\)/);
  assert.match(text, /ALL GREEN/);
  assert.match(text, /#315/);
  assert.match(text, /Recurring findings/);
});

test('meta-aggregate CLI skips non-conforming JSON with a warning and excludes it from counts', () => {
  const tmpRoot = fs.mkdtempSync(path.join(process.cwd(), '.meta-aggregate-test-'));
  const verdictDir = path.join(tmpRoot, 'verdicts');
  fs.mkdirSync(verdictDir, { recursive: true });

  const validPath = path.join(verdictDir, 'priya.json');
  const invalidPath = path.join(verdictDir, 'rollup.json');
  const rollupOut = path.join(tmpRoot, 'aggregate.json');
  fs.writeFileSync(validPath, JSON.stringify(verdicts[0], null, 2), 'utf8');
  fs.writeFileSync(invalidPath, JSON.stringify({ schema: 'agentweaver.persona-meta-aggregate/v1', runs: 99 }, null, 2), 'utf8');

  const cli = spawnSync(
    process.execPath,
    [path.join(process.cwd(), 'lib', 'meta-aggregate.mjs'), verdictDir, '--json', rollupOut],
    { cwd: process.cwd(), encoding: 'utf8' },
  );

  try {
    assert.equal(cli.status, 0, `stderr:\n${cli.stderr}\nstdout:\n${cli.stdout}`);
    assert.match(cli.stderr, /skip .*rollup\.json: non-conforming verdict/i);
    const agg = JSON.parse(fs.readFileSync(rollupOut, 'utf8'));
    assert.equal(agg.runs, 1);
    assert.deepEqual(agg.personas, ['priya']);
  } finally {
    fs.rmSync(tmpRoot, { recursive: true, force: true });
  }
});
