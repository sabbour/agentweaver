# Persona-driven API E2E harness

A reusable, API-only end-to-end harness that drives Agentweaver the way a real
user would — but through its **REST API with a bearer token**, not a browser.
It formalizes the ad-hoc "curl + `gh auth token`" validation the coordinator
already does into a repeatable loop that **persona definitions** (`specs/personas/*.md`)
can drive.

This is the **API-driven track** of issue
[#1 (Persona-driven self-improvement testing harness)](https://github.com/sabbour/agentweaver/issues/1).
A Playwright/browser track stays as a secondary, frontend-specific effort; this
track is the primary E2E validation method because it mirrors how the product is
actually exercised server-side and is fast, deterministic, and CI-friendly.

## Why API-driven first

- **Mirrors real validation.** E2E scenarios are already validated ad-hoc via
  API calls (`gh auth token` as the bearer key) plus kubectl/App Insights
  cross-checks. This harness makes that repeatable.
- **Driver, not judge, of quality.** The harness's job is to **drive** Agentweaver
  through the API and **capture the full raw evidence trail** (every API call with
  request+response bodies, the complete event stream, the drafted **outcome spec**
  verbatim, per-phase timings, token/cost). It renders only **objective,
  deterministic** verdicts (HTTP status, run status transitions, structural
  validity). Whether the produced *content* is actually good is a **subjective
  judgment deferred to a separate LLM/human judge** that reads the finding JSON.
- **Bounded and safe.** The default rung starts a coordinator run in
  `defineOutcome` mode, which drafts a confirmable plan and **suspends at the
  confirmation gate**. It exercises project creation → multi-agent team assembly
  → coordinator planning **without executing, merging, or deploying anything.**

## Two driving models

The harness supports two ways to *drive* Agentweaver. Both obey the same
driver/judge separation (capture evidence verbatim; defer quality to a judge) and
the same bounded-at-the-confirmation-gate safety rule. They differ only in **who
decides what the persona does next**:

| | **Fixed-script** (`scenarios/*.mjs`) | **LLM-in-the-loop** (`agent-driver/` + `briefs/*.md`) |
|---|---|---|
| Decides each turn | a hardcoded API-call sequence | a fresh-context LLM, live, from the REAL responses |
| Persona input | a scenario module | a **brief** (goals/voice/constraints, not a script) |
| Multi-turn pushback | no (one-shot draft) | **yes — mandatory ≥2 objections** via `outcome-spec/revise` |
| Output | a finding (`findings/*.json`) | a transcript (`transcripts/*.json`) |
| Best for | fast, deterministic regression checks + seam validation | emergent behaviour, revise-loop responsiveness, scenario discovery |

The **LLM-in-the-loop** model implements the "simulate user conversations"
technique (see `decisions/inbox/tank-persona-brief-pivot.md`): a fresh LLM is given
ONLY a persona brief and drives the run turn-by-turn, deciding each action from
what the API actually returned, and **must push back at least twice** (grounded in
real content) — which forces genuinely emergent behaviour instead of a scripted
demo. `agent-driver/tools.mjs` exposes the discrete tools it calls (`init`,
`list-blueprints`, `create-project`, `get-team`, `submit-goal`, `get-spec`,
`revise-spec` [the pushback lever], `get-events`, `finish`) — each hits the real
API and records the turn verbatim (including every `get-spec` poll attempt and the
full raw `get-events` body). There is deliberately **no `confirm` tool**, so
the scoping-rung run stops at the gate and never executes.

> **A "turn" here = one API action, not a chat message.** We are not simulating a
> chat conversation (no chat/MCP/Console surface is driven today — that's hardened
> and tested separately, out of scope for #1). Each turn is the driving LLM choosing
> the next real API *action* from the brief + real responses so far, and "pushback"
> means it reads a real response and issues a real lever call (`revise-spec`
> feedback — later `steer` / review `request_changes`), not free-text chat. Same
> turn-by-turn / no-prescripted-both-sides discipline and mandatory ≥2 pushback,
> just anchored to API actions + JSON responses. *Forward-looking (not built now):*
> once MCP/Console chat is hardened, the same brief/pushback/meta-aggregation
> architecture should be re-applied at the chat layer — see `JUDGE.md`.

> Status: LLM-in-the-loop is **prototyped + live-verified for two personas**
> (`briefs/priya.md` and `briefs/jordan.md`) — the second persona confirmed the
> pattern repeats (it independently surfaced a *revision regression*, a finding a
> fixed script can't see). Fixed-script scenarios are kept as fallback references.
> The **judge** methodology (per-run + cross-run meta-aggregation) is documented in
> [`JUDGE.md`](./JUDGE.md).

## The self-improvement loop (API edition)

1. **Load a persona brief or scenario** (`briefs/*.md` / `scenarios/*.mjs`, derived
   from `specs/personas/*.md`) as the test identity, goals, and behavioral profile.
2. **Drive Agentweaver via REST** — create a project from a blueprint, assemble a
   team, submit a goal, review the drafted plan; in the LLM-in-the-loop model,
   **push back ≥2 times** and read each re-draft.
3. **Capture the full evidence trail** — every API request+response body, the
   complete event stream / turn transcript, the drafted outcome spec verbatim,
   per-phase timings, and token/cost metrics — into structured JSON.
4. **Driver verdict = objective mechanics only** — the driver decides P0
   platform-correctness (did the calls succeed, did a team assemble, did the spec
   settle, no `run.failed`, did the mandatory pushbacks happen) and deterministic
   structural checks (seam validation). It does **not** decide subjective quality.
5. **Judge (separate pass)** — an LLM or human reads the captured evidence + the
   persona's success criteria and renders the **P1 output-quality** verdict, per
   [`JUDGE.md`](./JUDGE.md): a per-run verdict AND a periodic cross-run
   meta-aggregation (invariants / divergences / capability gaps / drift). A failing
   verdict is ready to be filed as a GitHub issue; rerun after fixes.

## Driver / judge separation (why the harness does not self-certify quality)

Per @sabbour's architectural correction, the harness **must not embed heuristic
pass/fail judgment of output quality** — such author-written heuristics can't
anticipate every valid variation and risk silently masking regressions. The split:

- **Driver (this code) hard-fails ONLY on deterministic facts.** Objective
  platform-correctness (P0) — auth accepted, project/team/run created, spec left
  `drafting`, no `run.failed` — and deterministic **structural/schema validation**
  (reserved-role denylist for issue #311, `WorkflowDefinitionLoader.Load` mirror:
  dangling edges / unrouted check branches, the backend guard round-trip). These
  are objective; a regression is unambiguous, so they legitimately gate the run.
- **Judge (separate LLM/human pass) decides subjective quality (P1).** "Is the
  drafted plan actually good for this persona?" is deferred. Scenarios expose a
  non-gating `judgeContext(evidence)` that returns deterministic **reference data**
  (e.g. Priya's expected ticket IDs, the known 4821↔4822 duplicate pair) — never a
  `pass:` field. The judge compares the captured evidence against the persona's
  authored **"Success looks like"** criteria (embedded in `judgeInputs`).
- **CANNOT_DETERMINE.** Genuinely unobservable through the API surface (e.g. the
  generator's model provider was down). **Never guessed** — excluded from scoring
  and reported distinctly (exit `3` when a run is otherwise clean but has gaps).

> The harness now **assembles** the judge prompt for you (it still does not *call*
> an LLM itself — no keys, no network): `lib/judge.mjs` packages a captured
> transcript + `JUDGE.md` + the persona's authored criteria into a single prompt a
> real LLM consumes, and `lib/meta-aggregate.mjs` rolls up the resulting verdicts
> across a batch (invariants / divergences / recurring findings). See
> **[Automated judging](#automated-judging-prompt-assembly--meta-aggregation)**.
> The actual P0/P1 verdict is still rendered by a fresh LLM (this conversation, the
> coordinator, or a future automated step) — the harness only drives and formats.

The reporter's console banner reflects the **driver** verdict only — `DRIVE+CAPTURE
OK` / `DRIVER P0 FAIL` — and prints "P1 — output quality: ⧗ DEFERRED to LLM judge".

## Automated judging: prompt assembly + meta-aggregation

The judging is a **two-layer LLM pass**, and the harness now provides the plumbing
for both — while still never *calling* an LLM itself (no keys, no network; it only
assembles the prompt and rolls up whatever verdicts an LLM returns):

**Layer 1 — per-run verdict.** `lib/judge.mjs` takes one captured transcript and
packages it — together with `JUDGE.md` and the persona's authored
`specs/personas/*.md` criteria (resolved automatically from the brief) — into a
single prompt. Feed that prompt to any LLM (this conversation, the coordinator, a
future automated step). It handles both transcript shapes (v1 where the spec *is*
`response.body`, and v1.1 where it is `response.body.spec` with a deterministic
`p0Objective` block), surfaces every drafted spec verbatim and each pushback's
before/after, and asks the judge to emit a machine-readable verdict block
(`agentweaver.persona-judge-verdict/v1`).

```bash
# assemble a judge prompt from a captured transcript, then hand it to an LLM
node lib/judge.mjs transcripts/priya-live-2026-07-14T11-20-37-407Z.json > judge-prompt-priya.txt
#  ...feed judge-prompt-priya.txt to an LLM; save its ```json``` verdict block to verdicts/priya.json
node lib/judge.mjs transcripts/jordan-live-....json --out judge-prompt-jordan.txt
```

**Layer 2 — cross-run meta-aggregation.** `lib/meta-aggregate.mjs` consumes the
verdict blocks from a whole batch and cross-references them (JUDGE.md "Layer 2"):
invariants (P0 mechanics that held in *every* run → candidate platform guarantees),
divergences (P1 verdicts that varied → judgment-call space / inconsistency signal),
recurring findings (the same issue surfaced by ≥2 personas — e.g. the #315
revision-regression reproduced by Jordan **and** Maya), capability gaps, drift, and
pushback compliance. It makes no subjective call of its own — it only tallies.

```bash
# roll up all the LLM verdicts from a batch (a dir of *.json, or explicit files)
node lib/meta-aggregate.mjs verdicts/ --json rollup.json
```

Verdict blocks live in `verdicts/` (git-ignored run artifacts, like `transcripts/`
and `findings/`). The prompt-assembly and rollup logic is unit-tested in
`test/judge.test.mjs`; the actual judging is not tested (it requires a real LLM).

## Layout

```
scripts/persona-harness/
  run-persona.mjs            Fixed-script CLI — resolves token, loads scenario+persona, drives, reports
  JUDGE.md                   Judge playbook (per-run verdict + cross-run meta-aggregation)
  package.json               Declares the one dependency (yaml) + `npm test`
  lib/
    client.mjs               Thin bearer-auth REST client; records every call for evidence
    persona.mjs              Parses specs/personas/*.md (scenarios + failure signals)
    runner.mjs               Generic persona DRIVER + evidence capturer (objective P0 only)
    seams.mjs                Generated-artifact seam driver (blueprint/workflow generation)
    generation-checks.mjs    Pure validators: reserved-role denylist + workflow YAML validation
    judge.mjs                LLM-judge PROMPT ASSEMBLER — packages transcript + JUDGE.md + criteria (no LLM call)
    meta-aggregate.mjs       Layer-2 cross-run rollup over LLM verdict blocks (invariants/divergences/recurring)
    metrics.mjs              Token/cost summary via GET /api/projects/{id}/metrics
    reporter.mjs             Structured finding writer + console report
  briefs/
    priya.md                 Persona BRIEF — support triage (goals/voice/constraints + mandatory ≥2 pushback)
    jordan.md                Persona BRIEF — greenfield idea → AKS Automatic plan (2nd persona; proves the pattern repeats)
    maya.md                  Persona BRIEF — market strategist / Q3 competitive brief (3rd persona; content domain)
  agent-driver/
    tools.mjs                Persona-agnostic discrete tool surface over the real API for an LLM to drive live (records transcript)
  scenarios/                 Fixed-script fallback (one-shot, deterministic)
    priya-ticket-triage.mjs  Persona scenario — support ticket triage (scoping rung)
    jordan-blank-to-plan.mjs Persona scenario — blank idea → coordinated plan (scoping rung)
    generated-artifacts-seam.mjs  Seam scenario — generated roster/workflow structural integrity
  test/
    priya-checks.test.mjs         Unit tests for Priya's non-gating judgeContext + TLS guard
    generation-checks.test.mjs    Unit tests for the seam validators (with #311 + structural negatives)
    agent-driver-tools.test.mjs   Unit tests for the driver's deterministic P0 computation
    judge.test.mjs                Unit tests for the judge prompt assembler + meta-aggregation rollup
  findings/                  Emitted JSON findings from fixed-script runs (git-ignored)
  transcripts/               Emitted turn-by-turn transcripts from LLM-in-the-loop runs (git-ignored)
  verdicts/                  LLM-judge verdict blocks consumed by meta-aggregate.mjs (git-ignored)
```

## Running the LLM-in-the-loop driver (persona brief)

A fresh-context LLM (a sub-agent, or any model with shell access) is handed ONLY a
brief and drives live. It calls the tools from `agent-driver/`. `--brief` selects
any persona in `briefs/` (`priya`, `jordan`, …) — the tool wrapper is
persona-agnostic; only the brief and the LLM's live decisions differ:

```powershell
cd scripts/persona-harness/agent-driver
node tools.mjs init --brief <persona> --base-url https://agentweaver.<zone>.westus2.staging.aksapp.io --insecure
node tools.mjs list-blueprints --thought "..."
node tools.mjs create-project --blueprint <id> --thought "..."
node tools.mjs get-team --thought "..."
node tools.mjs submit-goal --goal "<the persona's plain-language ask>" --thought "..."
node tools.mjs get-spec --thought "..."
node tools.mjs revise-spec --feedback "<pushback grounded in what you just read>" --thought "..."   # ≥2 required
node tools.mjs get-spec --thought "..."     # read the re-draft, decide if addressed
node tools.mjs finish --summary "..."       # stops at the gate (no confirm), writes transcript, cleans up
```

Each command prints the REAL API response so the driving LLM reacts to actual
state. `finish` records both raw `pushbackAttemptCount` and objectively successful
`pushbackCount`, plus a deterministic `p0Objective` block (HTTP success, successful
pushbacks, post-pushback spec re-settle, safe terminal state). It writes an
`agentweaver.persona-transcript/v1.1` file to `transcripts/`. P1 quality remains
explicitly deferred to the separate judge. Hand that transcript +
the persona's `specs/personas/*.md` criteria to a judge per `JUDGE.md`.

> The checked-in `transcripts/priya-live-2026-07-14T11-20-37-407Z.json` and
> `transcripts/jordan-live-2026-07-14T12-00-55-930Z.json` artifacts are legacy
> **v1** evidence kept for reference only. Fresh live reruns for both Priya and
> Jordan would be valuable future work to capture real **v1.1** transcripts with
> the new root/per-turn fields.

## Install

The persona-scoping track is zero-dependency (Node built-ins + global `fetch`),
but the **seam validators parse YAML**, so install the one dependency once:

```powershell
cd scripts/persona-harness
npm install
```

## Run it

Requires Node 18+ (uses global `fetch`). Token resolves from
`--token` → `$AGENTWEAVER_TOKEN` → `gh auth token`.

```powershell
cd scripts/persona-harness

# List available scenarios
node run-persona.mjs --list

# Run one scenario against staging (uses `gh auth token` as the bearer key)
node run-persona.mjs `
  --scenario priya-ticket-triage `
  --base-url https://agentweaver.<zone>.westus2.staging.aksapp.io `
  --insecure           # staging cert SAN can drift; omit against a trusted cert

# Keep the created project/run for manual inspection
node run-persona.mjs --scenario priya-ticket-triage --base-url <url> --keep
```

Exit code: `0` driver drove + captured evidence cleanly (P0 platform-correctness
held — **not** a quality verdict), `1` a deterministic driver check failed
(mechanics or structural), `2` harness/setup error, `3` inconclusive (a seam
scenario couldn't be assessed because the generator's model provider was
unavailable — not a product regression). A JSON finding is written to `findings/`
on every run for the judge to consume.

### TLS verification and `--insecure`

`--insecure` disables TLS certificate verification (staging cert SANs can drift on
redeploy). It is **only** honoured for `localhost`/`127.0.0.1` and `*.staging.*`
hosts. Against any other host it aborts with exit `2` unless you also pass
`--allow-insecure-prod`, so a fat-fingered production URL can never silently run
without cert checks.

### Tests

Pure-function unit tests (no network) cover the scenarios' **non-gating
`judgeContext`** reference data (proving it hands the judge the exact ticket IDs /
duplicate pair / verify checklist, and computes no pass/fail), the TLS guard, and
the deterministic seam validators (with issue-#311 + structural negative cases):

```powershell
cd scripts/persona-harness
node --test
```

> Finding the current staging host: the ingress/gateway hostname rotates on
> redeploy. Resolve it with
> `kubectl get httproute -n agentweaver -o jsonpath='{.items[0].spec.hostnames[0]}'`.

## What one scenario does (Priya — Ticket triage swarm)

`scenarios/priya-ticket-triage.mjs` maps Priya's first persona scenario to the API.
The driver decides the deterministic rows; the last two rows are captured as
**evidence for the judge**, not decided by the driver:

| Persona intent | API call | Driver signal (deterministic) |
|---|---|---|
| Be a real support lead | `GET /api/auth/github` | `status == signed_in` |
| Explore team templates | `GET /api/blueprints` | `blueprint-content-authoring` offered |
| Create a support-ops project | `POST /api/projects` (blank + blueprint) | `201` + `project_id` |
| Get a multi-agent team | `GET /api/projects/{id}/team` | `members.length >= 2` |
| Paste messy ticket batch, start | `POST /api/projects/{id}/orchestrations` (`defineOutcome`) | `201` + `runId` |
| Review the plan | `GET /api/runs/{id}/outcome-spec` (poll) | spec settles out of `drafting` |
| See the work happen | `GET /api/runs/{id}/events` | events flowed, no `run.failed` |
| Trust/trace the plan (**judge, not driver**) | full `outcomeSpec` captured verbatim | judge compares vs persona success criteria |

Persona **success criteria** and **failure signals** ("cannot trace why a ticket
got a severity", "cannot handle batches/long pasted text") are embedded verbatim in
the finding's `judgeInputs` so the LLM/human judge — not the driver — decides
whether the drafted content actually meets them.

The driver captures the coordinator's **drafted fields** verbatim (not the echoed
goal) so the judge has real content to assess, and the poller waits for the spec
to leave the transient `drafting` state before capturing it as settled.

## Seam-testing GENERATED artifacts (issue #1 expansion)

Beyond judging a product *outcome*, the harness also targets the **generation
seams** — the points where the LLM-backed generators emit blueprints, workflows,
and team casts. A generated artifact that is merely *non-empty* is not good
enough; it must be **structurally correct**, or it fails later (at run time, or —
worse — silently, the way a human had to catch
[issue #311](https://github.com/sabbour/agentweaver/issues/311) by hand when a
generated roster leaked a reserved system role).

`scenarios/generated-artifacts-seam.mjs` (a `kind: 'generation-seam'` scenario,
driven by `lib/seams.mjs`) exercises the real generators and asserts:

| Seam | API call | Structural assertion |
|---|---|---|
| Blueprint roster | `POST /api/blueprints/generate` | roster is a real multi-role team (≥N) |
| Blueprint roster | ″ | **excludes reserved roles** (Scribe/Work Monitor/Rai/Coordinator — #311) |
| Blueprint workflows | ″ | bundles ≥1 workflow; any inline `generated_workflow_yaml` **passes backend structural validation** |
| Workflow graph | `POST /api/projects/{id}/workflows/generate` | passes `WorkflowDefinitionLoader.Load` rules (no dangling edges, every check-node verdict routed, serial steps resolve, known node types) |
| Workflow graph | ″ | assigns no work to a reserved orchestration role |
| Backend guard (round-trip) | `PUT /api/projects/{id}/workflows/{id}` | backend **rejects** a deliberately-broken workflow with 4xx (mirror agrees) **and accepts** a valid one (positive control — proves the rejection is specific, not blanket) |

The round-trip check is the strongest form: it proves the harness's local mirror
agrees with the **live, deployed** backend guard, not just a copy of its source.

The two validators in `lib/generation-checks.mjs` are **faithful ports of backend
truth**, so the harness fails on exactly what the backend would reject (or should
have):

- `findReservedRoleLeaks` / `isReservedRole` — mirror
  `packages/Agentweaver.Squad/Catalog/ReservedRoles.cs`.
- `validateWorkflowYaml` — mirrors
  `apps/Agentweaver.Api/Workflows/WorkflowDefinitionLoader.cs` (collects *all*
  violations instead of failing fast, but the pass/fail contract is identical).

These are unit-tested with adversarial fixtures (`test/generation-checks.test.mjs`):
a roster that leaks `Scribe` (the #311 shape), a workflow with a dangling edge, a
check node with an unrouted verdict, a serial step that references nothing, an
unknown node type — each asserts a **FAIL**, so the checks would catch a real
regression rather than rubber-stamp anything.

```powershell
node run-persona.mjs --scenario generated-artifacts-seam --base-url <staging-url> --insecure
```

> Generation calls a live model and can take 20–60s per seam; a provider outage
> yields exit `3` (inconclusive), not a false `1` (fail).

## Performance / cost metrics

Every run records **per-phase latency** in the finding under `phaseTimings` and a
**token/cost summary** under `performance`, then prints both — so speed *and*
spend regressions are visible over time, not just pass/fail.

- **Phase timings** (persona runner): `blueprintsFetchMs`, `projectCreateMs`,
  `teamFetchMs`, `orchestrationAcceptMs`, `outcomeSpecSettleMs`. (Seam runner:
  `blueprintGenerateMs`, `projectCreateMs`, `workflowGenerateMs`.)
- **Token / cost**: fetched from the same endpoint the product dashboard uses —
  `GET /api/projects/{id}/metrics` (`lib/metrics.mjs`) — **before cleanup**, so a
  just-run project's usage is captured. Reports `totalTokens`, `totalAiu`
  (nanoAiu ÷ 1e9), per-model and per-agent breakdowns, and response-duration
  p50/p95. App Insights can lag a fresh run; the summary flags `hasData:false` so
  zeros read as "not yet ingested", not "free".

> Deeper per-milestone timings (each rung of `confirm → run → review`) land with
> the deeper-rungs increment.

## Current scope / status

| Requirement (issue #1 expansion) | Status |
|---|---|
| Persona scoping rung (project → team → plan, judged from API) | ✅ live (Priya, Jordan) |
| Seam-testing generated artifacts (rosters, workflows) | ✅ live (`generated-artifacts-seam`) |
| Backend-guard round-trip (mirror agrees with live guard) | ✅ live (PUT broken → 4xx, valid → 2xx) |
| P0 platform-correctness + structural (driver-owned, deterministic) | ✅ in driver + reporter |
| Driver/judge separation (driver captures evidence; LLM judge decides quality) | ✅ evidence + `judgeInputs` in finding v2 |
| Per-phase performance timings in the finding | ✅ both runners |
| Token / cost metrics (via `/api/projects/{id}/metrics`) | ✅ live in finding `performance` |
| LLM-in-the-loop driving (persona brief, live turn-by-turn, ≥2 pushbacks) | ✅ prototyped + live (Priya **and** Jordan) — `agent-driver/` + `briefs/*.md` |
| Two-layer judge methodology (per-run + cross-run meta-aggregation) | ✅ documented (`JUDGE.md`) |
| LLM-in-the-loop for more personas (beyond Priya + Jordan) | ⏳ pending (fixed-script scenarios kept as fallback) |
| Automated meta-aggregation pass over a transcript batch | ✅ live (`node lib/meta-aggregate.mjs verdicts/` mechanically rolls up judge verdict JSON; external human/LLM judgment is still separate) |
| Automated LLM-judge-calling pass (consumes findings/transcripts) | ⏳ pending (format ready, caller not built) |
| Deeper rungs (confirm → run → review → outcome) | ⏳ pending (opt-in `--deep`) |
| Draft-blueprint testbed mode | ⏳ pending |



Add a file under `scenarios/` exporting a default object. The generic engine in
`lib/runner.mjs` handles auth, project creation, team assembly, orchestration,
polling, full evidence capture, deterministic P0 checks, and cleanup — a scenario
only supplies:

```js
export default {
  id: 'jordan-blank-to-plan',                 // used in the finding filename
  personaFile: 'greenfield-aks-automatic-developer.md',
  personaScenario: 'Blank idea to AKS Automatic',
  title: 'Jordan Lee — Blank idea to a coordinated plan (API-driven)',
  blueprintId: 'blueprint-software-development',
  projectPrefix: 'persona-jordan',
  buildGoal(persona) { return 'Build a simple multi-user task tracker ...'; },
  // NON-GATING: deterministic reference data for the LLM/human judge — NEVER a
  // pass/fail verdict. Returns e.g. expected ticket IDs + a "judge should verify"
  // checklist derived from the persona's authored success criteria.
  judgeContext({ outcomeSpec, events, team, submittedGoal }) {
    return { expectedTicketIds: [...], judgeShouldVerify: ['...'] };
  },
};
```

- `buildGoal` returns the plain-language goal the persona would type.
- `judgeContext` returns **non-gating** reference data for the judge. It must NOT
  compute pass/fail — subjective content quality is the judge's call, not the
  driver's. (Deterministic *structural* validation lives in the driver/seams, not
  here.)
- Everything else (the shared rungs + deterministic P0 checks + evidence capture)
  is inherited.

### Adding deeper rungs (beyond the confirmation gate)

The default engine stops at the outcome-spec confirmation gate (safe, no side
effects). To go further, reuse the same `client` in a scenario/engine extension:

- `POST /api/runs/{id}/outcome-spec/confirm` → let the plan execute.
- Poll `GET /api/runs/{id}` for `reviewing` (HITL gate) / `completed`.
- `POST /api/runs/{id}/assembly/review` `{ approve: true }` / `{ request_changes: true }`.
- `GET /api/runs/{id}/files` + run detail to inspect produced artifacts.

Keep deployment-triggering scenarios (e.g. Jordan's full "deploy to AKS
Automatic") behind an explicit opt-in flag so default runs never deploy.

## Filing findings as issues

A finding's `driver.platformPass == false` (a deterministic mechanics/structural
break) — or an LLM judge's P1 verdict on the captured `evidence` — plus the
`apiCalls` trace contain everything needed to open an issue:

```powershell
$f = Get-Content findings/<latest>.json | ConvertFrom-Json
gh issue create --title "persona($($f.scenario.id)): $($f.driver.platformChecks | ? {!$_.pass} | Select -First 1 -Expand name)" `
  --body "Persona: $($f.persona.title)`nScenario: $($f.scenario.personaScenario)`nTarget: $($f.target)`n`n(attach finding JSON)"
```

## Reference

- Persona schema: [`specs/personas/README.md`](../../specs/personas/README.md)
- REST API: [`apps/Agentweaver.Api/API.md`](../../apps/Agentweaver.Api/API.md)
- Coordinator outcome-spec / confirmation gate: `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs`
