# JUDGE playbook — persona harness

This is the **judge** half of the persona harness's driver/judge separation. The
harness (driver) never decides whether produced content is *good*; it only drives
Agentweaver via the real API and captures verbatim evidence. **You** — a fresh LLM
agent (or a human) reading that evidence — render the quality verdict. This
playbook tells you how.

You are handed one of two kinds of evidence artifact:

- A **per-run finding** — `scripts/persona-harness/findings/*.json`
  (`agentweaver.persona-finding/v2`), emitted by `run-persona.mjs` (the fixed-
  script scenarios), OR
- A **live transcript** — `scripts/persona-harness/transcripts/*.json`
  (`agentweaver.persona-transcript/v1.1`), emitted by the LLM-in-the-loop driver
  (`agent-driver/tools.mjs`), a turn-by-turn record of a persona brief being
  driven live. Each turn is a typed record (see the `TranscriptTurn` JSDoc in
  `tools.mjs`): `{n, sessionId, traceId, actor, thought` (intent)`, action`
  (api_action)`, request, response, latencyMs, outcome, tokensIn/Out, note}` — so
  you can look for named fields reliably. `sessionId` correlates all turns of one
  run; `traceId` (from the response's `traceparent`/`request-id` header, when the
  server sends one) ties a turn to backend App Insights/logs. `tokensIn/Out` are
  `null` per turn (the API has no per-request token accounting; token/cost is
  aggregate, in the finding's `performance`). There is no `first_chunk_ms` — the
  API returns whole JSON responses, not a token stream, so time-to-first-chunk
  isn't meaningful here (`latencyMs` is full round-trip).

Plus the persona's authored criteria in `specs/personas/*.md` ("Success looks
like" + "Failure signals").

> **"Turn" = an API action, not a chat message (for now).** This harness adapts the
> "simulate user conversations" technique to an **API-request-driven** surface, not
> a chat surface. Each turn in a transcript is the driving LLM choosing the next
> real API *action* (`create-project`, `submit-goal`, `get-spec`, `revise-spec`,
> and — in deeper rungs — `confirm-spec`, `steer`, `assembly/review`) from the
> persona brief + the real JSON responses so far. "**Pushback**" therefore means
> the driving LLM, having read a real response (a drafted spec, an assembly plan, a
> review-gate diff), decides the persona would object/redirect and issues a real
> lever call (`revise-spec` feedback, a `steer`, or a review `request_changes`) —
> not free-text chat. Same turn-by-turn / no-prescripted-both-sides discipline,
> same mandatory ≥2 pushback, same meta-aggregation — just anchored to API
> actions + responses. Each turn is annotated with **intent** (why the call was
> made, `turn.thought`) and **composition** (what the response contained,
> `turn.note` / captured `response.body`).

You do **not** re-run anything. Judge from the captured evidence alone. If the
evidence genuinely does not show something either way, say so — never guess.

---

## Layer 1 — Per-run verdict (one finding / one transcript)

Classify each observation into Smith's black-box acceptance taxonomy:

### P0 — platform-correctness (orchestration mechanics)
Objective/deterministic. For a v2 finding these are already computed in
`driver.platformChecks` — confirm they are green:
- auth accepted; blueprint offered; project created (201); a multi-agent team
  assembled; run accepted (201); events flowed with no `run.failed`; the outcome
  spec left the `drafting` state and became fetchable.
For a live transcript, verify the same mechanics from the turn HTTP statuses:
every driving action returned a success status, the spec settled, and (for the
LLM-in-the-loop model) the **mandatory pushback happened at least twice**. In
v1.1 transcripts the driver computes this objective block for you as
`p0Objective`, where `pushbackCount` means **successfully applied** pushbacks
(not just raw attempts), `pushbackRequirementMet` reflects those successful
applications, and each `revise-spec` turn includes `objectiveRevision`
(`postAccepted`, `specReachedSettledState`, `specChanged`, `appliedSuccessfully`).
P1 still requires you to verify the pushbacks were grounded in real returned
content and that each successful `revise-spec` actually improved the spec.

A P0 failure is a genuine platform regression — file it.

### P1 — output-quality (is the produced content actually good?)
Subjective — **this is your job, not the driver's.** Read the FULL captured
content (for v2: `evidence.outcomeSpec` verbatim; for a transcript: the drafted
spec in each `get-spec` turn, and how it changed after each pushback). Compare it
against the persona's authored "Success looks like" criteria and the `judgeInputs`
/ `judgeContext` reference data (e.g. Priya's expected ticket IDs, the known
4821↔4822 duplicate pair). Ask, for the specific persona:
- Did the plan actually cover everything the persona needs (no dropped items)?
- Did it commit to the specific capabilities the persona cares about (e.g. Priya:
  per-ticket severity with rationale, duplicate flagging, owning team, internal vs
  customer-facing separation; Jordan: idea→app→container→deploy arc, owns
  verification, only asks for the decisions he must make)?
- For live transcripts: did the system **actually improve in response to each
  pushback**, or did it deflect / regress / ignore it? Quote the before/after.

Render P1 as PASS / FAIL / PARTIAL with **specific evidence quotes**. Never pass
on a keyword match; never fail on a stylistic nit.

### CANNOT_DETERMINE
Genuinely unobservable through the captured surface (e.g. kernel isolation
internals, the exact model used, whether a downstream rung would succeed when the
run was bounded before it). Mark explicitly — do NOT force a pass/fail.

---

## Layer 2 — Meta-aggregation (across a BATCH of runs)

Per-run judgment in isolation is noisy: one draft may be good or bad by luck of the
draw. The higher-signal judgment comes from reading **all** the runs/transcripts
from a session or day **together** and cross-referencing them (this mirrors the
technique in `decisions/inbox/tank-persona-brief-pivot.md`). After N runs, produce:

1. **Invariants** — behaviours that held in EVERY run (e.g. "the coordinator always
   grouped 4821+4822 once asked", "a team always assembled with ≥2 members"). These
   are candidate **P0 platform-correctness facts** / system guarantees. A future
   regression in an invariant is a high-confidence bug.
2. **Divergences** — behaviours that VARIED run-to-run (e.g. "sometimes severity
   rationale was included in the first draft, sometimes only after a pushback").
   These map the **judgment-call space** — they are NOT fixed rules, and are the
   main **P1 output-quality signal**: inconsistency is itself a finding worth
   raising even when each individual run is arguably acceptable.
3. **Capability / tool gaps** — things personas repeatedly tried or wanted that the
   API/product does not support well (e.g. "no way to attach a file batch", "no
   lever to separate internal vs customer output"). File as feature gaps.
4. **Drift** — places where actual system behaviour did NOT match what a persona's
   brief reasonably assumed (the product surprised the user). File as bugs or docs
   gaps.

Cite specific runs/transcripts (by filename + turn number) for every claim.

### Clean-run criteria
Do not declare a scenario "flawless" on a single green run. Require **two
consecutive clean runs** (all P0 green, P1 PASS, no unexplained divergence) before
treating a scenario as a trusted regression guard.

---

## Output format (what to hand back)

For a per-run judgment:
```
RUN: <finding|transcript filename>
PERSONA / SCENARIO: <...>
P0 platform-correctness: PASS | FAIL   (+ evidence)
P1 output-quality:       PASS | PARTIAL | FAIL   (+ quoted evidence vs criteria)
CANNOT_DETERMINE:        <list, or "none">
Pushback (live only):    <n> pushbacks; each addressed? (quote before/after)
Filed work:              <issue titles, if any>
```

For a meta-aggregation:
```
BATCH: <N runs, date range>
Invariants:     <bulleted, each cited>
Divergences:    <bulleted, each cited>
Capability gaps:<bulleted, each cited>
Drift:          <bulleted, each cited>
Clean-run status per scenario: <scenario -> n consecutive clean runs>
```

---

## Why this exists (contract with the driver)

The driver **must not** embed subjective quality heuristics — such author-written
checks can't anticipate every valid variation and silently mask regressions. So the
driver hard-fails only on deterministic facts (HTTP status, structural/schema
validation like `WorkflowDefinitionLoader.Load` and the issue-#311 reserved-role
denylist), captures everything else verbatim, and leaves the quality call to you.

The harness now **assembles** the judge prompt but still does not *call* an LLM
(no keys, no network): `lib/judge.mjs <transcript>` packages the captured evidence
+ this playbook + the persona's authored `specs/personas/*.md` criteria into a
single prompt you (a real LLM — this conversation, the coordinator, or a future
automated step) consume to render the Layer-1 verdict; it asks you to emit a
machine-readable verdict block (`agentweaver.persona-judge-verdict/v1`).
`lib/meta-aggregate.mjs verdicts/` then performs the Layer-2 cross-run synthesis
over a batch of those verdict blocks. An automated LLM-*calling* step (piping the
assembled prompt to a model and capturing its verdict) is still future work.

---

## Forward-looking: the same architecture at the chat layer (NOT built now)

Today's harness is **API-only** — there is no conversational chat surface being
driven, because the MCP server and the Console chat aren't hardened/tested yet
(that is separate future work, out of scope for issue #1). When those surfaces are
hardened, the **same** persona-brief → live turn-by-turn driving → mandatory ≥2
pushback → meta-aggregation architecture should be **re-applied at the chat layer**:
the driving LLM would then exchange real conversational turns through MCP / the
Console (text in, text out, as in the original blog technique) instead of raw API
calls, and "pushback" would be a chat message rather than a `revise-spec`/`steer`
lever. The judge taxonomy (P0/P1/CANNOT_DETERMINE) and this two-layer method carry
over unchanged. **Do not build this now** — it is noted only so the design stays
coherent when the chat surface is ready.
