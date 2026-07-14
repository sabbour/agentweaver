#!/usr/bin/env node
// LLM-in-the-loop persona DRIVER — tool surface.
//
// This is the "tools" half of the persona-brief driving model (see the blog
// technique in decisions/inbox/tank-persona-brief-pivot.md). Instead of a fixed
// script (scenarios/*.mjs) executing a hardcoded sequence of API calls, a fresh-
// context LLM (a sub-agent, or any model with shell access) is handed ONLY a
// persona BRIEF and decides each next action live, based on the REAL API
// responses it gets back. These commands are the thin, discrete tools it calls.
//
// Design invariants (unchanged from the driver/judge correction):
//   * ZERO embedded pass/fail quality heuristics. These tools only DRIVE the real
//     API and RECORD everything verbatim. The LLM decides "what does the persona
//     do next"; a separate judge decides "was the outcome good" from the transcript.
//   * Every call is captured with full request + response bodies; multi-poll tools
//     (get-spec / revise-spec) persist every poll attempt verbatim inside the turn.
//   * Bounded/safe: there is deliberately NO `confirm` tool here — the scoping-rung
//     PoC stops at the confirmation gate and never triggers execution.
//
// Session state (project id, run id, transcript) is persisted to a session JSON so
// the LLM can call these commands as separate shell invocations and keep context.
//
// Usage (each command records a transcript turn; --thought is the persona's live
// reasoning for that turn and is REQUIRED so the transcript reads as a conversation):
//
//   node tools.mjs init         --brief <persona> --base-url <url> [--insecure]
//   node tools.mjs list-blueprints --thought "..."
//   node tools.mjs create-project  --blueprint <id> --thought "..."
//   node tools.mjs get-team        --thought "..."
//   node tools.mjs submit-goal     --goal "<messy batch>" --thought "..."
//   node tools.mjs get-spec        --thought "..."
//   node tools.mjs revise-spec     --feedback "<pushback>" --thought "..."   # the pushback lever
//   node tools.mjs get-events      --thought "..."
//   node tools.mjs check-approvals  --thought "..."                      # detect pending approval gates
//   node tools.mjs resolve-approval --thought "..." [--request-id <id> | --command-hash <h>] [--all]
//                                   [--decision approve|deny|defer|request-changes] [--scope once|run|tool|always]
//                                   [--reason "..."] [--feedback "..."] [--judge-cmd "<llm cli>"]   # DETECT -> JUDGE -> EXECUTE
//   node tools.mjs finish         --summary "..." [--keep]
//
// Every command prints the REAL API response to stdout so the LLM can react to it.

import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join } from 'node:path';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { randomUUID } from 'node:crypto';

import { AgentweaverClient } from '../lib/client.mjs';
import { detectPendingApprovals, describeGate } from '../lib/approvals.mjs';
import { decideApproval, executeApprovalDecision, makeDefaultJudge } from '../lib/approval-judge.mjs';
import { loadPersona } from '../../persona-briefs/index.mjs';

/**
 * @typedef {Object} TranscriptTurn
 * A single structured observability record — one per API action the driving LLM
 * takes. Consistent shape so a judge (JUDGE.md) can reliably look for named fields
 * rather than parsing ad-hoc JSON. Maps to the blog's per-turn "intent/composition"
 * annotations, and doubles as the P4 per-turn latency record.
 * @property {number}  n         1-based turn index.
 * @property {string}  at        ISO timestamp the turn was recorded.
 * @property {?string} sessionId Stable per-run harness session id (correlate all turns of one run).
 * @property {?string} traceId   Backend correlation id from the response headers (App Insights / logs), if any.
 * @property {'persona'|'system'} actor  Who "owns" the turn (persona decision vs system read).
 * @property {?string} thought   INTENT — the persona's live reasoning / goal for this action.
 * @property {?string} action    The api_action taken (e.g. 'submit-goal', 'revise-spec (pushback)').
 * @property {?{method:string,path:string,body:*}} request   Verbatim request (null for meta turns).
 * @property {?{status:number,ms:number,body:*}}   response  Verbatim response incl. per-call latency.
 * @property {?number} latencyMs COMPOSITION/perf — response latency, promoted for easy P4 scanning.
 * @property {?number} upstreamMs Backend-only processing time (x-envoy-upstream-service-time), if present.
 * @property {?{ok:boolean,status:?number}} outcome  Coarse success signal for quick judge scans.
 * @property {?number} tokensIn  Per-turn prompt tokens — null: the API does not expose per-request
 *                               token accounting; token/cost is aggregate via GET /api/projects/{id}/metrics
 *                               (captured at run level in the finding's `performance`, not per-turn).
 * @property {?number} tokensOut Per-turn completion tokens — null for the same reason as tokensIn.
 * @property {?string} note      Short human-readable summary of the response (composition).
 */

const HERE = dirname(fileURLToPath(import.meta.url));
const TRANSCRIPTS_DIR = join(HERE, '..', 'transcripts');
const DEFAULT_SPEC_TIMEOUT_MS = 120_000;
const DEFAULT_SPEC_POLL_MS = 4_000;
const REQUIRED_SUCCESSFUL_PUSHBACKS = 2;

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) out[key] = true;
      else {
        out[key] = next;
        i++;
      }
    }
  }
  return out;
}

function resolveToken(explicit) {
  if (explicit) return explicit;
  if (process.env.AGENTWEAVER_TOKEN) return process.env.AGENTWEAVER_TOKEN;
  return execFileSync('gh', ['auth', 'token'], { encoding: 'utf8' }).trim();
}

async function loadSession() {
  const sessionPath = process.env.AGENTWEAVER_HARNESS_SESSION ?? join(HERE, 'session.current.json');
  if (!existsSync(sessionPath)) throw new Error('no active session — run `init` first');
  return JSON.parse(await readFile(sessionPath, 'utf8'));
}

async function saveSession(s) {
  const sessionPath = process.env.AGENTWEAVER_HARNESS_SESSION ?? join(HERE, 'session.current.json');
  await writeFile(sessionPath, JSON.stringify(s, null, 2), 'utf8');
}

// Append one conversation turn to the transcript. A turn = the persona's live
// reasoning (`thought`), the concrete action taken, and the REAL system response.
// Shape is documented by the TranscriptTurn typedef above so a judge can rely on it.
async function recordTurn(session, { actor, thought, action, apiCall, note }) {
  session.turns.push({
    n: session.turns.length + 1,
    at: new Date().toISOString(),
    sessionId: session.sessionId ?? null, // correlate all turns of one run
    traceId: apiCall ? (apiCall.traceId ?? null) : null, // tie turn to backend App Insights/logs
    actor, // 'persona' | 'system'
    thought: thought ?? null, // persona's intent for this turn (blog: "intent" annotation)
    action: action ?? null,
    request: apiCall ? { method: apiCall.method, path: apiCall.path, body: apiCall.requestBody } : null,
    response: apiCall ? { status: apiCall.status, ms: apiCall.ms, body: apiCall.responseBody } : null,
    latencyMs: apiCall ? (apiCall.ms ?? null) : null, // promoted for P4 perf scanning
    upstreamMs: apiCall ? (apiCall.upstreamMs ?? null) : null, // backend-only latency (envoy header)
    outcome: apiCall ? { ok: apiCall.ok ?? (apiCall.status >= 200 && apiCall.status < 300), status: apiCall.status ?? null } : null,
    tokensIn: null, // API has no per-request token accounting; see /metrics (run-level P4)
    tokensOut: null,
    note: note ?? null,
  });
  await saveSession(session);
}

function newClient(session, token) {
  return new AgentweaverClient({
    baseUrl: session.baseUrl, token, insecure: session.insecure,
    allowProd: session.allowProd, confirmProduction: session.confirmProduction,
  });
}

function print(obj) {
  console.log(JSON.stringify(obj, null, 2));
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function isHttpSuccess(status) {
  return Number.isInteger(status) && status >= 200 && status < 300;
}

function isSettledOutcomeSpec(spec) {
  return typeof spec?.status === 'string' && spec.status !== 'drafting';
}

function summarizeOutcomeSpec(spec) {
  if (!spec || typeof spec !== 'object') return null;
  return {
    goal: spec.goal ?? null,
    desiredOutcome: spec.desiredOutcome ?? null,
    scope: spec.scope ?? null,
    assumptions: spec.assumptions ?? null,
    clarifyingQuestions: spec.clarifyingQuestions ?? null,
    status: spec.status ?? null,
    confirmedBy: spec.confirmedBy ?? null,
  };
}

function specFingerprint(spec) {
  const summary = summarizeOutcomeSpec(spec);
  return summary ? JSON.stringify(summary) : null;
}

function compactApiCall(call) {
  if (!call) return null;
  return {
    method: call.method ?? null,
    path: call.path ?? null,
    status: call.status ?? null,
    ms: call.ms ?? null,
    ok: call.ok ?? isHttpSuccess(call.status),
    traceId: call.traceId ?? null,
    upstreamMs: call.upstreamMs ?? null,
    requestBody: call.requestBody ?? null,
    responseBody: call.responseBody ?? null,
  };
}

async function pollOutcomeSpec(client, runId, { timeoutMs = DEFAULT_SPEC_TIMEOUT_MS, pollMs = DEFAULT_SPEC_POLL_MS } = {}) {
  const started = Date.now();
  /** @type {ReturnType<typeof compactApiCall>[]} */
  const polls = [];
  let finalCall = null;
  let settled = false;

  while (Date.now() - started < timeoutMs) {
    const call = await client.get(`/api/runs/${runId}/outcome-spec`);
    polls.push(compactApiCall(call));
    finalCall = call;
    if (call.status === 200 && isSettledOutcomeSpec(call.responseBody)) {
      settled = true;
      break;
    }
    await sleep(pollMs);
  }

  return {
    settled,
    durationMs: Date.now() - started,
    polls,
    finalCall,
    finalSpec: finalCall?.responseBody ?? null,
  };
}

function collectTurnStatuses(turn) {
  const statuses = [];
  const body = turn?.response?.body;
  if (Number.isInteger(turn?.response?.status)) statuses.push(turn.response.status);

  if (Array.isArray(body?.polls)) {
    for (const poll of body.polls) if (Number.isInteger(poll?.status)) statuses.push(poll.status);
  }
  if (Number.isInteger(body?.preRevisionSpec?.status)) statuses.push(body.preRevisionSpec.status);
  if (Array.isArray(body?.postRevisionPolls)) {
    for (const poll of body.postRevisionPolls) if (Number.isInteger(poll?.status)) statuses.push(poll.status);
  }

  return statuses;
}

function latestObservedSpecStatus(turns) {
  for (let i = turns.length - 1; i >= 0; i--) {
    const body = turns[i]?.response?.body;
    if (body?.finalSpec?.status) return body.finalSpec.status;
    if (body?.spec?.status) return body.spec.status;
    if (typeof body?.status === 'string') return body.status;
  }
  return null;
}

export function computeDeterministicP0(turns = []) {
  const statuses = turns.flatMap(collectTurnStatuses);
  const allApiCallsSucceeded = statuses.length > 0 && statuses.every(isHttpSuccess);
  const revisionFacts = turns
    .filter((turn) => turn?.action === 'revise-spec (pushback)')
    .map((turn) => turn?.response?.body?.objectiveRevision ?? {});
  const pushbacksAppliedSuccessfully = revisionFacts.filter((fact) => fact.appliedSuccessfully === true).length;
  const specReachedSettledStateAfterEachPushback = revisionFacts.length > 0 && revisionFacts.every((fact) => fact.specReachedSettledState === true);
  const latestSpecStatus = latestObservedSpecStatus(turns);
  const endedInSafeTerminalState = latestSpecStatus === 'awaiting_confirmation';
  const objectivePass =
    allApiCallsSucceeded
    && pushbacksAppliedSuccessfully >= REQUIRED_SUCCESSFUL_PUSHBACKS
    && specReachedSettledStateAfterEachPushback
    && endedInSafeTerminalState;

  return {
    objectivePass,
    allApiCallsSucceeded,
    pushbacksAppliedSuccessfully,
    requiredSuccessfulPushbacks: REQUIRED_SUCCESSFUL_PUSHBACKS,
    specReachedSettledStateAfterEachPushback,
    endedInSafeTerminalState,
    latestObservedSpecStatus: latestSpecStatus,
  };
}

async function loadBriefText(brief) {
  try {
    return (await loadPersona(brief, 'api')).text;
  } catch {
    return null;
  }
}

async function loadJudgeMd() {
  return null;
}

// Build the (objective) evidence context handed to the approval judge: the persona
// brief, the JUDGE.md playbook, the recent transcript turns, and the recent raw
// events around the gate. No judgment — just packaging.
async function buildApprovalContext(session, gate, events) {
  const recentTurns = (session.turns ?? []).slice(-6).map((t) => ({
    n: t.n, actor: t.actor, action: t.action, thought: t.thought, note: t.note,
    status: t.response?.status ?? null,
  }));
  let recentEvents = Array.isArray(events) ? events : [];
  if (gate?.sequence != null) {
    recentEvents = recentEvents.filter((e) => typeof e?.sequence !== 'number' || e.sequence <= gate.sequence + 2);
  }
  recentEvents = recentEvents.slice(-15);
  return {
    briefText: await loadBriefText(session.brief),
    judgeMd: await loadJudgeMd(),
    recentTurns,
    recentEvents,
    runId: session.runId ?? null,
    persona: session.brief ?? null,
  };
}

const COMMANDS = {
  async init(args) {
    if (!args.brief) throw new Error('--brief is required');
    if (!args['base-url']) throw new Error('--base-url is required');
    const token = resolveToken(args.token);
    const session = {
      brief: args.brief,
      sessionId: randomUUID(),
      baseUrl: String(args['base-url']).replace(/\/+$/, ''),
      insecure: !!args.insecure,
      allowProd: !!args['allow-prod'],
      confirmProduction: !!args['i-understand-this-targets-production'],
      startedAt: new Date().toISOString(),
      projectId: null,
      runId: null,
      pushbackAttemptCount: 0,
      pushbackCount: 0,
      resolvedApprovalKeys: [],
      turns: [],
    };
    // Verify auth up front so the LLM knows the identity it is driving as.
    const client = newClient(session, token);
    const auth = await client.get('/api/auth/github');
    await recordTurn(session, {
      actor: 'system',
      action: 'init',
      apiCall: auth,
      note: `driving as ${auth.responseBody?.login ?? 'unknown'} against ${session.baseUrl}`,
    });
    await saveSession(session);
    print({
      ok: auth.ok,
      signedInAs: auth.responseBody?.login ?? null,
      sessionPath: process.env.AGENTWEAVER_HARNESS_SESSION ?? join(HERE, 'session.current.json'),
    });
  },

  async 'list-blueprints'(args) {
    const session = await loadSession();
    const client = newClient(session, resolveToken(args.token));
    const res = await client.get('/api/blueprints');
    await recordTurn(session, { actor: 'persona', thought: args.thought, action: 'list-blueprints', apiCall: res });
    const blueprints = (res.responseBody?.blueprints ?? []).map((b) => ({ id: b.id, name: b.name, summary: b.summary ?? b.description }));
    print({ status: res.status, blueprints });
  },

  async 'create-project'(args) {
    const session = await loadSession();
    if (!args.blueprint) throw new Error('--blueprint is required (call list-blueprints first)');
    const client = newClient(session, resolveToken(args.token));
    const slug = args.name || `persona-${session.brief}-live-${Date.now().toString(36)}`;
    const res = await client.post('/api/projects', {
      name: slug,
      origin: 'blank',
      working_directory: slug,
      blueprint_id: args.blueprint,
    });
    session.projectId = res.responseBody?.project_id ?? null;
    await recordTurn(session, { actor: 'persona', thought: args.thought, action: `create-project(${args.blueprint})`, apiCall: res });
    print({ status: res.status, projectId: session.projectId });
  },

  async 'get-team'(args) {
    const session = await loadSession();
    if (!session.projectId) throw new Error('no project yet');
    const client = newClient(session, resolveToken(args.token));
    const res = await client.get(`/api/projects/${session.projectId}/team`);
    await recordTurn(session, { actor: 'persona', thought: args.thought, action: 'get-team', apiCall: res });
    const members = (res.responseBody?.members ?? []).map((m) => ({ name: m.name, role: m.role ?? m.id }));
    print({ status: res.status, memberCount: members.length, members });
  },

  async 'submit-goal'(args) {
    const session = await loadSession();
    if (!session.projectId) throw new Error('no project yet');
    if (!args.goal) throw new Error('--goal is required');
    const client = newClient(session, resolveToken(args.token));
    const res = await client.post(`/api/projects/${session.projectId}/orchestrations`, {
      goal: String(args.goal),
      start_mode: 'defineOutcome',
    });
    session.runId = res.responseBody?.runId ?? null;
    await recordTurn(session, { actor: 'persona', thought: args.thought, action: 'submit-goal', apiCall: res });
    print({ status: res.status, runId: session.runId });
  },

  async 'get-spec'(args) {
    const session = await loadSession();
    if (!session.runId) throw new Error('no run yet');
    const client = newClient(session, resolveToken(args.token));
    const timeoutMs = Number(args.timeout ?? 120) * 1000;
    const polled = await pollOutcomeSpec(client, session.runId, { timeoutMs });
    const res = polled.finalCall ?? { method: 'GET', path: `/api/runs/${session.runId}/outcome-spec`, requestBody: null, status: 0, ms: polled.durationMs, responseBody: null, ok: false };
    await recordTurn(session, {
      actor: 'system',
      thought: args.thought,
      action: 'get-spec',
      apiCall: {
        ...res,
        responseBody: {
          settled: polled.settled,
          polls: polled.polls,
          spec: polled.finalSpec,
        },
      },
      note: polled.settled ? `spec settled: ${polled.finalSpec?.status}` : 'spec did not settle before timeout',
    });
    print({ status: res.status, settled: polled.settled, polls: polled.polls, spec: polled.finalSpec });
  },

  // The PUSHBACK lever. Sends feedback; the coordinator re-drafts the outcome spec
  // and re-suspends at the confirmation gate. This is what makes the run emergent:
  // the LLM reads the re-drafted spec and decides whether it was actually addressed.
  async 'revise-spec'(args) {
    const session = await loadSession();
    if (!session.runId) throw new Error('no run yet');
    if (!args.feedback) throw new Error('--feedback is required (this is the persona pushing back)');
    const client = newClient(session, resolveToken(args.token));
    const timeoutMs = Number(args.timeout ?? 120) * 1000;
    const preRevisionSpec = await client.get(`/api/runs/${session.runId}/outcome-spec`);
    session.pushbackAttemptCount = (session.pushbackAttemptCount ?? 0) + 1;
    const res = await client.post(`/api/runs/${session.runId}/outcome-spec/revise`, { feedback: String(args.feedback) });
    const postRevision = isHttpSuccess(res.status)
      ? await pollOutcomeSpec(client, session.runId, { timeoutMs })
      : { settled: false, durationMs: 0, polls: [], finalCall: null, finalSpec: null };
    const preFingerprint = preRevisionSpec.status === 200 ? specFingerprint(preRevisionSpec.responseBody) : null;
    const postFingerprint = specFingerprint(postRevision.finalSpec);
    const objectiveRevision = {
      attemptNumber: session.pushbackAttemptCount,
      postAccepted: isHttpSuccess(res.status),
      preRevisionFetchOk: isHttpSuccess(preRevisionSpec.status),
      specReachedSettledState: postRevision.settled,
      specChanged: preFingerprint !== null && postFingerprint !== null && preFingerprint !== postFingerprint,
    };
    objectiveRevision.appliedSuccessfully =
      objectiveRevision.postAccepted
      && objectiveRevision.preRevisionFetchOk
      && objectiveRevision.specReachedSettledState
      && objectiveRevision.specChanged;
    if (objectiveRevision.appliedSuccessfully) session.pushbackCount += 1;
    await recordTurn(session, {
      actor: 'persona',
      thought: args.thought,
      action: 'revise-spec (pushback)',
      apiCall: {
        ...res,
        responseBody: {
          reviseCall: compactApiCall(res),
          preRevisionSpec: compactApiCall(preRevisionSpec),
          postRevisionPolls: postRevision.polls,
          finalSpec: postRevision.finalSpec,
          objectiveRevision,
        },
      },
      note: `pushback attempt #${session.pushbackAttemptCount} → successful applications ${session.pushbackCount}: ${String(args.feedback).slice(0, 200)}`,
    });
    print({
      status: res.status,
      pushbackAttemptCount: session.pushbackAttemptCount,
      pushbackCount: session.pushbackCount,
      objectiveRevision,
      body: res.responseBody,
      finalSpec: postRevision.finalSpec,
    });
  },

  async 'get-events'(args) {
    const session = await loadSession();
    if (!session.runId) throw new Error('no run yet');
    const client = newClient(session, resolveToken(args.token));
    const res = await client.get(`/api/runs/${session.runId}/events`);
    const events = Array.isArray(res.responseBody) ? res.responseBody : [];
    const typeCounts = {};
    for (const e of events) typeCounts[e.type] = (typeCounts[e.type] ?? 0) + 1;
    await recordTurn(session, {
      actor: 'system',
      thought: args.thought,
      action: 'get-events',
      apiCall: { method: 'GET', path: `/api/runs/${session.runId}/events`, requestBody: null, status: res.status, ms: res.ms, ok: res.ok, traceId: res.traceId, upstreamMs: res.upstreamMs, responseBody: events },
      note: `${events.length} events`,
    });
    print({ status: res.status, count: events.length, typeCounts, events });
  },

  // Detect whether the run is currently BLOCKED on an approval gate (tool / shell /
  // coordinator-child). Pure structural detection over the real events feed — it
  // makes NO approve/deny judgment; it only reports what is pending so the LLM/judge
  // can act. Signal choice is documented in lib/approvals.mjs (the events feed, not
  // /api/notifications, which only surfaces human_review today).
  async 'check-approvals'(args) {
    const session = await loadSession();
    if (!session.runId) throw new Error('no run yet');
    const client = newClient(session, resolveToken(args.token));
    const res = await client.get(`/api/runs/${session.runId}/events`);
    const events = Array.isArray(res.responseBody) ? res.responseBody : [];
    const { pending } = detectPendingApprovals(events, { alreadyResolvedKeys: session.resolvedApprovalKeys ?? [] });
    const summary = pending.map((g) => ({ key: g.key, kind: g.kind, requestId: g.requestId, commandHash: g.commandHash, toolName: g.toolName, url: g.url, description: describeGate(g) }));
    await recordTurn(session, {
      actor: 'system',
      thought: args.thought,
      action: 'check-approvals',
      apiCall: { method: 'GET', path: `/api/runs/${session.runId}/events`, requestBody: null, status: res.status, ms: res.ms, ok: res.ok, traceId: res.traceId, upstreamMs: res.upstreamMs, responseBody: { pendingApprovals: summary } },
      note: `${pending.length} pending approval gate(s)`,
    });
    print({ status: res.status, pendingCount: pending.length, pending: summary });
  },

  // Drive an approval gate the human way: DETECT -> JUDGE -> EXECUTE. The driver
  // performs zero subjective reasoning: it packages the gate evidence, hands it to
  // the judge (lib/approval-judge.mjs), and executes EXACTLY the judge's decision
  // against the real approval endpoints. Judge resolution: an explicit judged
  // decision passed via --decision (a human/operator acting as judge) > an external
  // judge command (--judge-cmd or $AGENTWEAVER_APPROVAL_JUDGE_CMD) > default DEFER
  // (never blind-approve). Targets one gate via --request-id / --command-hash, else
  // the first pending gate (or every pending gate with --all).
  async 'resolve-approval'(args) {
    const session = await loadSession();
    if (!session.runId) throw new Error('no run yet');
    const client = newClient(session, resolveToken(args.token));
    const evRes = await client.get(`/api/runs/${session.runId}/events`);
    const events = Array.isArray(evRes.responseBody) ? evRes.responseBody : [];
    let { pending } = detectPendingApprovals(events, { alreadyResolvedKeys: session.resolvedApprovalKeys ?? [] });

    if (args['request-id']) pending = pending.filter((g) => g.requestId === args['request-id']);
    if (args['command-hash']) pending = pending.filter((g) => g.commandHash === args['command-hash']);
    if (pending.length === 0) {
      print({ status: evRes.status, resolved: [], note: 'no matching pending approval gate' });
      return;
    }
    const targets = args.all ? pending : [pending[0]];

    // The judge. An explicit --decision is treated as the JUDGE'S verdict passed
    // through (operator/human-as-judge), NOT the driver reasoning. Otherwise defer
    // to a wired judge command, else safe-defer.
    const explicitDecision = args.decision
      ? {
        decision: String(args.decision),
        scope: args.scope ? String(args.scope) : 'once',
        reason: args.reason ? String(args.reason) : '(operator-supplied judged decision)',
        feedback: args.feedback ? { summary: String(args.feedback), requestedChanges: [String(args.feedback)] } : undefined,
        source: 'operator',
      }
      : null;
    const judge = makeDefaultJudge({ explicitDecision, judgeCmd: args['judge-cmd'] ? String(args['judge-cmd']) : null });

    session.resolvedApprovalKeys = session.resolvedApprovalKeys ?? [];
    const results = [];
    for (const gate of targets) {
      const context = await buildApprovalContext(session, gate, events);
      const { prompt, decision } = await decideApproval(gate, context, { judge });
      const outcome = await executeApprovalDecision(client, session.runId, gate, decision);
      if ((outcome.executed && outcome.apiCall?.ok) || outcome.handled) session.resolvedApprovalKeys.push(gate.key);

      // Full audit turn: WHAT was gated, WHAT the judge saw + decided + why, and
      // WHICH API call executed the decision (or that it was deferred). Visible to a
      // human/meta reviewer after the fact — never a silent side effect.
      await recordTurn(session, {
        actor: 'persona',
        thought: args.thought ?? `approval gate detected: ${describeGate(gate)}`,
        action: `resolve-approval (${decision.decision})`,
        apiCall: outcome.apiCall
          ? outcome.apiCall
          : { method: 'NONE', path: `/api/runs/${session.runId} (${outcome.requiresChanges ? 'changes requested' : 'deferred'} — no API call)`, requestBody: null, status: 0, ms: 0, ok: false, responseBody: null },
        note: `gate=${describeGate(gate)} | judge=${decision.decision}/${decision.scope} (${decision.source ?? 'judge'}) reason="${decision.reason}"${outcome.feedback ? ` feedback=${JSON.stringify(outcome.feedback)}` : ''}`,
      });
      // Attach the packaged judge evidence to the just-recorded turn for the audit trail.
      const recorded = session.turns[session.turns.length - 1];
      recorded.approval = {
        gate: { key: gate.key, kind: gate.kind, requestId: gate.requestId, commandHash: gate.commandHash, toolName: gate.toolName, url: gate.url, command: gate.command, evidenceEvent: gate.evidenceEvent },
        judge: { prompt, decision, source: decision.source ?? null },
        executed: outcome.executed,
        requiresChanges: outcome.requiresChanges,
        feedback: outcome.feedback ?? null,
      };
      await saveSession(session);
      results.push({ key: gate.key, decision: decision.decision, scope: decision.scope, reason: decision.reason, feedback: outcome.feedback ?? null, executed: outcome.executed, apiStatus: outcome.apiCall?.status ?? null });
    }
    print({ status: evRes.status, resolved: results });
  },

  async finish(args) {
    const session = await loadSession();
    session.endedAt = new Date().toISOString();
    session.personaSummary = args.summary ?? null;
    session.pushbackAttemptCount = session.pushbackAttemptCount ?? 0;
    session.p0Objective = computeDeterministicP0(session.turns);
    session.pushbackRequirementMet = session.p0Objective.pushbacksAppliedSuccessfully >= REQUIRED_SUCCESSFUL_PUSHBACKS;
    session.p1SubjectiveVerdict = {
      status: 'deferred_to_judge',
      detail: 'The driver computes only deterministic P0 mechanics; P1 content quality must be judged from the transcript.',
    };

    await mkdir(TRANSCRIPTS_DIR, { recursive: true });
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const outPath = join(TRANSCRIPTS_DIR, `${session.brief}-live-${stamp}.json`);
    await writeFile(
      outPath,
      JSON.stringify(
        {
          schema: 'agentweaver.persona-transcript/v1.1',
          model: 'llm-in-the-loop (persona brief, not script)',
          sessionId: session.sessionId ?? null,
          brief: session.brief,
          target: session.baseUrl,
          drivenAs: session.turns[0]?.note ?? null,
          startedAt: session.startedAt,
          endedAt: session.endedAt,
          projectId: session.projectId,
          runId: session.runId,
          pushbackAttemptCount: session.pushbackAttemptCount,
          pushbackCount: session.pushbackCount,
          pushbackRequirementMet: session.pushbackRequirementMet,
          p0Objective: session.p0Objective,
          p1SubjectiveVerdict: session.p1SubjectiveVerdict,
          personaSummary: session.personaSummary,
          turns: session.turns,
        },
        null,
        2,
      ),
      'utf8',
    );

    // Bounded cleanup — cancel + delete the throwaway project unless --keep.
    if (!args.keep && session.projectId) {
      const client = newClient(session, resolveToken(args.token));
      if (session.runId) await client.post(`/api/runs/${session.runId}/cancel`).catch(() => {});
      await client.del(`/api/projects/${session.projectId}?confirm=true`).catch(() => {});
    }

    print({
      transcript: outPath,
      turns: session.turns.length,
      pushbackAttemptCount: session.pushbackAttemptCount,
      pushbackCount: session.pushbackCount,
      pushbackRequirementMet: session.pushbackRequirementMet,
      p0Objective: session.p0Objective,
      p1SubjectiveVerdict: session.p1SubjectiveVerdict,
      cleanedUp: !args.keep,
    });
  },
};

async function main() {
  const [command, ...rest] = process.argv.slice(2);
  if (!command || !COMMANDS[command]) {
    console.error(`usage: node tools.mjs <command> [--flags]\ncommands: ${Object.keys(COMMANDS).join(', ')}`);
    return 2;
  }
  try {
    const args = parseArgs(rest);
    if (args.session) process.env.AGENTWEAVER_HARNESS_SESSION = args.session;
    await COMMANDS[command](args);
    return 0;
  } catch (err) {
    console.error(`error: ${err.message ?? err}`);
    return 1;
  }
}

if (import.meta.url === `file://${process.argv[1]}` || import.meta.url === pathToFileURL(process.argv[1]).href) {
  // Set exitCode and let the event loop drain naturally instead of calling
  // process.exit(): forcing exit while undici keep-alive TLS sockets are still
  // open (with cert verification disabled for staging) can trip a libuv assertion
  // on some Node builds. Draining lets those sockets close cleanly first.
  main()
    .then((code) => {
      process.exitCode = code;
    })
    .catch((err) => {
      console.error(err);
      process.exitCode = 1;
    });
}
