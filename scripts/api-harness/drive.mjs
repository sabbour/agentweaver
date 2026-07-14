#!/usr/bin/env node
// The ONE way to drive a persona scenario against the live Agentweaver API.
//
// There is no fixed per-scenario script here. A driving LLM (Harness itself, or a
// sub-agent it dispatches) is handed a persona brief (persona-briefs/personas/*.md
// + persona-briefs/surfaces/*.api.md) and decides EVERY next action live: which
// endpoint to call, when to push back with a revision, when to poll events/
// approvals, when to stop at a gate. This file only supplies the thin, generic
// primitives that make that possible:
//
//   node drive.mjs init   --brief <persona> --base-url <url> [--insecure]
//   node drive.mjs spec   [--refresh]                              # read the API surface
//   node drive.mjs call   --method GET|POST|PUT|DELETE --path "/api/..." [--body '<json>'] --thought "..."
//   node drive.mjs call   --operation-id <opId> [--params '{"id":"..."}'] [--body '<json>'] --thought "..."
//   node drive.mjs check-approvals  --thought "..."
//   node drive.mjs resolve-approval --thought "..." [--request-id <id> | --command-hash <h>] [--all]
//                                   [--decision approve|deny|defer|request-changes] [--scope once|run|tool|always]
//                                   [--reason "..."] [--feedback "..."] [--judge-cmd "<llm cli>"]
//   node drive.mjs finish --summary "..." [--keep]
//
// WHY NOT A CURATED LIST OF NAMED BUSINESS ACTIONS (submit-goal, revise-spec,
// get-spec, ...)? Per @sabbour's explicit direction, refined over two rounds of
// feedback: first "I don't want subcommands, I want direct curl calls to the
// API... you probably need a Swagger endpoint", then clarified further —
// "doesn't have to be curl, could be a dynamic client created from swagger" — the
// mechanism doesn't matter (raw method/path/body vs. a spec-resolved
// operationId), what matters is that the SET OF CALLABLE OPERATIONS comes
// dynamically from the OpenAPI/Swagger spec (whatever the API serves), never from
// a fixed list this file writes per persona/scenario. `call` supports BOTH: a raw
// `--method`/`--path` (curl-equivalent) and a `--operation-id` resolved against
// the cached spec (a minimal "dynamic client built from swagger" — method+path
// template looked up, `{param}` path placeholders and query params filled from
// `--params`). Either way the LLM decides which endpoint to hit by reading the
// spec (via `spec`) and the persona-brief intent, not by picking from a pre-baked
// menu of what "submitting a goal" or "pushing back" means. That menu WAS the
// rigidity bug: a fixed submit-goal/revise-spec/get-spec vocabulary structurally
// cannot express "poll /api/runs/{id}/events three times, notice X, then call a
// completely different endpoint Y" — exactly the kind of grounded, adaptive
// behavior personas like Priya require.
//
// `check-approvals`/`resolve-approval` remain distinct, NAMED commands — NOT
// because approvals are a business action being curated, but because they encode
// a safety invariant: the driver must never blind-approve a gate. That judge-gated
// DETECT -> JUDGE -> EXECUTE flow (lib/approval-judge.mjs) is a guardrail, not a
// workflow shortcut, so it is kept as its own command rather than folded into
// `call` (which never decides anything and never overrides its own defer-by-
// default judge).
//
// Design invariants (unchanged from the original driver/judge correction):
//   * ZERO embedded pass/fail quality heuristics. `call` only DRIVES the real API
//     and RECORDS everything verbatim. The LLM decides what to do next; a separate
//     Judge subagent decides whether the outcome was good, from the transcript.
//   * Every call is captured with full request + response bodies, verbatim.
//   * Bounded/safe: there is no `confirm` tool — the scoping-rung PoC stops at the
//     confirmation gate and never triggers execution, and `call` cannot itself
//     approve anything (that only happens through resolve-approval's judge gate).
//
// Session state (project id if the persona created one, run id if it started one,
// the transcript) is persisted to a session JSON so the LLM can call these
// commands as separate shell invocations and keep context across turns.

import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join } from 'node:path';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { randomUUID } from 'node:crypto';

import { AgentweaverClient } from './lib/client.mjs';
import { detectPendingApprovals, describeGate } from './lib/approvals.mjs';
import { decideApproval, executeApprovalDecision, makeDefaultJudge } from './lib/approval-judge.mjs';
import { loadPersona } from '../persona-briefs/index.mjs';

/**
 * @typedef {Object} TranscriptTurn
 * A single structured observability record — one per API action the driving LLM
 * takes. Consistent shape so a judge can reliably look for named fields rather
 * than parsing ad-hoc JSON.
 * @property {number}  n         1-based turn index.
 * @property {string}  at        ISO timestamp the turn was recorded.
 * @property {?string} sessionId Stable per-run harness session id.
 * @property {?string} traceId   Backend correlation id, if any.
 * @property {'persona'|'system'} actor  Who "owns" the turn.
 * @property {?string} thought   INTENT — the persona's live reasoning for this action.
 * @property {?string} action    e.g. 'GET /api/runs/{id}/events', 'check-approvals'.
 * @property {?{method:string,path:string,body:*}} request   Verbatim request.
 * @property {?{status:number,ms:number,body:*}}   response  Verbatim response.
 * @property {?number} latencyMs
 * @property {?number} upstreamMs
 * @property {?{ok:boolean,status:?number}} outcome
 * @property {?string} note
 */

const HERE = dirname(fileURLToPath(import.meta.url));
const TRANSCRIPTS_DIR = join(HERE, 'transcripts');

// Candidate OpenAPI/Swagger document locations, tried in order. The backend does
// not yet serve one of these (tracked as a follow-up with the API owner — see
// README "Known gap"); `spec` reports that plainly rather than pretending success.
const OPENAPI_CANDIDATE_PATHS = ['/openapi/v1.json', '/swagger/v1/swagger.json'];

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
async function recordTurn(session, { actor, thought, action, apiCall, note }) {
  session.turns.push({
    n: session.turns.length + 1,
    at: new Date().toISOString(),
    sessionId: session.sessionId ?? null,
    traceId: apiCall ? (apiCall.traceId ?? null) : null,
    actor,
    thought: thought ?? null,
    action: action ?? null,
    request: apiCall ? { method: apiCall.method, path: apiCall.path, body: apiCall.requestBody } : null,
    response: apiCall ? { status: apiCall.status, ms: apiCall.ms, body: apiCall.responseBody } : null,
    latencyMs: apiCall ? (apiCall.ms ?? null) : null,
    upstreamMs: apiCall ? (apiCall.upstreamMs ?? null) : null,
    outcome: apiCall ? { ok: apiCall.ok ?? (apiCall.status >= 200 && apiCall.status < 300), status: apiCall.status ?? null } : null,
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

function isHttpSuccess(status) {
  return Number.isInteger(status) && status >= 200 && status < 300;
}

// Generic, persona-agnostic P0 mechanics check: did every recorded `call` turn
// succeed? This intentionally embeds NO knowledge of what any particular endpoint,
// action name, or pushback count "should" be — that would reintroduce the same
// curated-action rigidity this file exists to remove. Whether the PERSONA's
// pushback/objections were substantively grounded is a P1 content question the
// Judge answers from the full transcript, not something the driver counts here.
export function computeDeterministicP0(turns = []) {
  const callTurns = (turns ?? []).filter((t) => t?.request && t?.response);
  const statuses = callTurns.map((t) => t.response?.status);
  const allApiCallsSucceeded = statuses.length > 0 && statuses.every(isHttpSuccess);
  return {
    objectivePass: allApiCallsSucceeded,
    totalCalls: callTurns.length,
    allApiCallsSucceeded,
  };
}

// Minimal "dynamic client built from swagger": given the cached OpenAPI endpoint
// list (from `spec`) and an operationId, resolve the concrete method + path to
// call, substituting `{param}` path placeholders and appending declared query
// params — both from a single `--params` map. Pure/no I/O so it's unit-testable
// without a live API. Throws on any operationId/param the spec doesn't declare;
// never guesses.
export function resolveOperation(cachedSpec, operationId, params = {}) {
  const endpoint = (cachedSpec?.endpoints ?? []).find((e) => e.operationId === operationId);
  if (!endpoint) throw new Error(`operationId "${operationId}" not found in the cached spec`);
  const pathParamNames = (endpoint.parameters ?? []).filter((p) => p.in === 'path').map((p) => p.name);
  const queryParamNames = (endpoint.parameters ?? []).filter((p) => p.in === 'query').map((p) => p.name);
  let path = endpoint.path.replace(/\{([^}]+)\}/g, (_, name) => {
    if (!(name in params)) throw new Error(`operation "${operationId}" requires path param "${name}" in --params`);
    return encodeURIComponent(params[name]);
  });
  const query = queryParamNames
    .filter((name) => params[name] !== undefined)
    .map((name) => `${encodeURIComponent(name)}=${encodeURIComponent(params[name])}`)
    .join('&');
  if (query) path += `?${query}`;
  return { method: endpoint.method, path, pathParamNames, queryParamNames };
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
      resolvedApprovalKeys: [],
      turns: [],
    };
    // Verify auth up front so the LLM knows the identity it is driving as, and
    // surface the persona brief text so it doesn't have to guess where to read it.
    const client = newClient(session, token);
    const auth = await client.get('/api/auth/github');
    await recordTurn(session, {
      actor: 'system',
      action: 'init',
      apiCall: auth,
      note: `driving as ${auth.responseBody?.login ?? 'unknown'} against ${session.baseUrl}`,
    });
    let brief = null;
    try {
      brief = await loadPersona(args.brief, 'api');
    } catch (err) {
      brief = { error: `could not load persona brief "${args.brief}": ${err.message}` };
    }
    await saveSession(session);
    print({
      ok: auth.ok,
      signedInAs: auth.responseBody?.login ?? null,
      sessionPath: process.env.AGENTWEAVER_HARNESS_SESSION ?? join(HERE, 'session.current.json'),
      personaBriefText: brief?.text ?? null,
      personaBriefError: brief?.error ?? null,
      hint: 'Read the persona brief above, then call `spec` to see what endpoints exist, then drive with `call`.',
    });
  },

  // Fetch (and cache) the API's OpenAPI/Swagger document so the driving LLM knows
  // what endpoints/shapes exist, instead of guessing or reinventing raw requests
  // blind. KNOWN GAP: apps/Agentweaver.Api does not yet serve one (no Swashbuckle /
  // Microsoft.AspNetCore.OpenApi wiring in Program.cs as of this writing) — this
  // command reports that plainly and falls back to pointing at
  // apps/Agentweaver.Api/API.md as the interim hand-written reference.
  async spec(args) {
    const session = await loadSession();
    const client = newClient(session, resolveToken(args.token));
    const cachePath = join(HERE, 'openapi.cache.json');
    if (!args.refresh && existsSync(cachePath)) {
      const cached = JSON.parse(await readFile(cachePath, 'utf8'));
      print({ ...cached, fromCache: true });
      return;
    }
    for (const p of OPENAPI_CANDIDATE_PATHS) {
      const res = await client.get(p);
      if (res.status === 200 && res.responseBody && typeof res.responseBody === 'object') {
        const paths = res.responseBody.paths ?? {};
        const endpoints = Object.entries(paths).flatMap(([path, ops]) =>
          Object.entries(ops ?? {}).map(([method, op]) => ({
            method: method.toUpperCase(),
            path,
            operationId: op?.operationId ?? null,
            summary: op?.summary ?? op?.operationId ?? null,
            // Path/query parameter names — lets `call --operation-id` substitute
            // {param} placeholders and build the query string dynamically, i.e. a
            // minimal dynamic client built FROM the spec rather than a fixed list
            // of named actions per persona.
            parameters: Array.isArray(op?.parameters)
              ? op.parameters.map((prm) => ({ name: prm?.name, in: prm?.in, required: !!prm?.required }))
              : [],
            hasRequestBody: !!op?.requestBody,
          })),
        );
        const result = { available: true, source: p, endpointCount: endpoints.length, endpoints };
        await writeFile(cachePath, JSON.stringify(result, null, 2), 'utf8');
        print(result);
        return;
      }
    }
    print({
      available: false,
      triedPaths: OPENAPI_CANDIDATE_PATHS,
      note: 'No OpenAPI/Swagger document is currently served by this API instance.',
      fallback: 'apps/Agentweaver.Api/API.md (hand-written endpoint reference in the repo)',
    });
  },

  // The ONE generic action primitive. Arbitrary method/path/body against the real
  // API — the driving LLM chooses these from the OpenAPI spec (`spec`) and the
  // persona-brief intent, turn by turn. This never interprets, curates, or limits
  // which endpoint may be called; it only records what happened, verbatim.
  //
  // Two equivalent ways to address an endpoint, BOTH fully spec/response-driven
  // (never a fixed per-persona action list):
  //   --method <M> --path <P>                      raw method/path (curl-equivalent)
  //   --operation-id <id> [--params '{"id":"x"}']   resolved dynamically against the
  //                                                  cached OpenAPI doc (`spec` output) —
  //                                                  a minimal "dynamic client built
  //                                                  from swagger": method+path template
  //                                                  are looked up, {param} placeholders
  //                                                  in the path are substituted from
  //                                                  --params, and any remaining params
  //                                                  declared `in: query` are appended
  //                                                  as a query string. Requires `spec`
  //                                                  to have been called first this
  //                                                  session (or --refresh via `spec`).
  async call(args) {
    const session = await loadSession();
    const client = newClient(session, resolveToken(args.token));
    let method;
    let path;
    let params = {};
    if (args.params !== undefined) {
      try {
        params = JSON.parse(String(args.params));
      } catch (err) {
        throw new Error(`--params is not valid JSON: ${err.message}`);
      }
    }
    if (args['operation-id']) {
      const cachePath = join(HERE, 'openapi.cache.json');
      if (!existsSync(cachePath)) throw new Error('no cached OpenAPI spec — run `spec` first, then retry with --operation-id');
      const cached = JSON.parse(await readFile(cachePath, 'utf8'));
      const resolved = resolveOperation(cached, args['operation-id'], params);
      method = resolved.method;
      path = resolved.path;
    } else {
      if (!args.method) throw new Error('--method is required (GET|POST|PUT|DELETE), or use --operation-id');
      if (!args.path) throw new Error('--path is required, e.g. /api/projects, or use --operation-id');
      method = String(args.method).toUpperCase();
      path = String(args.path);
    }

    let body;
    if (args.body !== undefined && args.body !== true) {
      try {
        body = JSON.parse(String(args.body));
      } catch (err) {
        throw new Error(`--body is not valid JSON: ${err.message}`);
      }
    }
    const res = await client.call(method, path, body);

    // Best-effort bookkeeping so later commands (check-approvals, finish cleanup)
    // know which project/run this session is currently driving, without requiring
    // the LLM to track ids itself. Purely observational — never gates anything.
    if (method === 'POST' && /^\/api\/projects\/?$/.test(path) && res.responseBody?.project_id) {
      session.projectId = res.responseBody.project_id;
    }
    const orchMatch = /^\/api\/projects\/[^/]+\/orchestrations\/?$/.test(path);
    if (method === 'POST' && orchMatch && res.responseBody?.runId) {
      session.runId = res.responseBody.runId;
    }

    await recordTurn(session, { actor: 'persona', thought: args.thought, action: `${method} ${path}`, apiCall: res });
    print({ status: res.status, ok: res.ok, body: res.responseBody, projectId: session.projectId, runId: session.runId });
  },

  // Detect whether the run is currently BLOCKED on an approval gate (tool / shell /
  // coordinator-child). Pure structural detection over the real events feed — it
  // makes NO approve/deny judgment; it only reports what is pending so the LLM/
  // judge can act. Signal choice documented in lib/approvals.mjs.
  async 'check-approvals'(args) {
    const session = await loadSession();
    if (!session.runId) throw new Error('no run yet (call `call` to start one, then set --path accordingly)');
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
      const recentTurns = (session.turns ?? []).slice(-6).map((t) => ({
        n: t.n, actor: t.actor, action: t.action, thought: t.thought, note: t.note, status: t.response?.status ?? null,
      }));
      let recentEvents = events;
      if (gate?.sequence != null) recentEvents = recentEvents.filter((e) => typeof e?.sequence !== 'number' || e.sequence <= gate.sequence + 2);
      recentEvents = recentEvents.slice(-15);
      let briefText = null;
      try { briefText = (await loadPersona(session.brief, 'api')).text; } catch { /* best-effort */ }
      const context = { briefText, judgeMd: null, recentTurns, recentEvents, runId: session.runId, persona: session.brief ?? null };

      const { prompt, decision } = await decideApproval(gate, context, { judge });
      const outcome = await executeApprovalDecision(client, session.runId, gate, decision);
      if ((outcome.executed && outcome.apiCall?.ok) || outcome.handled) session.resolvedApprovalKeys.push(gate.key);

      await recordTurn(session, {
        actor: 'persona',
        thought: args.thought ?? `approval gate detected: ${describeGate(gate)}`,
        action: `resolve-approval (${decision.decision})`,
        apiCall: outcome.apiCall
          ? outcome.apiCall
          : { method: 'NONE', path: `/api/runs/${session.runId} (${outcome.requiresChanges ? 'changes requested' : 'deferred'} — no API call)`, requestBody: null, status: 0, ms: 0, ok: false, responseBody: null },
        note: `gate=${describeGate(gate)} | judge=${decision.decision}/${decision.scope} (${decision.source ?? 'judge'}) reason="${decision.reason}"${outcome.feedback ? ` feedback=${JSON.stringify(outcome.feedback)}` : ''}`,
      });
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
    session.p0Objective = computeDeterministicP0(session.turns);
    session.p1SubjectiveVerdict = {
      status: 'deferred_to_judge',
      detail: 'The driver computes only deterministic P0 mechanics (did every recorded call succeed). P1 content quality — including whether any pushback/objections were grounded — must be judged from the full transcript.',
    };

    await mkdir(TRANSCRIPTS_DIR, { recursive: true });
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const outPath = join(TRANSCRIPTS_DIR, `${session.brief}-live-${stamp}.json`);
    await writeFile(
      outPath,
      JSON.stringify(
        {
          schema: 'agentweaver.persona-transcript/v2',
          model: 'llm-in-the-loop (persona brief, dynamic — no fixed script)',
          sessionId: session.sessionId ?? null,
          brief: session.brief,
          target: session.baseUrl,
          drivenAs: session.turns[0]?.note ?? null,
          startedAt: session.startedAt,
          endedAt: session.endedAt,
          projectId: session.projectId,
          runId: session.runId,
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
      p0Objective: session.p0Objective,
      p1SubjectiveVerdict: session.p1SubjectiveVerdict,
      cleanedUp: !args.keep,
    });
  },
};

async function main() {
  const [command, ...rest] = process.argv.slice(2);
  if (!command || !COMMANDS[command]) {
    console.error(`usage: node drive.mjs <command> [--flags]\ncommands: ${Object.keys(COMMANDS).join(', ')}`);
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
      process.exitCode = 2;
    });
}
