// Generic persona-scenario DRIVER.
//
// IMPORTANT — driver/judge separation (per @sabbour's architectural correction):
// this module is a DRIVER + EVIDENCE CAPTURER only. It drives Agentweaver through
// the API exactly as a persona would, records the FULL raw evidence trail (every
// API call with request+response bodies, the complete event stream, the drafted
// outcome spec verbatim, per-phase timings), and computes ONLY deterministic,
// objective PLATFORM-CORRECTNESS (P0) checks — did the calls succeed, did a team
// assemble, did the spec settle, did events flow, was there no hard run failure.
//
// It DOES NOT judge subjective output quality (P1) — e.g. "is the drafted plan
// actually good for this persona?". That verdict is deferred to a separate LLM
// judge pass that reads the captured evidence + the persona's authored success
// criteria. The driver therefore embeds no content heuristics / regex "pass"
// gating; it just captures enough that a judge (human or LLM) can render P0/P1
// from the JSON report alone, without re-running anything.
//
// Bounded by design: it starts a coordinator run in `defineOutcome` mode, which
// drafts a confirmable outcome spec and suspends at the confirmation gate. That
// exercises project creation, multi-agent team assembly, and coordinator
// planning WITHOUT executing/merging/deploying anything — the safe first rung of
// the self-improvement loop. Deeper rungs (confirm → run → review gate) reuse the
// same client and can be layered on later.

// Statuses that mean the run will never reach the confirmation gate — short-circuit
// the poll instead of burning the whole timeout. `bounded` is intentionally excluded
// (a bounded run may still have drafted a spec before hitting its step budget).
import { detectPendingApprovals, describeGate } from './approvals.mjs';
import { decideApproval, executeApprovalDecision, makeDefaultJudge } from './approval-judge.mjs';

const TERMINAL_FAIL = new Set(['failed', 'cancelled', 'canceled', 'errored', 'error']);

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * @param {import('./client.mjs').AgentweaverClient} client
 * @param {any} scenario  scenario playbook (scenarios/*.mjs default export)
 * @param {any} persona   parsed persona (lib/persona.mjs loadPersona result)
 * @param {Object} opts
 * @param {number} [opts.timeoutMs]
 * @param {number} [opts.pollMs]
 * @param {boolean} [opts.keep]  keep the project/run instead of cleaning up
 * @param {boolean} [opts.driveApprovals]  when true, detect + judge + drive approval gates
 *        that appear during the run (deeper rungs). Default false — the scoping rung
 *        suspends at the confirmation gate and never raises tool/shell gates.
 * @param {(input:{prompt:string,gate:object,context:object})=>Promise<object>} [opts.judge]
 *        the in-the-loop approval judge. The driver executes EXACTLY what it returns; it
 *        never decides approve/deny itself. Defaults to a safe DEFER judge (see
 *        lib/approval-judge.mjs makeDefaultJudge), so enabling driveApprovals without a
 *        real judge never blind-approves anything.
 */
export async function driveScenario(client, scenario, persona, opts = {}) {
  const timeoutMs = opts.timeoutMs ?? 240_000;
  const pollMs = opts.pollMs ?? 4_000;
  const started = Date.now();
  const driveApprovals = opts.driveApprovals ?? scenario.driveApprovals ?? false;
  const approvalJudge = opts.judge ?? makeDefaultJudge();
  const resolvedApprovalKeys = new Set();

  // Per-phase latency — wall-clock ms for each milestone so speed regressions are
  // visible in the finding over time.
  const timings = {};
  const time = async (key, fn) => {
    const t0 = Date.now();
    try {
      return await fn();
    } finally {
      timings[key] = Date.now() - t0;
    }
  };

  const evidence = {
    submittedGoal: null,
    projectId: null,
    runId: null,
    team: null,
    outcomeSpec: null,
    events: [],
    eventTypes: [],
    runStatus: null,
    // Audit trail of every judged approval gate driven during the run (requirement
    // 5): what was gated, what the judge saw + decided + why, and the API call that
    // executed the decision. Empty for scoping-rung runs (no gates raised).
    approvalDecisions: [],
  };
  // ONLY deterministic platform-correctness (P0) checks live here. No subjective
  // content judgment — that is the LLM judge's job, from the captured evidence.
  /** @type {{name:string, pass:boolean, detail:string, category:string}[]} */
  const platformChecks = [];
  const addCheck = (name, pass, detail = '') =>
    platformChecks.push({ name, pass: !!pass, detail, category: 'P0' });

  // --- Step 1: authenticate (persona identity via bearer token) ---
  const auth = await client.get('/api/auth/github');
  const signedIn = auth.ok && auth.responseBody?.status === 'signed_in';
  addCheck(
    'Authenticated as a real user (bearer token accepted)',
    signedIn,
    signedIn ? `signed in as ${auth.responseBody.login}` : `status ${auth.status}`,
  );
  if (!signedIn) return finalize();

  // --- Step 2: persona explores available blueprints ---
  const blueprints = await time('blueprintsFetchMs', () => client.get('/api/blueprints'));
  const list = blueprints.responseBody?.blueprints ?? [];
  const hasBlueprint = list.some((b) => b.id === scenario.blueprintId);
  addCheck(
    `Blueprint "${scenario.blueprintId}" is offered`,
    hasBlueprint,
    hasBlueprint ? 'found in catalog' : `not in ${list.length} blueprints`,
  );
  if (!hasBlueprint) return finalize();

  // --- Step 3: create a project seeded with a multi-agent team ---
  const slug = `${scenario.projectPrefix}-${Date.now().toString(36)}`;
  const create = await time('projectCreateMs', () =>
    client.post('/api/projects', {
      name: slug,
      origin: 'blank',
      working_directory: slug,
      blueprint_id: scenario.blueprintId,
    }),
  );
  evidence.projectId = create.responseBody?.project_id ?? null;
  addCheck(
    'Project created from a plain-language starting point',
    create.status === 201 && !!evidence.projectId,
    evidence.projectId ? `project ${evidence.projectId}` : `status ${create.status}`,
  );
  if (!evidence.projectId) return finalize();

  // --- Step 4: confirm a multi-agent team was assembled ---
  const team = await time('teamFetchMs', () => client.get(`/api/projects/${evidence.projectId}/team`));
  evidence.team = team.responseBody;
  const memberCount = Array.isArray(team.responseBody?.members) ? team.responseBody.members.length : 0;
  addCheck(
    'A multi-agent team was assembled (not a single generalist)',
    memberCount >= 2,
    `${memberCount} team member(s)`,
  );

  // --- Step 5: persona "starts the run" — coordinator drafts a plan ---
  const goal = scenario.buildGoal(persona);
  evidence.submittedGoal = goal;
  const orch = await time('orchestrationAcceptMs', () =>
    client.post(`/api/projects/${evidence.projectId}/orchestrations`, {
      goal,
      start_mode: 'defineOutcome',
    }),
  );
  evidence.runId = orch.responseBody?.runId ?? null;
  addCheck(
    'Coordinator accepted the goal and started a run',
    orch.status === 201 && !!evidence.runId,
    evidence.runId ? `run ${evidence.runId}` : `status ${orch.status} ${JSON.stringify(orch.responseBody)}`,
  );
  if (!evidence.runId) return finalize();

  // --- Step 6: poll for the *settled* reviewable outcome spec (bounded wait) ---
  // The spec transitions drafting -> awaiting_confirmation -> confirmed. We keep
  // the latest snapshot but only treat it as "reviewable" once it leaves the
  // transient "drafting" state, so downstream checks judge real drafted content
  // rather than an echo of the submitted goal.
  let sawFailure = false;
  let specSettled = false;
  const pollStarted = Date.now();
  while (Date.now() - started < timeoutMs) {
    const runResp = await client.get(`/api/runs/${evidence.runId}`);
    evidence.runStatus = runResp.responseBody?.status ?? evidence.runStatus;

    const spec = await client.get(`/api/runs/${evidence.runId}/outcome-spec`);
    if (spec.status === 200 && spec.responseBody) {
      evidence.outcomeSpec = spec.responseBody;
      if (spec.responseBody.status && spec.responseBody.status !== 'drafting') {
        specSettled = true;
        break;
      }
    }
    if (evidence.runStatus && TERMINAL_FAIL.has(evidence.runStatus)) {
      sawFailure = true;
      break;
    }

    // Drive any approval gate that appeared this tick so the run does not stall
    // waiting on an approval that never comes. Detection is deterministic; the
    // approve/deny/defer/request-changes decision is the injected judge's, and the driver executes
    // exactly that. Only active when opts.driveApprovals is enabled (deeper rungs).
    if (driveApprovals) {
      await driveApprovalsOnce();
    }
    await sleep(pollMs);
  }
  timings.outcomeSpecSettleMs = Date.now() - pollStarted;
  evidence.outcomeSpecSettled = specSettled;

  // --- Step 7: pull the FULL event trail for evidence (verbatim) ---
  // The judge needs the complete stream, not a {sequence,type} projection.
  const events = await client.get(`/api/runs/${evidence.runId}/events`);
  if (Array.isArray(events.responseBody)) {
    evidence.events = events.responseBody;
    evidence.eventTypes = events.responseBody.map((e) => ({ sequence: e.sequence, type: e.type }));
  }
  const failedEvent = evidence.eventTypes.some((e) => e.type === 'run.failed');

  addCheck(
    'Run progressed without a hard failure',
    !sawFailure && !failedEvent && !TERMINAL_FAIL.has(evidence.runStatus ?? ''),
    `status=${evidence.runStatus ?? 'unknown'}, ${evidence.events.length} events`,
  );
  addCheck(
    'Coordinator produced a reviewable plan (idea → plan in one traceable flow)',
    !!evidence.outcomeSpec && specSettled,
    specSettled
      ? `outcome spec settled (status=${evidence.outcomeSpec?.status}) and fetchable via API`
      : evidence.outcomeSpec
        ? 'outcome spec still drafting at timeout (never reached confirmation gate)'
        : 'no outcome spec within timeout',
  );

  // --- Non-gating judge context (optional) ---
  // Scenarios may supply deterministic REFERENCE DATA to help a downstream judge
  // (e.g. Priya's expected ticket IDs). This is NOT a pass/fail check — it never
  // gates the driver verdict; it is captured verbatim into the finding for the
  // judge to compare against the drafted content.
  let judgeContext = null;
  try {
    judgeContext = scenario.judgeContext?.(evidence) ?? null;
  } catch (err) {
    judgeContext = { error: `judgeContext threw: ${err?.message ?? err}` };
  }

  return finalize(judgeContext);

  // Detect + judge + execute every currently-pending approval gate on the run. The
  // driver contributes no judgment: it packages evidence, calls the judge, executes
  // exactly the decision, and records the full audit trail into
  // evidence.approvalDecisions. Best-effort — a transport error never fails the run.
  async function driveApprovalsOnce() {
    try {
      const evRes = await client.get(`/api/runs/${evidence.runId}/events`);
      const events = Array.isArray(evRes.responseBody) ? evRes.responseBody : [];
      const { pending } = detectPendingApprovals(events, { alreadyResolvedKeys: resolvedApprovalKeys });
      for (const gate of pending) {
        const context = {
          briefText: persona?.raw ?? null,
          recentTurns: [],
          recentEvents: events.slice(-15),
          runId: evidence.runId,
          persona: persona?.title ?? null,
          approvalScope: scenario.approvalScope,
        };
        const { prompt, decision } = await decideApproval(gate, context, { judge: approvalJudge });
        const outcome = await executeApprovalDecision(client, evidence.runId, gate, decision);
        if ((outcome.executed && outcome.apiCall?.ok) || outcome.handled) resolvedApprovalKeys.add(gate.key);
        evidence.approvalDecisions.push({
          detectedAt: new Date().toISOString(),
          gate: { key: gate.key, kind: gate.kind, requestId: gate.requestId, commandHash: gate.commandHash, toolName: gate.toolName, url: gate.url, command: gate.command, description: describeGate(gate), evidenceEvent: gate.evidenceEvent },
          judge: { prompt, decision, source: decision.source ?? null },
          executed: outcome.executed,
          requiresChanges: outcome.requiresChanges,
          feedback: outcome.feedback ?? null,
          apiCall: outcome.apiCall
            ? { method: outcome.apiCall.method, path: outcome.apiCall.path, status: outcome.apiCall.status, ok: outcome.apiCall.ok, responseBody: outcome.apiCall.responseBody }
            : null,
        });
      }
    } catch (err) {
      evidence.approvalDecisions.push({ detectedAt: new Date().toISOString(), error: `driveApprovalsOnce threw: ${err?.message ?? err}` });
    }
  }

  function finalize(jctx = null) {
    // The driver's verdict is ONLY objective platform-correctness (P0). Subjective
    // output quality (P1) is intentionally NOT decided here — a separate LLM judge
    // renders that from the captured evidence + persona success criteria.
    const platformPass = platformChecks.length > 0 && platformChecks.every((c) => c.pass);
    return {
      platformPass,
      platformChecks,
      judgeContext: jctx,
      evidence,
      timings,
      durationMs: Date.now() - started,
      cleanup: async () => {
        if (opts.keep) return;
        if (evidence.runId) await client.post(`/api/runs/${evidence.runId}/cancel`).catch(() => {});
        if (evidence.projectId) await client.del(`/api/projects/${evidence.projectId}?confirm=true`).catch(() => {});
      },
    };
  }
}
