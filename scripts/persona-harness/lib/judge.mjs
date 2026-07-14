// lib/judge.mjs — LLM-judge PROMPT ASSEMBLER for persona transcripts.
//
// This is a DRIVER/FORMATTER, not a judge. It NEVER decides whether produced
// content is good, and it NEVER calls an LLM API (no keys, no network). It packages
// a captured transcript + the JUDGE.md playbook + the persona's authored criteria
// into a single prompt string that a REAL LLM (this conversation, the coordinator,
// or a future automated step) can consume to render the P0/P1/CANNOT_DETERMINE
// verdict — consistent with the harness's "no embedded heuristics, LLM as judge"
// contract.
//
// CLI:   node lib/judge.mjs <transcript.json> [--out file.txt]
//        node lib/judge.mjs transcripts/priya-live-....json > judge-prompt-priya.txt
//
// The prompt asks the judge to emit BOTH a human-readable verdict (JUDGE.md format)
// AND a machine-readable ```json``` verdict block (agentweaver.persona-judge-verdict/v1)
// that lib/meta-aggregate.mjs can consume across a batch.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
export const HARNESS_ROOT = path.resolve(__dirname, '..');
export const REPO_ROOT = path.resolve(HARNESS_ROOT, '..', '..');

export const VERDICT_SCHEMA = 'agentweaver.persona-judge-verdict/v1';

/** @param {string} p */
export function loadTranscript(p) {
  return JSON.parse(fs.readFileSync(p, 'utf8'));
}

/**
 * Resolve the persona brief file and the authored persona spec (Success/Failure
 * criteria) for a transcript. The brief name is `transcript.brief` (e.g. "priya");
 * the authored spec path is parsed from the `specs/personas/<name>.md` link the
 * brief file embeds ("Derived from ..."). Falls back to a filename-contains match.
 * @param {any} transcript
 * @param {{harnessRoot?:string, repoRoot?:string}} [opts]
 */
export function resolvePersonaSources(transcript, opts = {}) {
  const harnessRoot = opts.harnessRoot ?? HARNESS_ROOT;
  const repoRoot = opts.repoRoot ?? REPO_ROOT;
  const briefName = transcript.brief ?? transcript.persona ?? transcript.slug ?? 'unknown';

  const briefPath = path.join(harnessRoot, 'briefs', `${briefName}.md`);
  let briefText = null;
  if (fs.existsSync(briefPath)) briefText = fs.readFileSync(briefPath, 'utf8');

  let authoredPath = null;
  let authoredText = null;
  const personasDir = path.join(repoRoot, 'specs', 'personas');
  // 1) parse the "specs/personas/<name>.md" link out of the brief body.
  if (briefText) {
    const m = briefText.match(/specs\/personas\/([a-z0-9-]+)\.md/i);
    if (m) {
      const candidate = path.join(personasDir, `${m[1]}.md`);
      if (fs.existsSync(candidate)) authoredPath = candidate;
    }
  }
  // 2) fallback: a personas file whose name contains the brief name.
  if (!authoredPath && fs.existsSync(personasDir)) {
    const hit = fs
      .readdirSync(personasDir)
      .filter((f) => f.endsWith('.md') && f.toLowerCase() !== 'readme.md')
      .find((f) => f.toLowerCase().includes(String(briefName).toLowerCase()));
    if (hit) authoredPath = path.join(personasDir, hit);
  }
  if (authoredPath) authoredText = fs.readFileSync(authoredPath, 'utf8');

  return { briefName, briefPath, briefText, authoredPath, authoredText };
}

function classifyAction(action = '') {
  const a = String(action);
  if (a.startsWith('revise-spec')) return 'revise-spec';
  if (a.startsWith('create-project')) return 'create-project';
  if (a.startsWith('get-spec')) return 'get-spec';
  for (const k of ['init', 'list-blueprints', 'get-team', 'submit-goal', 'get-events', 'confirm-spec', 'steer', 'finish']) {
    if (a.startsWith(k)) return k;
  }
  return 'other';
}

/** The drafted outcome spec from a get-spec turn, across v1 (body IS the spec) and
 *  v1.1 (body = {settled, polls, spec}). */
export function extractSpecFromGetSpec(turn) {
  const body = turn?.response?.body;
  if (!body || typeof body !== 'object') return null;
  if (body.spec && typeof body.spec === 'object') return body.spec; // v1.1
  return body; // v1: {goal, desiredOutcome, scope, assumptions, status}
}

/** Before/after + objective sub-facts of a revise-spec (pushback) turn.
 *  v1: response.body is the PRIOR spec (synchronous), after lands on the next
 *  get-spec. v1.1: response.body = {preRevisionSpec, postRevisionPolls, finalSpec,
 *  objectiveRevision}. */
export function extractRevise(turn) {
  const body = turn?.response?.body ?? {};
  const feedback =
    turn?.request?.body?.feedback ??
    turn?.request?.body?.reason ??
    (typeof turn?.request?.body === 'string' ? turn.request.body : null);
  if (body && (body.preRevisionSpec || body.finalSpec || body.objectiveRevision)) {
    // v1.1 rich shape
    const before = body.preRevisionSpec?.responseBody ?? body.preRevisionSpec?.spec ?? body.preRevisionSpec ?? null;
    return {
      shape: 'v1.1',
      feedback,
      before,
      after: body.finalSpec ?? null,
      objectiveRevision: body.objectiveRevision ?? null,
      polls: body.postRevisionPolls ?? null,
    };
  }
  // v1: body is the prior spec; the resulting draft appears on the following get-spec.
  return { shape: 'v1', feedback, before: body ?? null, after: null, objectiveRevision: null, polls: null };
}

/** Shape-agnostic per-turn digest a judge (or a human) can read consistently. */
export function normalizeTurns(transcript) {
  const turns = transcript.turns ?? transcript.transcript ?? [];
  return turns.map((t) => {
    const kind = classifyAction(t.action);
    const digest = {
      n: t.n,
      actor: t.actor ?? null,
      action: t.action ?? null,
      kind,
      intent: t.thought ?? null, // why this call was made
      composition: t.note ?? null, // what the response contained
      httpStatus: t.response?.status ?? null,
      latencyMs: t.latencyMs ?? null,
      upstreamMs: t.upstreamMs ?? null,
      outcome: t.outcome ?? null,
      rawTurn: t,
    };
    if (kind === 'get-spec') digest.specDraft = extractSpecFromGetSpec(t);
    if (kind === 'revise-spec') digest.pushback = extractRevise(t);
    return digest;
  });
}

function fence(obj) {
  return '```json\n' + JSON.stringify(obj, null, 2) + '\n```';
}

function renderTurn(d) {
  const lines = [];
  lines.push(`### Turn ${d.n} — ${d.action}  (kind: ${d.kind}, HTTP ${d.httpStatus ?? 'n/a'}${d.outcome ? ', outcome: ' + d.outcome : ''})`);
  if (d.latencyMs != null) lines.push(`- latencyMs: ${d.latencyMs}${d.upstreamMs != null ? `, upstreamMs: ${d.upstreamMs}` : ''}`);
  if (d.intent) lines.push(`- **intent:** ${d.intent}`);
  if (d.composition) lines.push(`- **composition:** ${d.composition}`);
  if (d.rawTurn) lines.push(`- raw recorded turn (lossless JSON evidence):\n${fence(d.rawTurn)}`);
  if (d.kind === 'revise-spec' && d.pushback) {
    if (d.pushback.feedback) lines.push(`- **pushback feedback (verbatim):** ${JSON.stringify(d.pushback.feedback)}`);
    if (d.pushback.objectiveRevision) lines.push(`- objectiveRevision: ${JSON.stringify(d.pushback.objectiveRevision)}`);
    if (d.pushback.before) lines.push(`- spec BEFORE this pushback:\n${fence(d.pushback.before)}`);
    if (d.pushback.after) lines.push(`- spec AFTER this pushback:\n${fence(d.pushback.after)}`);
    else lines.push(`- spec AFTER: (see the next get-spec turn — v1 transcripts return the prior spec synchronously and land the redraft on the following poll)`);
  }
  if (d.kind === 'get-spec' && d.specDraft) {
    lines.push(`- drafted outcome spec (verbatim):\n${fence(d.specDraft)}`);
  }
  return lines.join('\n');
}

/**
 * Assemble the full judge prompt. Pure w.r.t. inputs — all file content is passed
 * in, so this is unit-testable without disk. Returns a single string.
 * @param {any} transcript
 * @param {{judgeMd:string, briefText:string|null, authoredText:string|null, transcriptFile?:string}} ctx
 */
export function assembleJudgePrompt(transcript, ctx) {
  const { judgeMd, briefText, authoredText, transcriptFile } = ctx;
  const digests = normalizeTurns(transcript);
  const pushbacks = digests.filter((d) => d.kind === 'revise-spec');

  const meta = {
    transcriptFile: transcriptFile ?? null,
    schema: transcript.schema ?? null,
    persona: transcript.brief ?? transcript.persona ?? null,
    model: transcript.model ?? null,
    target: transcript.target ?? null,
    drivenAs: transcript.drivenAs ?? null,
    projectId: transcript.projectId ?? null,
    runId: transcript.runId ?? null,
    startedAt: transcript.startedAt ?? null,
    endedAt: transcript.endedAt ?? null,
    turnCount: digests.length,
    pushbackTurns: pushbacks.map((p) => p.n),
    pushbackCount: transcript.pushbackCount ?? null,
    pushbackRequirementMet: transcript.pushbackRequirementMet ?? null,
    p0Objective: transcript.p0Objective ?? null, // present in v1.1 (Ghost's deterministic block)
  };

  const verdictTemplate = {
    schema: VERDICT_SCHEMA,
    transcript: transcriptFile ?? transcript.slug ?? null,
    persona: meta.persona,
    p0: { verdict: 'PASS | FAIL', evidence: '<one line + which mechanic>', mechanics: { authAccepted: true, blueprintOffered: true, projectCreated: true, teamAssembled: true, runAccepted: true, eventsFlowed: true, noRunFailed: true, specSettled: true } },
    p1: {
      verdict: 'PASS | PARTIAL | FAIL',
      evidence: '<quoted spec content vs the persona\'s authored criteria>',
      criteriaCoverage: [{ criterion: '<authored success item>', met: 'yes | no | partial', quote: '<verbatim from spec>' }],
    },
    pushback: { count: 0, requirementMet: true, each: [{ n: 0, grounded: 'true|false — was it based on real returned content?', addressed: 'true|false|regressed', beforeAfterQuote: '<...>' }] },
    cannotDetermine: ['<list, or empty>'],
    findings: [{ title: '<...>', kind: 'P0 | P1 | capability-gap | drift', recurring: 'true|false', relatedIssue: '<#nnn or null>', evidence: '<transcript turn refs>' }],
  };

  return [
    '# TASK: Judge one persona-harness transcript',
    '',
    'You are the LLM judge for the Agentweaver persona harness. The harness is a pure',
    'DRIVER: it drove a persona brief live against the real Agentweaver API and captured',
    'the verbatim evidence below. It did NOT decide whether the produced content is good —',
    '**that is your job.** Judge ONLY from the captured evidence; do not re-run anything.',
    'If the evidence genuinely does not show something either way, say CANNOT_DETERMINE —',
    'never guess.',
    '',
    '---',
    '## The JUDGE playbook (methodology — P0 / P1 / CANNOT_DETERMINE + pushback rules)',
    '',
    judgeMd?.trim() || '(JUDGE.md not found — apply P0 objective mechanics vs P1 subjective quality vs CANNOT_DETERMINE.)',
    '',
    '---',
    '## The persona BRIEF that was driven (goals / voice / constraints)',
    '',
    briefText?.trim() || '(brief not found)',
    '',
    '---',
    "## The persona's AUTHORED criteria (specs/personas) — \"Success looks like\" / \"Failure signals\"",
    '',
    authoredText?.trim() || '(authored persona criteria not found — judge P1 against the brief above)',
    '',
    '---',
    '## Run metadata',
    '',
    fence(meta),
    '',
    '---',
    '## Captured evidence — turn by turn (intent + composition + verbatim content + lossless raw turn JSON)',
    '',
    digests.map(renderTurn).join('\n\n'),
    '',
    '---',
    '## Persona self-summary (the driving LLM\'s own closing note)',
    '',
    (transcript.personaSummary ? String(transcript.personaSummary) : '(none)'),
    '',
    '---',
    '## What to output',
    '',
    'First, the human-readable verdict in the JUDGE.md "Output format" (RUN / PERSONA /',
    'P0 / P1 / CANNOT_DETERMINE / Pushback / Filed work).',
    '',
    'Then, a machine-readable verdict block in EXACTLY this shape (so the meta-aggregation',
    'pass can consume it) — fill in real values, keep the `schema` field verbatim:',
    '',
    fence(verdictTemplate),
    '',
    'Reminders: P0 is objective orchestration mechanics (was each API call a success, did',
    'the spec leave `drafting`/settle, did the mandatory ≥2 pushbacks each apply). P1 is',
    "subjective quality vs THIS persona's authored criteria — quote the drafted spec, and",
    'for each pushback quote the before/after to say whether the system improved, deflected,',
    'or REGRESSED an unrelated already-established requirement (cf. issue #315).',
  ].join('\n');
}

/** Disk-backed convenience: read transcript + JUDGE.md + persona sources and assemble. */
export function buildPromptForFile(transcriptFile, opts = {}) {
  const harnessRoot = opts.harnessRoot ?? HARNESS_ROOT;
  const transcript = loadTranscript(transcriptFile);
  const judgeMdPath = path.join(harnessRoot, 'JUDGE.md');
  const judgeMd = fs.existsSync(judgeMdPath) ? fs.readFileSync(judgeMdPath, 'utf8') : '';
  const { briefText, authoredText } = resolvePersonaSources(transcript, opts);
  return assembleJudgePrompt(transcript, { judgeMd, briefText, authoredText, transcriptFile });
}

// ---- CLI ----
function isMain() {
  return process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
}
if (isMain()) {
  const args = process.argv.slice(2);
  const outIdx = args.indexOf('--out');
  let outFile = null;
  if (outIdx !== -1) {
    outFile = args[outIdx + 1];
    args.splice(outIdx, 2);
  }
  const transcriptFile = args[0];
  if (!transcriptFile) {
    console.error('usage: node lib/judge.mjs <transcript.json> [--out prompt.txt]');
    process.exit(2);
  }
  const prompt = buildPromptForFile(transcriptFile);
  if (outFile) {
    fs.writeFileSync(outFile, prompt, 'utf8');
    console.error(`judge prompt written to ${outFile} (${prompt.length} chars) — feed it to an LLM.`);
  } else {
    process.stdout.write(prompt);
  }
}
