# Agentweaver API Test Harness Plan

_Last updated: 2026-07-14 — author: Tank (Backend Engineer)_

> **Status: design spec.** Unlike the UI and MCP specs, the harness this document
> describes **already exists and runs** — it is `scripts/persona-harness/`, the
> primary API-driven E2E track for issue #1, live-verified across three personas
> (Priya, Jordan, Maya) against staging. This spec brings that harness into the
> **three-harness shared architecture** that Trinity (`docs/ui-test-harness-plan.md`)
> and Morpheus (`docs/mcp-test-harness-plan.md`) converged on, and specifies the
> migration from today's local `briefs/` + `lib/judge.mjs` to the shared
> `scripts/persona-briefs/` + `scripts/harness-judge/` packages.
>
> **This document supersedes the harness-architecture description in
> `docs/e2e-harness-plan.md`.** That older plan predates the three-harness split and
> the full self-improvement vision; its autopilot/Squad-dispatch operating rules,
> release cadence, and methodology still stand and are unchanged. Only the
> *harness-architecture* portion is superseded here (a one-line pointer has been
> added to that file's Workstream 1).

---

## The full vision — a self-improvement feedback loop, not three test suites

The three persona harnesses (API, UI, MCP) are **not** independent test suites. Together
they form a **self-improvement feedback loop** whose purpose is to **replace manual
bug-hunting** — today's loop of Ahmed launching the app and reporting bugs by hand, or
the coordinator running ad hoc API calls that must be re-described each session. For that
loop to become autonomous, **all three stages of the pipeline must be LLM/model-driven**,
not just the middle one:

1. **Persona generation** (the *inputs*) — personas must be **LLM-generatable on demand**
   (new Jobs-To-Be-Done variations), not limited to the hand-authored jordan/maya/priya
   set. See [Cross-Harness Shared Layer §1](#1-shared-persona--brief-format--define-personas-once-surface-agnostically).
2. **Persona behavior** (the *driving*) — already covered by this harness's
   LLM-in-the-loop, turn-by-turn API-action selection: a fresh-context LLM decides each
   next API call live from the **real responses** it has seen, and must push back **≥2
   times** grounded in real content. See [Architecture](#architecture).
3. **Judging** (the *evaluation*) — the shared judge must render more than P0/P1
   pass/fail; it must also assess a **frustration level** (an emotional/UX signal) from
   the transcript evidence. See [Cross-Harness Shared Layer §2](#2-judge-architecture--one-shared-judge-core--thin-api-evidence-adapter-option-a).

This matches, word-for-word in intent, the "full vision" framing in the UI and MCP specs.
The three harnesses drive the **same** personas through **different surfaces** and feed a
**shared judge**, so the same Jordan/Maya/Priya scenario is comparable across API vs UI
vs MCP.

---

## Division of responsibility across the three surfaces

Make this explicit so the harnesses don't overlap or contradict:

| Harness | Surface | Drives via | Directory | Layer |
|---|---|---|---|---|
| **API harness** (this spec, **exists**) | Backend REST lifecycle | bearer-token HTTP calls | `scripts/persona-harness/` | **ground truth** |
| **UI harness** (Trinity) | Web UI | Playwright browser | `scripts/ui-persona-harness/` | experience |
| **MCP harness** (Morpheus) | MCP protocol / tool-call surface | MCP client | `scripts/mcp-persona-harness/` | experience |

**The API harness is the ground-truth layer.** It tests **core backend functionality in
isolation**, through JSON, with **no UX/usability layer**. Its question is *"does the
platform actually work"* — did the orchestration mechanically do the right thing (auth
accepted, project created, team assembled, run accepted, events flowed, outcome spec
settled, pushbacks applied, no `run.failed`), independent of any client's ergonomics.
Because it reads structured payloads rather than a rendered surface, it is fast,
deterministic, CI-friendly, and free of any "is this findable / clear / frustrating"
concern — those belong to the experience-layer harnesses.

**How this differs from and complements the UI/MCP harnesses.** The UI and MCP harnesses
primarily test the **experience layer** — *usability, discoverability, and frustration*
for a browser user or an MCP client — not merely "did the call return 2xx." The API
harness deliberately has no such layer: it is the reference against which an
experience-layer symptom is attributed to a cause.

**Meta-aggregation cross-references the layers.** Because all three harnesses emit the
**same** canonical verdict schema into one shared verdict pool, the shared
`meta-aggregate` rolls them up **per persona/scenario across surfaces** and distinguishes
a *real backend bug* from a *UX-only issue*:

- A UI/MCP P1-or-frustration finding that **co-occurs with an API-harness P0 fail** for
  the same persona/scenario is a **backend root cause** surfacing as bad UX — the ground
  truth confirms the platform actually misbehaved.
- A UI/MCP frustration finding with a **clean API-harness run** (P0 PASS, P1 PASS) for the
  same persona/scenario is a **genuine experience-layer defect** living in the surface
  itself — the backend did the right thing; only the surface made it hard.

This cross-reference is exactly what the shared verdict schema + cross-surface
meta-aggregation makes possible, and it is the reason the API harness stays deliberately
narrow: its objective P0 checks exist not only to prove the backend correct on their own,
but to **attribute** the other harnesses' experience findings to a layer.

---

## Cross-Harness Shared Layer

> This is the section Ahmed asked all three harness specs to converge on. The
> recommendations below are written to be **identical** across API, UI, and MCP, so we
> end up with **one** set of personas and **one** judge, not three. Where the three
> current specs disagree on a detail, that divergence is flagged explicitly at the end of
> this section for the coordinator to reconcile **before** the shared packages are
> extracted.

### 1. Shared persona / brief format — define personas ONCE, surface-agnostically

**Recommendation:** each persona is defined **once** in a new shared package
`scripts/persona-briefs/`, surface-agnostic, and each harness drives that same persona
through a thin per-surface adapter. Do NOT duplicate or re-adapt briefs per harness.

```
scripts/persona-briefs/            SHARED — surface-agnostic single source of truth
  package.json                     Zero heavy deps; imported by all three harnesses
  personas/
    priya.md                       Persona CORE — identity, goal, voice, constraints,
    jordan.md                      the mandatory >=2-pushback rule, and the authored
    maya.md                        "Success looks like" / "Failure signals" criteria.
    ...                            NOTHING surface-specific (no "curl", no "click", no tool name).
  surfaces/
    priya.api.md                   Per-surface ADAPTER — how THIS persona's intent maps to
    priya.ui.md                    the surface's actions ONLY (API: "submit-goal" / "revise-spec";
    priya.mcp.md                   UI: composer paste; MCP: the tool the persona reaches for). Thin, additive.
    ...
  generate-core.mjs                LLM PROMPT ASSEMBLER — packages a target JTBD/domain + an
                                   exclusion list of existing archetypes so an LLM proposes a NEW
                                   persona core in the personas/*.md shape. Does not call a model itself.
  generate-adapter.mjs             LLM PROMPT ASSEMBLER — given a persona core + a target surface,
                                   assembles a prompt for an LLM to propose that surface's adapter.
  index.mjs                        Resolves persona core + optional surface adapter for a harness
```

- The **persona core** carries everything that must be identical across surfaces — who
  they are, what they want, their voice, their low-tolerance triggers, and the ≥2-pushback
  requirement. This is what makes "Jordan via API" and "Jordan via UI" the **same Jordan**.
- The **surface adapter** is thin and additive: it only says how that persona's intent
  expresses itself on that surface. For the API harness the adapter maps the abstract
  levers — *propose / inspect draft / push back / (optionally) confirm* — onto the
  concrete REST actions `submit-goal → get-spec → revise-spec (the pushback lever) →
  confirm-spec`. A persona with no `.api.md` adapter simply isn't run on the API surface.
- **Personas are LLM-generatable on demand, not only hand-authored.**
  `scripts/persona-briefs/` is a **generator-and-store**, not just a store. The API
  harness already ships a working `lib/generate-brief.mjs` (assemble a prompt, never call
  a model, no keys/network — the "architect, not caller" pattern). That module is the
  concrete seed for `generate-core.mjs`: given a seed (a JTBD theme, a discipline, a
  capability/seam to stress, or "a plausible new-user variation of Jordan") it prompts a
  model to synthesize a **new** surface-agnostic persona core conforming to the canonical
  shape. Generated cores are the same shape as hand-authored ones, so all three harnesses
  drive them with **zero code changes**; jordan/maya/priya become the seed corpus, not the
  ceiling. This is what makes pipeline stage 1 model-driven.

**Migration of the API harness's existing briefs (specific plan).** The API harness owns
three hand-authored briefs today — `scripts/persona-harness/briefs/{jordan,maya,priya}.md`
— each already **surface-agnostic in spirit** (Jordan's brief talks about "get idea → app
→ container → deploy" and "push back ≥2 times", never about REST specifically) but
physically living inside the API harness and referencing "the real Agentweaver API". They
migrate as follows, as a **coordinated move, not an out-of-band edit** to in-flight files:

1. **Lift the core.** Move each `briefs/<name>.md` into
   `scripts/persona-briefs/personas/<name>.md`, keeping identity/goal/voice/constraints and
   the mandatory ≥2-pushback rule verbatim. These already reference `specs/personas/<name>.md`
   for the authored "Success looks like" criteria; that link is preserved unchanged.
2. **Peel the API phrasing into an adapter.** Any REST-specific wording (the mention of
   "the real Agentweaver API", the implicit `submit-goal/revise-spec` levers) moves into a
   new thin `scripts/persona-briefs/surfaces/<name>.api.md`. The core becomes surface-neutral.
3. **Re-point the API harness.** `agent-driver/tools.mjs` currently resolves briefs from
   `BRIEFS_DIR = join(HERE, '..', 'briefs')`; it changes to resolve the core + `.api.md`
   adapter via `scripts/persona-briefs/index.mjs`. No driver *logic* changes — only the
   resolution path.
4. **Seed, don't cap.** The migrated three are the exemplar corpus; new cores arrive
   LLM-generated via `generate-core.mjs`.

Until the extraction lands, the API harness keeps using its local `briefs/` unchanged
(it is the currently-running production track); the move happens at a safe checkpoint
(see [Rollout](#rollout--migration-plan)), never as a concurrent edit while the harness is
mid-flight.

### 2. Judge architecture — ONE shared judge core + thin API evidence adapter (option a)

**Recommendation: option (a)** — a single shared judge core (one prompt library + one
canonical verdict schema + the JUDGE.md methodology) with thin per-surface **evidence
adapters** (API call/response + events, UI DOM/screenshot/console/network, MCP
protocol/tool-call). **NOT** three separate judges.

```
scripts/harness-judge/             SHARED — extracted from the API harness's lib/judge.mjs
  package.json
  JUDGE.md                         Canonical methodology: P0 objective / P1 subjective /
                                   CANNOT_DETERMINE, pushback rules, FRUSTRATION rubric, two-layer
                                   (per-run + meta-aggregation). Surface-neutral core + short
                                   per-surface appendices (JUDGE.api.md / JUDGE.ui.md / JUDGE.mcp.md).
  core.mjs                         Assembles the judge prompt from persona core + authored criteria +
                                   run metadata + a normalized EVIDENCE bundle. Emits the canonical
                                   verdict schema agentweaver.persona-judge-verdict/v1 (P0 + P1 +
                                   REQUIRED frustration).
  verdict-schema.mjs               The canonical agentweaver.persona-judge-verdict/v1 contract.
  meta-aggregate.mjs               Cross-run + CROSS-SURFACE rollup (moved from the API harness).
  adapters/
    api.mjs                        API transcript -> normalized evidence (calls, bodies, events, outcome spec)
    ui.mjs                         UI transcript  -> normalized evidence (DOM, screenshot ref, console, network)  [Trinity]
    mcp.mjs                        MCP transcript -> normalized evidence (tool calls, protocol frames)            [Morpheus]
  test/
    core.test.mjs
    adapters.*.test.mjs
```

Each adapter's only job is to turn its surface's raw transcript into the **same normalized
evidence shape**; `core.mjs` then does the identical judging regardless of surface and
always emits the **one** canonical verdict schema.

**The API harness already built most of this — it is promoted, not thrown away.** The
existing `scripts/persona-harness/lib/judge.mjs` is already:

- a **prompt assembler that never calls a model itself** (no keys, no network — it
  packages a captured transcript + `JUDGE.md` + the persona's authored criteria into one
  prompt a real LLM consumes);
- emitting a verdict schema that is **already surface-agnostic** —
  `{p0, p1, pushback, cannotDetermine, findings}`, where **P0** = objective orchestration
  mechanics (surface-independent facts about the orchestration) and **P1** = content
  quality vs the persona's authored criteria (judged from the drafted outcome-spec bytes,
  identical regardless of surface);
- already paired with a working `lib/meta-aggregate.mjs` (invariants / divergences /
  recurring findings across a batch).

So the extraction is a **promotion**: `lib/judge.mjs` becomes `harness-judge/core.mjs` +
`verdict-schema.mjs`; `lib/meta-aggregate.mjs` moves to `harness-judge/meta-aggregate.mjs`;
the API harness contributes `adapters/api.mjs` (the normalizer for its own transcript
shape) and a `JUDGE.api.md` appendix; the surface-neutral methodology in `JUDGE.md` stays
one file. The API harness then **consumes** the shared core instead of its local copy. No
judging logic is discarded — the API harness is the **seed** of the shared judge.

**Why (a) over three judges:** consistent P0/P1 meaning across surfaces (three judges
would drift); cross-surface meta-aggregation *requires* one schema (it is the whole point
of running three harnesses); lower maintenance (methodology written/tested once); surface
nuance preserved in short appendices without forking the core. This is identical to the
reasoning in Trinity's and Morpheus's specs.

### 3. Verdict schema — P0, P1, AND a required frustration dimension

Judging is not just pass/fail. The canonical `agentweaver.persona-judge-verdict/v1` schema
gains a **required `frustration` dimension** — an emotional/UX assessment the judge renders
**from the transcript evidence**, alongside the existing P0 (objective mechanics) and P1
(subjective quality) blocks. A run can be **P0-green and P1-PASS yet frustrating** (the
persona got there, but only after fighting the surface); that frustration must not be lost
in a binary verdict. Because the field is **shared**, frustration is directly comparable
API-vs-UI-vs-MCP for the same persona in meta-aggregation.

```jsonc
{
  "schema": "agentweaver.persona-judge-verdict/v1",
  "persona": "jordan",
  "surface": "api",                       // api | ui | mcp — which harness produced the evidence
  "p0": { "verdict": "PASS | FAIL", "evidence": "..." },
  "p1": { "verdict": "PASS | PARTIAL | FAIL", "evidence": "...", "criteriaCoverage": [ ] },
  "frustration": {                         // REQUIRED — emotional/UX assessment from evidence
    "level": "none | low | moderate | high | abandoned",   // ordinal; "abandoned" = persona gave up
    "score": 0,                            // 0-4 mirror of level, for meta-aggregate trend math
    "signals": [                           // OBSERVED evidence the level is grounded in (never invented)
      { "kind": "<signal>", "evidence": "<transcript turn refs / quote>" }
    ],
    "rationale": "<one line: why this level, tied to the signals above>"
  },
  "pushback": { "count": 0, "requirementMet": true, "each": [ ] },
  "cannotDetermine": [ ],
  "findings": [ ]
}
```

- **`frustration` is REQUIRED** (never omitted). If the evidence genuinely can't support a
  read, the judge emits `level: "none"` with an empty `signals` array and says so in
  `rationale` — it is never guessed.
- **It is the judge's call from evidence, not a driver heuristic.** The API driver does
  NOT compute a frustration score (that would be exactly the embedded subjective heuristic
  the driver/judge split forbids). The driver only **captures the raw signals** into the
  transcript; the judge reads them and assigns the level.
- **API-surface frustration signals** the API evidence adapter surfaces for the judge to
  weigh (illustrative, not a scoring formula): a high ratio of non-2xx responses forcing
  retries; the persona having to chain many calls where one path should exist; repeated
  `get-spec` polls with no progress (a spec that never settles); a pushback that the
  system did **not** meaningfully respond to (the re-drafted spec came back unchanged, so
  the persona had to object again); visible confusion in the persona's own `--thought`
  reasoning ("this still doesn't include the deploy step I asked for"); the persona
  abandoning the run before its goal (→ `abandoned`); long unexplained backend latency
  (`upstreamMs`) with no state change.
- **Frustration is a secondary output for the API harness, by design.** Because the API
  harness is the ground-truth layer, its most important outputs are P0 (did the mechanics
  work) and P1 (was the produced content good). Its frustration read is real but is used
  mainly to **anchor** the experience-layer harnesses' frustration reads: if Jordan is
  `abandoned` via UI but `low` via API for the same scenario, that pinpoints a
  browser-experience defect with a working backend; a persona frustrated on **every**
  surface points at a core product/model problem.

### Judge evidence sources (applies to the shared judge)

The judge must not reason from the raw transcript **alone** — it cross-references what an
API call *claimed* happened against what *actually* happened server-side. The shared judge
relies on **all** of:

- **API responses / event payloads** — the API harness's existing strength. Every turn is
  captured with the verbatim request + response body, and multi-poll tools (`get-spec` /
  `revise-spec`) persist **every** poll attempt inside the turn. The full raw
  `GET /api/runs/{id}/events` body is captured verbatim, plus per-call `ms`,
  `upstreamMs` (`x-envoy-upstream-service-time`), and the drafted outcome spec.
- **Application Insights + cluster (`kubectl`) logs** — the ground truth of what the
  backend actually did, correlated to the transcript by **`run_id` and `trace_id`**. This
  lets the judge catch **claim-vs-reality drift** — a call returned success but the backend
  logged a silent failure, or a `preview_url` was reported that never served traffic —
  which the transcript alone cannot show. The correlation queries are the ones already in
  `docs/e2e-harness-plan.md` (App Insights transaction search on the run's
  correlation/session ID; `kubectl logs -n agentweaver <pod>`).

**What's captured today vs. what's missing for log correlation (honest audit of
`lib/client.mjs` + `agent-driver/tools.mjs`):**

- **`run_id` — captured and sufficient.** `submit-goal` records `session.runId` from the
  `run_submit` response, and every subsequent turn carries it; all lifecycle calls key off
  it. Log correlation **by `run_id` and time-window works today** (it is exactly how the
  ad-hoc validation in `e2e-harness-plan.md` already correlates).
- **`trace_id` — plumbed but currently empty (the gap).** `lib/client.mjs` already scans
  response headers for a correlation id (`traceparent`, `request-id`, `x-request-id`,
  `x-correlation-id`) into `ApiCall.traceId`, and every turn records it. **But the deployed
  staging backend emits none of these on `/api/*` responses** (only istio-envoy proxy
  headers), so `traceId` populates as `null` today. Correlation therefore falls back to
  `run_id` + time-window, which is coarser (it can't pin a *single* API call to a *single*
  App Insights transaction). **What's missing to fully support later `trace_id`
  correlation is a backend change:** emit a W3C `traceparent` (or a stable correlation
  header) on API responses. That is a small, additive backend seam I own — file it as an
  observability follow-up so the harness's already-present capture path starts populating.
  Log pulls remain **best-effort**: if App Insights/kubectl are unavailable, the judge
  proceeds on transcript + response evidence and marks unverifiable claims
  `CANNOT_DETERMINE` rather than guessing.

### Driver-must-not-debug boundary

**One explicit rule, identical across all three harnesses:** the driver's LLM-in-the-loop
role is **exclusively to choose the persona's next API call** based on the brief + the
responses it has observed (it *simulates the user*). It **never** diagnoses why something
failed, classifies a root cause, or decides whether a failure is "really" broken, "a
backend problem", or "just slow". **All** interpretation, debugging, root-cause
attribution, and real-vs-not judgment is **exclusively the judge's job**, working from the
evidence bundle (responses/events + App Insights/kubectl) the driver hands off. When a run
misbehaves, the driver's LLM reacts **as the persona would** (retry, push back, get
confused, or abandon) and records that reaction verbatim — it does not step out of
character to analyze the platform.

**Self-audit of the existing API-harness code against this boundary (result: preserved,
no fix needed):**

- **`agent-driver/tools.mjs`** — the discrete tools only DRIVE the real API and RECORD
  verbatim; there is deliberately **no** `confirm` tool on the scoping rung and **zero**
  embedded pass/fail quality heuristics. Boundary intact.
- **`lib/approvals.mjs`** — pure **deterministic detection**: it parses the real events
  feed and reports, structurally, which tool/shell/coordinator-child gates are *pending*.
  It renders **no** opinion on whether any gate should proceed. Boundary intact.
- **`lib/approval-judge.mjs`** (the approval-driving feature built in `b4ac1104`) — the
  most boundary-sensitive code, and it is correct: the driver only **packages** the gated
  facts + surrounding evidence into a prompt and **executes exactly** what a *pluggable
  judge* returns. With no judge wired, the default is **DEFER** (never blind-approve) — the
  safe driver-only default. The driver invents no approve/deny itself. Boundary intact.

The audit found **no** place where the boundary is blurred; the approval-driving work was
built to preserve it from the start (zero heuristic judgment in the driver, judgment
delegated, absence-of-judgment ≠ approval).

### Inconsistencies found across the three specs (flagged for the coordinator)

Reading Trinity's `docs/ui-test-harness-plan.md` and Morpheus's
`docs/mcp-test-harness-plan.md` in full, the shared-layer **intent** is unanimous (one
persona-briefs package, one judge core, one canonical `agentweaver.persona-judge-verdict/v1`
schema with a required frustration dimension, driver-only everywhere). But the three specs
**disagree on concrete names/paths**, and Trinity and Morpheus disagree with *each other*,
so I cannot simply "match theirs." This spec adopts the split the **task itself** and
**Trinity** specify (two packages), and flags the rest for reconciliation **before** Phase 2
extraction:

1. **Judge package location — genuine conflict.** Trinity puts the judge in a **separate**
   package `scripts/harness-judge/` (`core.mjs`, `verdict-schema`, `meta-aggregate`,
   `adapters/`). Morpheus folds the judge **inside** persona-briefs at
   `scripts/persona-briefs/judge/` (`JUDGE.md`, `verdict-schema.mjs`, `assemble.mjs`,
   `meta-aggregate.mjs`). This spec follows Trinity + the task directive (a separate
   `scripts/harness-judge/`). **Coordinator must pick one** before extraction.
2. **Persona directory name — genuine conflict.** Trinity uses
   `scripts/persona-briefs/personas/*.md` (core) + `scripts/persona-briefs/surfaces/*.<sfx>.md`
   (adapters). Morpheus uses `scripts/persona-briefs/briefs/*.md` (no separate surfaces
   dir). This spec follows Trinity (`personas/` + `surfaces/`), which cleanly expresses the
   core/adapter split. **Reconcile the directory name.**
3. **Evidence-adapter location — conflict.** Trinity centralizes adapters under
   `harness-judge/adapters/{api,ui,mcp}.mjs`. Morpheus keeps a per-harness local
   `lib/evidence-adapter.mjs`. This spec follows Trinity (centralized `adapters/`). Falls
   out of decision (1).
4. **Generator entry-point name — minor.** Trinity: `generate-core.mjs` +
   `generate-adapter.mjs`. Morpheus: `generate/generate-brief.mjs` + `brief-schema.mjs`.
   The API harness's existing seed is `lib/generate-brief.mjs`. Cosmetic; align on one.
5. **Frustration sub-schema shape — minor field drift.** Trinity's `frustration` carries
   `{ level, score (0-4), signals:[{kind,evidence}], rationale }`. Morpheus's carries
   `{ level, evidence, signals:[<string>] }` (no numeric `score`, flatter signals). This
   spec adopts **Trinity's richer shape** (numeric `score` is needed for meta-aggregate
   trend math, and `{kind,evidence}` signals are more auditable). **Reconcile the signal
   shape and the `score` field** so all three emit byte-comparable frustration blocks.

The `level` ordinal (`none | low | moderate | high | abandoned`), the schema id
(`agentweaver.persona-judge-verdict/v1`), the P0/P1 semantics, and the driver-only rule are
**consistent** across all three specs — no conflict there.

---

## Architecture

The API harness already implements the brief-driven, LLM-in-the-loop, mandatory-pushback,
driver-only, judge-separated shape; this section documents it as-built.

### 1. How a persona brief drives API calls, turn by turn

- A fresh-context LLM (a sub-agent, or any model with shell access) is handed **only** a
  persona **brief** (goals, voice, constraints, the mandatory-pushback instruction — the
  same brief the UI and MCP harnesses use) plus its API surface adapter.
- Each **turn = one API action**, chosen by the driving LLM from the brief + the **real
  responses** seen so far — never a pre-written both-sides script. `agent-driver/tools.mjs`
  exposes the discrete tools: `init`, `list-blueprints`, `create-project`, `get-team`,
  `submit-goal`, `get-spec`, `revise-spec` (the pushback lever), `get-events`,
  `check-approvals`, `resolve-approval`, `finish`. Each hits the real API and records the
  turn verbatim.
- **Pushback** = the LLM, having read a real drafted outcome spec, decides the persona
  would object and issues a real `revise-spec` carrying the objection — not free-text chat.
  Mandatory **≥2 grounded pushbacks** per run.
- **Two rungs, matching the other harnesses' safety model:**
  - **Scoping rung (default, safe):** drive to the `confirm-spec` gate and **stop** —
    there is deliberately **no `confirm` tool**, so nothing is scaffolded, containerized,
    or deployed. This is the fast, deterministic, CI-friendly path.
  - **Deeper rung (opt-in, flagged):** confirm the spec and drive through dispatch → work
    plan → in-loop approvals (`check-approvals` / `resolve-approval`) → preview → completion
    → artifacts. Behind an explicit flag, and subject to the **mandatory live `curl` of
    `preview_url` before approving** rule carried over from `e2e-harness-plan.md`.

### 2. Driver-only evidence capture

Same hard rule as the other two harnesses: **the driver captures and executes; it never
judges.** Each turn is recorded verbatim into a transcript with a stable shape
(`TranscriptTurn`): `n`, `at`, `sessionId`, `traceId`, `actor`, `thought` (the persona's
live intent), `action`, verbatim `request`/`response` (with `latencyMs`/`upstreamMs`), a
coarse `outcome`, and a human-readable `note`.

Deterministic facts the **driver** may assert (objective, not quality judgments): every
driving call returned 2xx; the outcome spec left `drafting` and settled; the **mandatory
≥2 pushbacks** were each **applied** (the revise succeeded and the spec actually changed);
structural/schema validation (the `WorkflowDefinitionLoader.Load` mirror — dangling edges /
unrouted check branches — and the #311 reserved-role denylist). Everything subjective —
did the spec cover the persona's needs, did the system improve per pushback — is captured
verbatim and left to the judge.

### 3. Approval-driving (already implemented, commit `b4ac1104`)

The harness closes the gap where runs stalled waiting on approvals, with a
**DETECT → JUDGE → EXECUTE** loop that drives tool/shell/coordinator-child approval gates
via the real API **only after a judge decides**. As-built:

- **`lib/approvals.mjs`** — deterministic gate detection off the real
  `GET /api/runs/{id}/events` feed (the authoritative per-run signal, which also carries
  the exact `requestId` / `command_hash` needed to resolve a gate). Event vocabulary:
  `tool.approval_required`, `coordinator.child_approval_required`, `shell.approval_required`
  (and their `*_resolved` counterparts).
- **`lib/approval-judge.mjs`** — the narrow, in-the-loop approve/deny/defer judge contract:
  a pluggable judge (mock in tests, an LLM CLI via `AGENTWEAVER_APPROVAL_JUDGE_CMD`, or a
  human passing an explicit decision). **Default = DEFER** — never blind-approve.
- **New driver commands** `check-approvals` and `resolve-approval`, plus an **optional
  `driveApprovals` runner hook (OFF by default)**.
- **Full audit trail:** `turn.approval` transcript turns + an `evidence.approvalDecisions`
  record capturing the prompt + judged decision + the concrete API call for each gate.

Driver-only boundary preserved throughout: zero heuristic judgment in the driver.
Validated at **62/62 tests passing (22 new)**.

**Backend follow-up filed — #321** (`Notifications: emit reserved 'tool_approval' type`).
`GET /api/notifications` only surfaces `human_review` today; its `tool_approval` type is
reserved and unemitted, so it cannot detect the in-the-loop tool/shell gates at all. The
harness therefore drives off the run events feed (which *is* authoritative and race-free),
so **no backend change is required for the harness to work** — but emitting the
`tool_approval` notification type would let the notification surface (and the UI harness)
see these gates too. #321 tracks that gap.

---

## Directory / File Layout (as-built, with the shared-layer target)

```
scripts/persona-briefs/            SHARED (target) — persona cores + per-surface adapters
scripts/harness-judge/             SHARED (target) — judge core + canonical schema + meta-aggregate + adapters
scripts/persona-harness/           THIS harness (API-specific driver + evidence) — EXISTS TODAY
  README.md                        why API-driven first, driver/judge split, the two rungs
  package.json                     Node ESM; dep: yaml (dependency-light, no browser)
  agent-driver/
    tools.mjs                      LLM-in-the-loop DRIVER tool surface (the discrete API tools)
    AGENT.md                       driving-LLM preamble (implicit today; formalize on migration)
  briefs/                          jordan.md maya.md priya.md — MIGRATE to scripts/persona-briefs/personas/ + surfaces/*.api.md
  lib/
    client.mjs                     thin bearer-token REST client; captures traceId/upstreamMs
    approvals.mjs                  deterministic approval-gate detection (driver-only)
    approval-judge.mjs             in-the-loop approve/deny/defer judge contract (pluggable; default DEFER)
    judge.mjs                      end-of-run P0/P1 prompt assembler  -> PROMOTE to harness-judge/core.mjs
    meta-aggregate.mjs             cross-run rollup                    -> MOVE to harness-judge/meta-aggregate.mjs
    generate-brief.mjs             LLM brief-prompt assembler          -> SEED of persona-briefs/generate-core.mjs
    runner.mjs, reporter.mjs, persona.mjs, metrics.mjs, seams.mjs, generation-checks.mjs
  scenarios/                       fixed-script fallbacks (priya-ticket-triage, jordan-blank-to-plan, generated-artifacts-seam)
  JUDGE.md                         methodology  -> becomes the surface-neutral core + JUDGE.api.md appendix
  transcripts/ verdicts/ findings/ captured output (git-ignored)
```

Deliberate parallels to the sibling harnesses (`agent-driver/` ↔ `agent-driver-ui/` /
MCP `agent-driver/`; `reporter.mjs` ↔ `reporter-ui.mjs`) are kept so a reader who knows
one harness can navigate the others; personas and judge logic become **imported from the
shared packages**, never copied.

---

## Driver performance / interaction model

**Parallelism-first, autonomous, low-touch — identical to the other two harnesses.** The
API driver is built to run **many persona/scenario runs concurrently**, unattended, with
optional observability for Ahmed rather than any required interaction:

- **Autonomous / low-touch.** The coordinator dispatches N persona sessions as background
  agents (mirroring the E2E plan's "parallelize as much as possible" rule). A bearer token
  is resolved once (`gh auth token`) and reused; no per-run human step on the scoping rung.
- **Optional observability, never required.** Ahmed can tail a live transcript / stream
  turn-by-turn stdout if he wants to watch a run, but observation is never a gating
  interactive step; the default is unattended fan-out. (Each driver tool already prints the
  real API response to stdout, so a live tail is a read of existing output, not new work.)

**What must change in the session model to support N concurrent sessions safely (concrete
audit of `agent-driver/tools.mjs`):**

- **Session-file collision — the one real blocker, and it is bounded.** The driver
  persists per-run state to a **single fixed path**:
  `SESSION_PATH = join(HERE, 'session.current.json')`. This exists so the LLM can call each
  tool as a separate shell invocation and keep context across calls. But it means **two
  concurrent sessions on the same machine clobber each other's state** (project id, run id,
  pushback counters, resolved-approval keys). To run N sessions safely this must become a
  **per-session file**, e.g. keyed by the harness `sessionId` (already a `randomUUID()` set
  at `init`) — `session.<sessionId>.json` — selectable via a `--session <path>` flag or an
  `AGENTWEAVER_HARNESS_SESSION` env var that every tool invocation in a run threads through.
  This is a small, additive change to path resolution in `tools.mjs`; the driver *logic* is
  unchanged. (Transcripts already write per-run files under `transcripts/`, so only the live
  session file needs this fix.)
- **Project isolation — mostly already safe, tighten the name.** Each session already calls
  `create-project` fresh (it does not reuse a shared project), so run/project IDs are
  naturally disjoint. The only caveat is that concurrent sessions must create **uniquely
  named** projects (suffix the persona + `sessionId`/timestamp) so a big concurrent sweep
  doesn't collide on a human-readable project name. No shared mutable backend state is
  otherwise touched by the scoping rung.
- **Client instance isolation — already safe.** Each session constructs its own
  `AgentweaverClient` with its own `calls[]` log and its own bearer; there is no shared
  mutable client state across sessions. One caveat to preserve: `insecure` sets the
  **process-global** `NODE_TLS_REJECT_UNAUTHORIZED`, so if sessions are ever run
  in-process (rather than as separate shell invocations) that flag must be applied
  consistently; the current separate-process model sidesteps it.

Net: the harness is **already** structured for parallel fan-out (per-run transcripts,
fresh project per run, per-session client) **except** for the single fixed
`session.current.json` path — parameterizing that one path is the concrete change needed
for safe N-concurrent operation.

---

## Coverage Mapping (which open issues this harness is positioned to catch/verify)

Consistent with the categorization the coordinator gave Ahmed this session. Each row is a
**brief-driven scenario** (persona + goal + authored success criteria), not a fixed script;
the "Driver P0 captures" column is what the harness hard-checks objectively, the rest is
deferred to the judge.

| Issue | What it is | How the API (ground-truth) harness catches/verifies it | Rung |
|---|---|---|---|
| **#315** | Revision regression — fixing one pushback silently weakens a previously-satisfied requirement | **The harness's core strength.** The mandatory ≥2-pushback loop + cross-run meta-aggregation is exactly what *surfaced* #315 (Jordan, then reproduced in Maya). Driver asserts each pushback was applied; the judge/meta-aggregate detect a criterion that regressed across re-drafts. | scoping |
| **#317** | Coordinator stall-timeout watchdog declares `agent_stall_timeout` after the child already completed (completion-signal race) | Drive a run to dispatch, poll `/api/runs/{id}/children` + `/events`, capture the terminal-signal ordering; driver P0 flags a child that reached completion yet was marked stalled. (Epic #291.) | deeper |
| **#314** | Steer redirect resets all `assemble_ready` subtasks on a stale `ineligible_subtasks` marker (#309 follow-up) | Drive a steer redirect, snapshot child states before/after via `/children`, capture the park reason; driver P0 flags green subtasks reset to pending. (Epic #291.) | deeper |
| **#97** | Opaque `assembly_blocked` RCA — raw persisted reasons + re-arm path | Drive to an assembly block, read the persisted reason off `/events`/`/children`; driver P0 asserts the reason is raw/actionable (not opaque) and the re-arm path is observable. | deeper |
| **#267** | A2A regression — `build_test_infra_a2a_protocol_event_unsupported` at the build/test gate | Drive to the build/test gate on the deeper rung, capture the A2A protocol event verbatim off the event stream; driver P0 flags the unsupported-event failure. (Epic #293.) | deeper |
| **#271** | `POST /api/runs/{id}/retry` mints a new run_id and cold-starts, discarding prior progress | Drive a run, call retry, compare `run_id`/artifacts/worktree before vs after; driver P0 flags discarded progress. (Epic #291, with #240.) | deeper |
| **#240** | Coordinator takeover / durable per-attempt record with fencing | Verify takeover/fencing behavior via the run lifecycle + events; sibling of #271/#242. (Epic #291.) | deeper |
| **#242** | Parked-recovery / terminal-emission ordering | Verify terminal emission and durable resume via the event stream + resume path. (Epic #291.) | deeper |
| **#291** (epic) | Resume from durable progress; recover transient execution failures | The API harness is the **end-to-end ground-truth guard** for the whole epic — #317/#314/#271/#240/#242 all live here. | deeper |
| **#292** (epic) | Reliable, actionable collective assembly review gates | Drive to the assembly review gate; the approval-driving loop (`b4ac1104`) verifies gates raise, resolve, and route correctly. | deeper |
| **#293** (epic) | Fast, isolated AgentHost workspaces + dependable command execution | Ground-truth guard for build/test-gate + command-execution correctness (#267 lives here). | deeper |

Most verification here needs the **deeper rung** (past the confirmation gate), which is
opt-in and flagged; #315 is caught on the fast **scoping rung** because it is a
draft-and-pushback defect that never needs execution.

---

## Rollout / migration plan

**Concretely, what changes to `scripts/persona-harness/` to move from "today's local
`briefs/` + `lib/judge.mjs`" to "consumes shared `scripts/persona-briefs/` +
`scripts/harness-judge/`".** This is a **spec of the migration** — the refactor itself is
NOT performed in this task.

**Phase 0 — spec + convergence (this document).** Land `docs/api-test-harness-plan.md`.
Record the shared-layer recommendation and the flagged inconsistencies (judge package
location, persona dir name, adapter location, generator name, frustration sub-schema) as a
decision so the coordinator reconciles them with Trinity's and Morpheus's specs **before**
anyone extracts the shared package.

**Phase 1 — no-op for the running harness.** The API harness keeps running on its local
`briefs/` + `lib/judge.mjs` unchanged (it is the live production track). No shared-file
edits yet. This phase is purely "don't break what works while the others scaffold."

**Phase 2 — shared-package extraction (coordinated, single sequenced step).** Once the
inconsistencies are reconciled, one coordinated change:

1. **Move personas.** `briefs/{jordan,maya,priya}.md` → `scripts/persona-briefs/personas/`
   (core) + `scripts/persona-briefs/surfaces/{jordan,maya,priya}.api.md` (adapters); peel
   REST phrasing into the adapters (see [§1 migration](#1-shared-persona--brief-format--define-personas-once-surface-agnostically)).
2. **Promote the judge.** `lib/judge.mjs` → `harness-judge/core.mjs` +
   `verdict-schema.mjs`; `lib/meta-aggregate.mjs` → `harness-judge/meta-aggregate.mjs`;
   add `harness-judge/adapters/api.mjs` (normalizer for the API transcript shape) and
   split `JUDGE.md` into the surface-neutral core + `JUDGE.api.md` appendix; add the
   **required `frustration`** field to the verdict schema.
3. **Generalize the generator.** `lib/generate-brief.mjs` → `persona-briefs/generate-core.mjs`.
4. **Re-point imports.** `agent-driver/tools.mjs` resolves briefs via
   `persona-briefs/index.mjs` (not local `BRIEFS_DIR`); the runner/judge path imports
   `harness-judge/core.mjs` + `meta-aggregate.mjs`. **No driver logic changes** — only
   resolution/import paths.
5. **Verdicts to the shared pool.** Point the API harness's `verdicts/` at the shared pool
   so API runs meta-aggregate together with UI + MCP runs.

**Phase 3 — parallelism hardening (independent, can precede or follow Phase 2).**
Parameterize the single `session.current.json` path into a per-`sessionId` session file
and add unique per-session project naming (see [Driver performance model](#driver-performance--interaction-model)). This is API-harness-local, touches no shared file, and unblocks safe N-concurrent fan-out.

**Phase 4 — first shared-layer runs.** Run the migrated briefs through the shared judge
core + API evidence adapter, meta-aggregate across the batch (mixing API + UI + MCP
verdicts, including the cross-surface "did Jordan behave consistently across surfaces"
rollup), and file findings with the standing discipline (re-confirm reproduces, cross-ref
logs, fix → deploy → live-validate before closing).

**Coordination with Trinity's and Morpheus's rollouts (so the three don't collide when the
shared packages are extracted).** All three rollout plans independently reached the same
constraint — **do not touch `scripts/persona-harness/` while Tank is mid-edit**, and defer
shared-package extraction to a **coordinated checkpoint**, not a concurrent edit:

- Trinity's Phase 2 ("shared-layer extraction + UI judge adapter") is explicitly
  "coordinated with the API-track owner and Morpheus, not done out-of-band on Tank's
  in-flight files", handed to the API-track owner / coordinator to land.
- Morpheus's Phase 2 states the extraction "moves `briefs/*.md` + the judge core into
  `scripts/persona-briefs/` … done as a single, sequenced step when the API harness is at
  a safe checkpoint (Tank's `harness/wip-persona-v1` merged or paused) — never a concurrent
  edit to Tank's live files."
- **This spec confirms that sequencing from the API side:** I (Tank, the file owner)
  perform — or explicitly sanction — the extraction at a safe checkpoint of
  `scripts/persona-harness/`, in one sequenced change, after the coordinator reconciles the
  flagged inconsistencies. Until then, the UI and MCP harnesses consume the equivalent
  modules **read-only** from `scripts/persona-harness/` and carry thin local shims; the API
  harness keeps running on its local copies. This is the single hand-off point where all
  three tracks meet, and it is deliberately serialized to avoid a three-way edit collision.

---

## Operating rules (inherited, apply to this track too)

- **Never call an API fix "verified" from unit tests alone.** Drive the real deployed API
  and capture objective evidence; always cross-reference kubectl logs + App Insights.
- **Driver captures, judge decides.** No issue is closed on "the response looks right" —
  subjective quality and root-cause go through the LLM/human judge.
- **Re-confirm still-reproduces before fixing.**
- **Issue closure requires deploy + live validation**, not review-passing alone.
- **The bearer token is a credential.** Never commit, log, or attach it to a finding.
- **Do not embed heuristic quality pass/fail in the driver.** Only deterministic mechanics
  gate; everything else is deferred.
- **Never approve a human-review/preview gate without live-testing the `preview_url`
  first** (hard rule from `e2e-harness-plan.md`).
