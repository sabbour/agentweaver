# Agentweaver Continuous Validation Plan

_Last updated: 2026-07-14_

## Goal

Run **continuous autopilot validation** of Agentweaver on staging AKS, built around two standing workstreams that run in parallel, indefinitely:

1. **An LLM-powered E2E test harness** — an LLM designs scenarios, drives them via the API + `gh` bearer token, monitors execution, and judges output quality/suitability (not a fixed manual script).
2. **Continuous triage over open epics/issues** — periodically re-scan the backlog for staleness, already-fixed items, and re-prioritization — interleaved with harness work, not a one-time pass.

The coordinator (Squad) approves issue closure, priority, and scope changes only after the user (@sabbour) confirms; fixes are only closed once deployed and validated, with occasional explicit overrides.

---

## Current State (as of v0.9.49-rc1 live + verified, second in-flight batch running)

**Deployed & live (confirmed):** `v0.9.49-rc1` on staging AKS — all 4 workloads (api, frontend, mcp, worker/agent-host) Running, `/api/version` = `0.9.49-rc1`, `40-verify.sh` = **23/23 checks passed**.

**Closed in prior sessions (2026-07-13), carried forward as confirmed-still-closed:** #268, #304, #254, #263, #214, #249, #262, #273, #277, #279, #213, #215 (stale).

**Closed 2026-07-14 (batch 1, coordinator-verified):** #269 (Kata bwrap passthrough), #226 (steer redirect at review gate), #176 (blueprint under-selection).

**Closed 2026-07-14 (batch 2, rubber-duck peer-reviewed then coordinator-closed):** #270 (preview crash, #269 downstream symptom — evidence-commented), #250 (token-breakdown case-sensitive grouping — evidence-commented), #175 (workflow save 500 — root cause: allowed-workflow-id not persisted before Sync; fix live via local merge commit `3d59f944`), #305 (steering revision-child worktree branch mismatch — fix live since v0.9.47-rc1 via `1e54aab6`, fresh live repro confirmed zero recurrences).

**Fixed and live, carried in v0.9.49-rc1 (previously listed as pending, now confirmed deployed):** #227+#309, #308, #306, #224, #216, #278, #303, #307 (AgentHost pod resource right-sizing).

**In flight (new batch, dispatched post-deploy):** #1 (API-driven persona harness spike — prototype built and passing 9/9 live checks by Tank; pending rubber-duck design review before adoption as default), #180 (App Insights workspace id — found already fully fixed/wired live, config-only verification in progress, no code change needed), #208 (cancellation-as-failure telemetry storm — fix in progress), #186 (workflow editor RAI/Rubberduck/Human Review gate authoring palette — in progress, ties directly to the rubber-duck operating rule), #247 (global notification center MVP for Human Review + Tool Approval — in progress), #266 (preview stuck-dispatching stall — re-diagnosis from scratch in progress after v0.9.48-rc1 fix failed live validation).

**New this session:** #310 filed (catalog gap: no dedicated infra/ops workflow, nested under epic #296) from generation-quality probing.

**Deploy-state note:** `v0.9.48-rc1` (api+agent-host only, worker never updated) is confirmed superseded/poisoned — do not build on top of it. `v0.9.49-rc1` is the current authoritative live baseline.

### Status update — batch 3 (2026-07-14, still on v0.9.49-rc1, v0.9.50-rc1 not yet cut)

**Closed this batch (peer-reviewed/evidenced):** #309 (steer-redirect full-workplan reset — live-validated by Tank on a real repro run), #208 (cancellation-as-failure telemetry storm, backend+frontend combined), #312 (Vite preview 403 — gateway `URLRewrite` Host-to-`localhost` fix), #247 (global notification center MVP, Tool Approval explicitly deferred to #246), #310 (infra/ops catalog workflow), #227 (ghost queued steer directive), #216 (tool-approval URL-keying, live-confirmed).

**Reopened:** #267 (A2A `NotSupportedException: Received: None` — real regression, SDK pin confirmed unchanged, reproduced twice including a clean 57s repro with no cluster contention).

**New issue filed:** **#313** — root cause of the recurring "3-minute hard deadline" the user flagged: `ShellExecutionTracker`'s watchdog deadline races `PassthroughExecutor.CancelAfter` using the *same* timeout value, so a graceful/recoverable per-command timeout always loses to a fatal `shell_execution_timeout` throw. Confirmed a **code bug, not an infra/LB/Istio/kata timeout** — fix in progress.

**#306 confirmed already fixed and closed** (coordinator graph phantom edge — corridor-occlusion detection in `routeGridEdges()`, already live on `main`/v0.9.49-rc1, regression test `fittrackEdgeOcclusion.test.ts` passing; not a live click-through re-check, but low risk given the test pins the exact reported geometry).

**Process correction (caught by Seraph's triage pass #3):** #311, #208, #312, #247, #310 had been closed on peer-review + unit-test evidence alone while their fixes were still **uncommitted, undeployed working-tree diffs**. This violates the standing rule that issues are only closed once a fix is actually deployed and live-validated — peer review gates a merge, it does not substitute for deploy+validation. All five were **reopened** with an explanatory comment; they'll be re-closed with live staging evidence once they land in the v0.9.50-rc1 release batch. Going forward: do not close an issue whose fix has not yet been committed, built, and deployed to staging, regardless of how strong the peer review is.

**Pagination feature (issue: "add paging to list pages with potentially large number of items"):** backend contract approved and release-ready (Niobe, incl. an int32-overflow fix). Frontend (Dozer) got **REQUEST_CHANGES** on first review — real bug: several list pages capped fetches at the backend's `MaxPageSize=100` and paginated client-side over that single snapshot, silently truncating anything beyond item 100 (Project Gallery, Orchestrations "Recent", `OverviewPage`'s previously-unbounded project list). Per the standing Reviewer Rejection Protocol, Dozer is locked out of that revision — a fresh agent (Apoc) is implementing genuine server-side paging instead.

**In flight:** #242 (AgentHost terminal-emission gap / stale `assembly_blocked` wedge-on-redirect, distinct from #308/#309's trigger class — Tank), #267 root-cause (Tank-2), #313 fix implementation (Link), #306 phantom-edge graph rendering bug (Trinity), #251 release retag-forward gap (Morpheus, infra/tooling only — no real deploys run as part of investigating it), #278 stop-button confirmation dialog (Trinity2), #261 `declared_output_paths` tri-state parsing (Niobe), #302 message-stream timestamps (Dozer), pagination-frontend fix (Apoc), persona harness / issue #1 redesign (Tank-1, absorbing the turn-by-turn/persona-brief/mandatory-pushback/meta-aggregation architecture below — no completion yet).

**Not yet done:** `v0.9.50-rc1` batch commit/release (waiting on the in-flight items above to clear peer review), Scribe decisions-inbox merge pass 3 (dispatched, pending), SQL todos resync.

### Status update — v0.9.50-rc1 shipped (2026-07-14)

**Deployed & live (confirmed):** `v0.9.50-rc1` on staging AKS — all 4 workloads (api, frontend, mcp, worker) + 8 AgentHost warm-pool pods Running, `/api/version` = `0.9.50-rc1`, `40-verify.sh` = **23/23 checks passed**.

**Review wave fully cleared before this release** (Reviewer Rejection Protocol chains, locked-out-author-per-round enforced throughout):
- **#261** — 5 rounds (Niobe→Switch→Roy→Neo→Trinity), final APPROVE. Coordinator declared-output-path parsing now fails the WHOLE array closed on any invalid entry (traversal escape, drive-qualified, UNC), not just the bad entry.
- **Pagination-frontend** — 5 rounds (Dozer→Apoc→Neo→Mouse→Sentinel), final APPROVE. Migrated `useArtifactBrowser.ts` onto shared `isTerminalRunStatus()`/`normalizeRunStatus()`.
- **MemoriesPage pagination** — 2 rounds (Niobe→Iris), final APPROVE WITH NON-BLOCKING. Fixed a mutation-on-last-page stranding bug.
- **#108** — 2 rounds (Niobe→Link), APPROVE WITH NON-BLOCKING. Worker HPA now scales on the true unclaimed-backlog signal (`CountReadyForPickupAsync`), not the `RunStatus.Pending` proxy.

**Closed with live staging evidence this release:** #261, #108, #311 (non-castable system roles excluded from generated casts — live-verified via a real team roster showing 6 domain members cleanly separated from 4 `is_built_in:true` system roles), #312, #313, #208, #247 (live-verified `/api/notifications` responding), #200, #310, #302, #246, #282. Pagination contract live-verified on `/api/projects`, `/api/projects/{id}/runs`, `/api/projects/{id}/decisions/inbox`, `/api/projects/{id}/memory` — all return the `{items, page, page_size, total_count, total_pages}` envelope correctly.

**New issue filed:** **#318** — pre-existing (not a regression) `DataMigratorTests` schema-drift bug: hardcoded 30-value `INSERT INTO runs` vs. the table's actual 34 columns. Confirmed via git history unrelated to this batch; doesn't block releases but should be fixed so `dotnet test` runs fully clean.

**Persona harness (issue #1) progress this release cycle:**
- Now checkpointed on branch `harness/wip-persona-v1` (not yet merged to main — still a spike/prototype under active iteration), safely committed via a temp-index technique that never touches the shared working tree.
- Tank added automated LLM-judge invocation support: `lib/judge.mjs` (pure prompt ASSEMBLER — resolves persona brief + criteria + JUDGE.md + full transcript into one prompt for an external LLM call; does **not** call any LLM API itself) and `lib/meta-aggregate.mjs` (mechanical cross-run verdict rollup). This first version got **REQUEST_CHANGES** (transcript evidence was lossy for non-spec API calls; verdict aggregator accepted unvalidated JSON) — Tank locked out, Oracle fixed both issues in round 2, 32/32 tests passing, re-pushed to the WIP branch. Re-review pending.
- **Smith** drove three more full-lifecycle scenario attempts this cycle: FitTrackE2E-v12 (reproduced the #308-family assembly wedge via a distinct trigger), LinkVaultE2E-v1 (surfaced a genuinely new root cause — filed as **#317**, a coordinator stall-timeout/completion-signal race where a child finishes work but the 5-min watchdog fires before learning of it), and **HabitLoopE2E-v1 — the harness's first full-lifecycle success this session** (dispatch → 6/6 subtasks → RAI → Rubberduck → Build&Test → live preview URL → human-review → complete, ~52 min end-to-end). Also flagged a harness/API-contract gap: `auto_approve_tools` doesn't propagate to child runs/gates (noted, not filed as a bug — plausibly intentional safety gating).

**Not yet done:** merge `harness/wip-persona-v1` to main once it stabilizes and passes design review cleanly; re-review Oracle's judge-automation round 2; continue Smith's scenario coverage on the new v0.9.50-rc1 baseline; Scribe inbox-drain pass (dispatched, pending); SQL todos resync against actual GitHub issue state.

---

## Operating Rules (standing, apply to all workstreams)

- **Never call a fix "verified" from unit/build tests alone.** `dotnet build` succeeding and unit tests passing only proves the code compiles and isolated units behave as coded — it does NOT prove the fix works end-to-end. Always construct and run a dynamic E2E test via live API calls against staging that exercises the actual user-facing scenario (the real bug's reproduction steps) before claiming "verified." Report build/unit results and E2E results as separate, clearly labeled lines — never conflate them.
- **Always trigger the Squad agent** — never work inline; route every scenario/fix/triage task through Squad's dispatch mechanism.
- **Use Fleet to parallelize as much as possible** — fan out independent scenarios/issues concurrently rather than serializing.
- **Never work on an issue without first validating it's not stale** — re-confirm it still reproduces against current `main`/staging before touching it.
- **Never take shortcuts.** Root-cause fixes only, no symptom-plastering.
- **Don't scope creep** — stay within the requested task; flag adjacent issues rather than silently expanding scope.
- **Model assignment:**
  - **Planning, design, complex debugging** → `gpt-5.6-sol` and `claude-opus-4.8`.
  - **Scoped implementation work** → `gpt-5.6-terra` and `claude-sonnet-5`.
- **Periodically trigger Scribe** to store decisions and perform memory hygiene (dedup, archive stale entries) — don't let this lapse during long harness/triage runs.
- **Rubber-duck / peer review before calling anything done.** No fix, design decision, or spike conclusion ships or gets marked "verified"/"closed" on the strength of the author's own say-so alone. Before closing an issue or committing a fix as final: (1) for non-trivial fixes or design pivots, spawn a `rubber-duck` agent (or a peer agent instance in a different role/name) to critique the approach or the evidence before it's treated as settled — this catches logic errors and blind spots the author can't see in their own work; (2) for anything a Reviewer-role agent (Smith/Seraph/Rai) would normally gate, don't let the implementing agent self-approve — route through the Reviewer Rejection Protocol like any other review; (3) this applies to spikes and harness design too (e.g. issue #1's API-driven harness prototype) — get a second opinion on the design before treating it as the new default, not just a build-passes/tests-pass check. Skipping peer review to save time is exactly the kind of shortcut the "never take shortcuts" rule above already forbids.
- **Documentation stays in sync with what's shipped.** Before building/deploying any change (any release-pipeline run, not just this milestone), run the `.copilot/skills/docs-feature/SKILL.md` docs-feature skill so docs are updated alongside the code, not after the fact or forgotten. This applies per-fix, not just at milestone boundaries: an agent that lands a root-cause fix should check whether user-facing docs, API docs, or architecture notes need updating for that fix before handing it back, and the coordinator should verify this happened before including the fix in a release batch. Don't let "the fix works" substitute for "the fix is documented."
- **Standing autopilot loop directive — never idle, never go single-threaded when parallel work exists.** On every turn (including under autopilot with no user present), before doing anything else: (1) check for any running/idle background agents and process their results immediately (validate/commit/file-issue/close, or hand them the next task); (2) query the ready backlog (open issues not blocked on something in-flight, pending todos with satisfied deps) and identify EVERY independent item that could start right now; (3) dispatch ALL of them as background agents in one batch — never dispatch a single agent when two or more independent items are ready, and never let "I'm doing coordinator mechanics (commits, version bumps, doc updates)" become an excuse to pause the dispatch loop, since those are quick and should be interleaved between batches, not treated as the main event. If genuinely no new independent work exists, explicitly state that before going idle so it's clear this was checked, not forgotten.
- **Never leave backlog work unassigned while agents sit idle** — when an agent goes idle, immediately hand it (or a fresh agent) the next backlog item. If the next task is genuinely related and benefits from the idle agent's existing context, reuse its session (`write_agent`) and update its description/title to reflect the new task. Otherwise, launch a brand-new agent with fresh context for the unrelated task — but there's no need to actively terminate/delete the idle agent's session first; it's fine to just leave it idle and spin up a new one alongside it.
- **Only the coordinator (Squad) runs the release pipeline.** Fix agents commit their code changes (or leave them staged for the coordinator to include) and report back — they must NEVER independently prepare/publish a release or run `npm run azure:deploy-from-local`, `npm run azure:deploy-from-release`, or `npm run azure:release`. An agent doing this out-of-band produces inconsistent partial-deploy states — see the v0.9.48-rc1 incident. All deploys happen as a single coordinated Release Milestone (see below).
- **Periodically update this plan's "Next Steps on Resume" section after each batch of work** — after a batch of agent dispatches lands (fixes merged, validations complete, issues filed/closed), update status and what's next so the plan never goes stale relative to reality. Do this *in addition to*, not instead of, continuing to dispatch new work — never let a status-doc update become an excuse to pause dispatching. Self-improve this plan's own wording/structure freely when a better convention is learned (e.g. tightening a rule after a live correction) — but never delete or weaken an existing standing instruction; only add, refine, or append.
- **Never let agent messages/notifications sit queued without action.** The moment an agent goes idle or reports back, immediately triage the result and either close the loop (validate/commit/file issue) or dispatch its next task — don't let idle agents or completed reports accumulate unprocessed while attention drifts to side conversations.
- **The API-driven persona harness (issue #1) must be a thorough, LLM-driven E2E method, not a hand-waved smoke test.** It is not enough for a persona scenario to reach a checkpoint and pass a shallow substring/existence check. The harness must, at minimum: (1) have an LLM dynamically design and vary scenarios (not just a small fixed set of hardcoded per-persona playbooks) so it actually probes the space of realistic user intents rather than replaying the same script; (2) test the seams around *generated* artifacts specifically — blueprint generation, workflow generation, team-cast generation — verifying the generated output is structurally correct and fit for purpose (e.g. the #311 non-castable-roles bug is exactly the class of seam defect this harness should catch automatically, not rely on a human noticing); (3) drive workflows all the way through to completion and assert the actual produced outcome matches the scenario's authored success criteria in substance (specific content/structure checks, not "a non-empty response containing a keyword") — this includes the deeper rungs (confirm → run → review gate → merge/deploy), not just the initial scoping/outcome-spec rung; (4) track performance metrics for every run (latency per phase, token/cost usage, wall-clock time to each milestone) so regressions in speed/cost are caught the same way regressions in correctness are. Treat "PASS" claims that don't meet this bar as incomplete work requiring further hardening, not as done — this supersedes any earlier narrower framing of the harness as just a scoping-rung smoke test.
- **The #1 harness also doubles as a blueprint-idea testbed.** Beyond regression validation, the harness should be usable to try out and evaluate *new* blueprint/workflow ideas before they're added to the catalog — i.e., point a persona scenario at a draft/candidate blueprint (not just already-cataloged ones) and use the harness's seam/outcome/performance checks to judge whether the new idea actually produces a good result, the same rigor applied to validating existing ones. Design the harness's scenario/blueprint selection to accept an arbitrary or draft blueprint reference, not just the fixed catalog, so it can serve this experimentation role.
- **The #1 harness must ship an explicit judge playbook — written instructions the LLM judge follows, not just a raw JSON dump.** Alongside the captured evidence (see next rule), the harness repo must include a document (e.g. `scripts/persona-harness/JUDGE.md` or similar) that tells an LLM judge exactly how to: (1) drive a scenario if it needs to re-run or extend one (which CLI command, which flags, what the scenario file format means); (2) where to look for supplementary evidence beyond the captured JSON report when the JSON alone is inconclusive — e.g. which `kubectl logs`/`kubectl describe` commands to run, which Application Insights/Log Analytics queries to run, which live API endpoints to hit for corroboration (reusing the proven ones already confirmed this session: `/api/projects/{id}/metrics`, `/api/runs/{id}/token-breakdown`); and (3) exactly how to render a verdict — the P0 platform-correctness / P1 output-quality / CANNOT_DETERMINE taxonomy, what counts as evidence for each, and the output format expected (so results are comparable run-over-run). This turns "an LLM judges the results" from an implicit expectation into a concrete, followable playbook — the same way a human reviewer would be handed a rubric, not just raw logs.
- **The #1 harness must NOT embed its own heuristic validation logic — it is a driver, not a judge.** The harness's code (`run-persona.mjs`, `lib/runner.mjs`, scenario `extraChecks`, etc.) should be responsible only for: driving the run through the API (create project, assemble team, submit outcome/steer/confirm calls), capturing the full raw evidence trail (API responses, event stream, drafted spec/outcome content, timing/token data), and reporting that evidence in a structured, complete form. It must NOT contain hardcoded pass/fail rules — regexes, substring matches, field-presence checks, or any other author-written heuristic standing in for "is this good" — because those heuristics can't anticipate every valid variation and silently mask regressions or false negatives the heuristic's author didn't think of. Interpretation of whether a run's outcome is correct/high-quality belongs to a separate LLM judge pass (an agent — e.g. a rubber-duck/reviewer instance, or a fresh coordinator-dispatched judge agent) that reads the captured evidence after the fact and renders a verdict (adopting Smith's P0 platform-correctness / P1 output-quality / CANNOT_DETERMINE taxonomy for that judgment). Any existing scenario `extraChecks` heuristics (e.g. Priya's ticket-ID/duplicate-detection substring assertions) must be refactored to STOP gating pass/fail in the driver — instead, ensure the driver captures the exact evidence those checks were inspecting (full drafted spec content, event payloads) so a judge LLM can assess it. This is a correction that supersedes the earlier hardening guidance to "strengthen extraChecks assertions" — strengthening heuristics was the wrong direction; removing the driver's authority to self-judge is the right one.
- **The #1 harness's scenario-generation model is turn-by-turn persona simulation with mandatory pushback, not fixed scripted playbooks — per the technique described in https://sabbour.me/2026/04/28/simulating-user-conversations-to-evolve-agent-prompts.html.** Concretely: (1) personas are defined as **briefs** (goals, constraints, voice/behavior traits, and an explicit "must push back / redirect at least twice" instruction) — NOT as pre-scripted step-by-step playbook files (the original `priya-ticket-triage.mjs`/`jordan-blank-to-plan.mjs` hardcoded-sequence model is superseded by this); (2) each scenario run is driven live, turn-by-turn, by a fresh LLM instance (no cross-run contamination) that decides the persona's next action based ONLY on the persona brief plus the real API responses seen so far in that run — never a pre-written both-sides script; (3) the persona must genuinely push back, object, or change its mind at least twice per run, decided in the moment from real API results, not scripted in advance — this is what forces authentic scenario variation instead of a scripted demo wearing a "dynamic" label; (4) after a batch of N scenario runs, a separate **meta-aggregation pass** cross-references all the runs' captured transcripts/evidence together (not just one run in isolation) to extract: system invariants — behavior that held true in every run (candidate P0 platform-correctness facts), divergent patterns — behavior that varied run-to-run (P1 judgment-call space, not a hard rule), tool/capability gaps the personas collectively hit, and drift between what a persona's brief assumed the system would do vs. what it actually did. This meta-aggregation pass is in addition to, not a replacement for, per-run P0/P1/CANNOT_DETERMINE judging — it's judgment computed across a batch, which surfaces patterns no single run can. The driver-not-judge rule above still holds exactly as stated: this changes HOW the driver decides its next action (LLM-in-the-loop instead of a fixed script), not whether it embeds pass/fail logic (it still must not).
- **The harness's "turns" are API actions, not chat messages — for now.** The blog post's technique describes literal chat conversation turns; our harness does not yet drive Agentweaver's MCP server or Console chat surface (those aren't hardened/tested yet — that's separate, future scope, not part of #1 today). So a "turn" in our implementation = the driving LLM choosing the next real API call (create project, submit outcome, confirm spec, steer, approve/reject a review gate, etc.) based on the persona brief and the real API responses received so far, and "pushback" means issuing a real steer/request-changes/reject call reflecting the persona's objection — not free-text chat. Once the MCP server and Console chat are separately hardened and validated (a future initiative, not now), the same persona-brief/pushback/meta-aggregation architecture should be re-applied at the chat layer, driving real conversational turns instead of raw API calls — note this as a forward-looking follow-on in the harness's design docs, but do not build it as part of the current #1 scope.

---

## Staging Environment Recovery

If you encounter **weird/unexplainable DNS resolution errors or catastrophic-looking failures** (broad, previously-working surfaces suddenly failing), this is likely the periodic staging resource-group deletion, not a real regression.

1. Verify you're on the correct Azure subscription (`AKS INT/Staging Test`, `26fe00f8-9173-4872-9134-bb1d2e00343a`).
2. Check whether `agentweaver-rg` still exists.
3. **If it's gone, you have standing authority to recreate the environment** and proceed — no need to ask first.
4. Recreating means a new ingress hostname, so:
   - The user (@sabbour) will need to update the GitHub OAuth App callback URL manually — flag this to them.
   - This does **not** block API/bearer-token-based testing — the harness can resume immediately once the new environment is up, using the new base URL.

---

## Workstream 1: LLM-Powered E2E Test Harness

**Design principle:** the harness itself should be LLM-driven — generating scenario prompts, launching runs, interpreting events/logs, and judging suitability — not a static script of fixed inputs.

> **Harness architecture moved.** The harness-architecture design (the three-harness split — API/UI/MCP — the shared `scripts/persona-briefs/` + `scripts/harness-judge/` packages, the canonical `agentweaver.persona-judge-verdict/v1` schema, and the driver/judge separation) now lives in dedicated sibling specs and supersedes the architecture description here: **`docs/api-test-harness-plan.md`** (API, ground-truth), `docs/ui-test-harness-plan.md` (UI), `docs/mcp-test-harness-plan.md` (MCP). The autopilot/Squad-dispatch operating rules, release cadence, and methodology in this file are unchanged.

### Priority 1 (bring-forward)

Re-run the **moderately complex app scenario** that previously failed/stalled (e.g. `FitTrackE2E-v10`, stuck ~4hrs) — a genuinely complex generated/selected workflow with a real build+test gate, ending in a live preview URL.

Full lifecycle: dispatch → build/test gate → review gates → merge → reachable preview URL. Root-cause any stall via kubectl logs + App Insights before moving on.

### Then

- **Regression check** — re-run known-healthy recurring projects: FitTrackE2E, BookClubE2E, TrailMixE2E (v3–v10).
- **Generation-quality probes** — test blueprint/agent/workflow generation quality across varied inputs spanning different **disciplines** (software eng, marketing/content, data analysis, ops/DevOps, design, etc.), judging role/agent fit, workflow topology suitability, and gate placement per discipline. Includes:
  - Prompt implying a specific gate (e.g. "I want a human to review before it ships") — is a gate-aware workflow selected?
  - Multi-role prompt (frontend + backend + data + infra) — role/agent assignment breadth.
  - Directly informs **#176** / epic #296.

### Methodology (standing rules)

- **Primary tool:** direct API calls with `gh` bearer token — launch runs, poll `/api/runs/{id}` + `/api/runs/{id}/events`.
- **Always cross-check `kubectl` logs + Application Insights** alongside API state — never conclude root cause from API responses alone.
- **Playwright only for frontend-specific work** — no standing suite; write scenario-specific tests dynamically, headful browser with manual login or reused stored session (never headless/unauthenticated).
- **NEVER approve a human-review/preview gate without live-testing the preview URL first (hard rule, added 2026-07-14 after a real incident).** Root cause: on `ForumHubE2E-v1`, the human-review gate was approved 68 seconds after `coordinator.preview_ready` fired, with no HTTP check against `preview_url` in between — a rubber-stamp approval on the event alone. By the time the user tried the URL, the ephemeral preview pod had already been torn down and the URL 404'd/failed DNS. **Fix going forward:** the moment `coordinator.preview_ready` (or equivalent event/API field) appears with a `preview_url`, issue a real `curl`/HTTP GET against that exact URL and confirm a genuine 2xx response with expected page content (not just "port open") *before* calling the review-approve endpoint. Preview pods are ephemeral and torn down soon after run completion/approval — there is no "check it later," only "check it now, before you approve." This applies to every agent driving E2E scenarios (Smith and any future scenario-driver), and to the coordinator itself when auto-approving on the user's behalf.

```powershell
$token = gh auth token
$base = "https://agentweaver.6a528e9e153d92000129afcb.westus2.staging.aksapp.io"
curl.exe -H "Authorization: Bearer $token" "$base/api/projects" -Method POST -Body (@{prompt="..."} | ConvertTo-Json) -ContentType "application/json"
kubectl logs -n agentweaver <pod> --tail=200
# + Application Insights transaction search on the run's correlation/session ID
```

---

## Release Milestones (periodic semver cadence)

Ship in small, frequent, verifiable increments — don't let fixes pile up uncommitted/undeployed while the harness keeps working.

- **Versioning scheme:** `MAJOR.MINOR.PATCH-rcN` (e.g. `0.9.47-rc1`). Agentweaver stays on the `-rcN` staging-candidate suffix while iterating on staging; drop the suffix only for an actual tagged production release (out of scope for this harness).
- **Patch bump (`0.9.X` → `0.9.X+1`, reset to `-rc1`)** — cut a new milestone whenever a batch of **build-clean + live-E2E-validated** fixes accumulates. Trigger a milestone on whichever comes first:
  - 3–5 issues have been fixed and live-E2E-validated (not just build/unit-verified) since the last release, OR
  - ~60–90 minutes of continuous fix throughput have passed with at least one validated fix pending deploy, OR
  - A live bug Ahmed reported is fixed and validated (ship it immediately, don't batch-wait).
- **Minor bump (`0.9.X` → `0.10.0`)** — when a full workstream/epic (e.g. #288–#298) reaches a coherent, demo-able state, or when a breaking behavior/architecture change ships (e.g. the Kata passthrough sandbox mode change).
- **Milestone procedure** (same as the existing Release Pipeline below, just cadence-gated):
  1. Confirm the batch's issues are individually build-clean AND live-E2E-validated per the Operating Rules above.
  2. Add/review Changesets and follow `release:prepare` when publishing a version.
  3. Deploy local validation with `npm run azure:deploy-from-local`, or publish and deploy a prepared semver release with `npm run azure:release`; then verify with `npm run azure:verify`.
  4. Re-run each fix's live E2E validation against the newly deployed version (a fix isn't "closed" until validated on the version that's actually live).
  5. Close each validated issue with evidence via `gh issue close`.
  6. Log the milestone (version, issues included, validation evidence) via Scribe.
- **Never hold validated fixes back** waiting for an arbitrary "big" release — small frequent patch milestones are preferred over large infrequent ones, so staging always reflects the latest verified state.

---

## Workstream 2: Continuous Epic/Issue Triage

Run in parallel with the harness — not blocking, not one-shot:
- Re-scan open epics (#115, #288–#298 incl. #296) and backlog issues periodically.
- Flag stale/already-fixed items for closure (with evidence).
- Surface re-prioritization candidates as harness findings land (e.g. #176 evidence, #269 root cause).

### New Issue Filing (standing rule)

Whenever the harness or an investigating agent finds a **new, real, reproducible bug** that isn't already tracked (always re-verify staleness first — see Operating Rules):

1. **File it immediately** via `gh issue create` with full evidence (run/project IDs, event sequences, timestamps, root cause if known) — don't just mention it in a decision record and move on.
2. **Nest it under the best-matching existing epic**, don't leave it a floating standalone issue:
   - `#288` — tool approvals / human-input notifications (e.g. #216, #226, #227)
   - `#289` — live run tree, graph, session data coherence (e.g. #306 edge-occlusion)
   - `#290` — outcome-plan confirmation / coordinator messaging trust
   - `#291` — durable progress / resume / transient-failure recovery
   - `#292` — collective assembly review gates
   - `#293` — AgentHost workspace isolation / command execution (e.g. #269, #270, #305, #224)
   - `#294` — preview startup/discovery/availability (e.g. #266)
   - `#295` — MCP CLI/conversational workflows
   - `#296` — workflow generation/authoring/gates
   - `#297` — telemetry/model/session observability (e.g. #250)
   - `#298` — scaling/deployment/release provenance (e.g. #211, #251, #303)
   - Use GitHub sub-issues (`gh issue edit <new> --add-sub-issue-of <epic>` or the sub-issue API — see #136) if configured, otherwise reference in the issue body (`Part of #<epic>`) and comment on the epic linking back.
   - If genuinely no epic fits, that itself is a signal — flag it to Ahmed rather than silently leaving it un-nested.
3. **Never file-and-forget.** A newly filed issue must be immediately picked up (dispatch a Squad agent to fix it in the same batch, or explicitly queue it as the very next item for the next available agent) — filing is the start of the work item's lifecycle, not the end. Track this the same way as pre-existing backlog: it must reach build-clean + live-E2E-validated + deployed + closed like any other issue in this harness's Release Milestones cadence.

---

## Next Steps on Resume

1. Cut the `v0.9.49-rc1` milestone: batch-commit the full pending set (#227/#309, #308, #306, #224, #216, #278, #303, #266-retry — resource-sizing #307 already committed separately at `fcc338bf`), prepare its release metadata, then run `npm run azure:deploy-from-local` for validation or `npm run azure:release` for publication plus first deployment.
2. Live-E2E validate the full batch against the deployed `v0.9.49-rc1`: steering-redirect scoping (#227/#309), reconciler re-arm on `build_test_infra_*` (#308), edge-occlusion rendering (#306), scratch isolation (#224), tool-approval scoping (#216), stop-button confirmation (#278), and re-diagnose #266's stuck-"dispatching" preview stall from scratch (treat prior out-of-band `v0.9.48-rc1` as superseded).
3. Finish #250 validation once a valid bearer token is available for the token-breakdown endpoint.
4. Complete the priority-1 FitTrack-class scenario to a live preview URL (in flight — prior attempts stalled at assembly on #308/#309, now fixed pending deploy) and regression-check BookClub/TrailMix once the batch is live.
5. Re-validate #270 now that #269 is confirmed fixed (root-cause was the same bwrap/Kata gap).
6. Close each validated issue with evidence as the milestone lands; log the milestone via Scribe.
7. Interleave continuous triage passes throughout — don't wait for a "batch" moment. Multiple concurrent Squad sessions may be working this same repo/harness in parallel (shared working tree) — always re-check `.squad/decisions/inbox/` and `git status` for other sessions' in-flight work before assuming a clean slate.

---

## Release Pipeline (confirmed working)

Prepare release metadata → `npm run azure:release` (or `npm run azure:deploy-from-local` for SHA-identified validation) → `npm run azure:verify`.

**Convention reminder:** always re-verify a bug still reproduces on current `main`/staging before "fixing" it.
