# Agentweaver Continuous Validation Plan

_Last updated: 2026-07-14_

## Goal

Run **continuous autopilot validation** of Agentweaver on staging AKS, built around two standing workstreams that run in parallel, indefinitely:

1. **An LLM-powered E2E test harness** — an LLM designs scenarios, drives them via the API + `gh` bearer token, monitors execution, and judges output quality/suitability (not a fixed manual script).
2. **Continuous triage over open epics/issues** — periodically re-scan the backlog for staleness, already-fixed items, and re-prioritization — interleaved with harness work, not a one-time pass.

The coordinator (Squad) approves issue closure, priority, and scope changes only after the user (@sabbour) confirms; fixes are only closed once deployed and validated, with occasional explicit overrides.

---

## Current State (as of v0.9.47-rc1 live; v0.9.49-rc1 batch in flight)

**Deployed & live (last confirmed):** `v0.9.47-rc1` on staging AKS — all 4 workloads Running, `/api/version` = `0.9.47-rc1`.

**Closed this session (2026-07-13/14):** #268, #304, #254, #263, #214, #249, #262, #273, #277, #279, #213, #215 (stale), #269 (Kata bwrap passthrough — live-validated twice, by Morpheus and independently by Smith), #226 (steer redirect at review gate — live-validated on run `18cdc7ce`), #176 (blueprint under-selection — re-repro of the exact original prompt no longer reproduces; generator now produces a specialized, properly-gated workflow).

**Fixed, code-complete, batch-committed, pending v0.9.49-rc1 deploy + live re-validation:** #305 (steering revision-child worktree branch — already deployed in v0.9.47-rc1 per Link/Smith, no new code), #227+#309 (steering-redirect over-broad re-dispatch at a parked assembly gate — `CoordinatorSteeringService.cs`), #308 (reconciler assembly-recovery allowlist drift on `build_test_infra_*` reasons — `AssemblyPlanning.cs`/`CoordinatorReconciler.cs`/`CoordinatorDispatchService.cs`), #306 (Skyler→Hank phantom edge — real frontend edge-occlusion in `routeGridEdges`, `CoordinatorRunPage.tsx`), #224 (per-run AgentHost scratch dir), #216 (tool-approval always-allow URL-keying), #278 (stop-button confirmation dialog), #303 (selective image rebuild via paths-changed diff), #307 (AgentHost pod resource right-sizing — already committed `fcc338bf` on `k8s/sandbox-template-agenthost.yaml` and live-validated by Trinity, autoscale-out confirmed).

**Needs investigation before/at the same milestone:** #250 (token-breakdown case grouping — implemented, E2E-blocked on a 401 needing a valid bearer token to finish validation), #266 (previously "fixed" solo as out-of-band `v0.9.48-rc1` — live validation FAILED, run got stuck "dispatching" and never reached preview; root cause not yet found — **treating `v0.9.48-rc1` as poisoned/superseded, re-including #266's fix fresh in `v0.9.49-rc1` and re-diagnosing the stall**), #270 (preview crash `concurrently` not found — root-caused as a #269 downstream symptom, re-validation dispatched).

**New this session:** #310 filed (catalog gap: no dedicated infra/ops workflow, nested under epic #296) from generation-quality probing.

**Deploy-state note:** `VERSION` file and `/api/version` both read `0.9.47-rc1`; a prior out-of-band push tagged `v0.9.48-rc1` for api+agent-host only (worker never updated) — being superseded, not built on top of. Next milestone cuts `v0.9.49-rc1` fresh with the full batch above.

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
- **Never leave backlog work unassigned while agents sit idle** — when an agent goes idle, immediately hand it (or a fresh agent) the next backlog item. If the next task is genuinely related and benefits from the idle agent's existing context, reuse its session (`write_agent`) and update its description/title to reflect the new task. Otherwise, launch a brand-new agent with fresh context for the unrelated task — but there's no need to actively terminate/delete the idle agent's session first; it's fine to just leave it idle and spin up a new one alongside it.
- **Only the coordinator (Squad) runs the release pipeline.** Fix agents commit their code changes (or leave them staged for the coordinator to include) and report back — they must NEVER independently run `VERSION` bumps, `20-build-push-images.sh`, or `30-deploy.sh`. An agent doing this out-of-band produces inconsistent partial-deploy states (mismatched `VERSION`/`/api/version` vs. actually-running image tags, some workloads updated and others not) — see the v0.9.48-rc1 incident. All deploys happen as a single coordinated Release Milestone (see below).
- **Periodically update this plan's "Next Steps on Resume" section after each batch of work** — after a batch of agent dispatches lands (fixes merged, validations complete, issues filed/closed), update status and what's next so the plan never goes stale relative to reality. Do this *in addition to*, not instead of, continuing to dispatch new work — never let a status-doc update become an excuse to pause dispatching. Self-improve this plan's own wording/structure freely when a better convention is learned (e.g. tightening a rule after a live correction) — but never delete or weaken an existing standing instruction; only add, refine, or append.
- **Never let agent messages/notifications sit queued without action.** The moment an agent goes idle or reports back, immediately triage the result and either close the loop (validate/commit/file issue) or dispatch its next task — don't let idle agents or completed reports accumulate unprocessed while attention drifts to side conversations.

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
  2. Bump `VERSION`, commit with a summary of included fixes (`Fixes #A, #B, #C`).
  3. Build+push images (Git Bash, never WSL) → deploy → `40-verify.sh`.
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

1. Cut the `v0.9.49-rc1` milestone: batch-commit the full pending set (#227/#309, #308, #306, #224, #216, #278, #303, #266-retry — resource-sizing #307 already committed separately at `fcc338bf`), bump `VERSION`, build+push all 4 images (Git Bash), deploy, `40-verify.sh`.
2. Live-E2E validate the full batch against the deployed `v0.9.49-rc1`: steering-redirect scoping (#227/#309), reconciler re-arm on `build_test_infra_*` (#308), edge-occlusion rendering (#306), scratch isolation (#224), tool-approval scoping (#216), stop-button confirmation (#278), and re-diagnose #266's stuck-"dispatching" preview stall from scratch (treat prior out-of-band `v0.9.48-rc1` as superseded).
3. Finish #250 validation once a valid bearer token is available for the token-breakdown endpoint.
4. Complete the priority-1 FitTrack-class scenario to a live preview URL (in flight — prior attempts stalled at assembly on #308/#309, now fixed pending deploy) and regression-check BookClub/TrailMix once the batch is live.
5. Re-validate #270 now that #269 is confirmed fixed (root-cause was the same bwrap/Kata gap).
6. Close each validated issue with evidence as the milestone lands; log the milestone via Scribe.
7. Interleave continuous triage passes throughout — don't wait for a "batch" moment. Multiple concurrent Squad sessions may be working this same repo/harness in parallel (shared working tree) — always re-check `.squad/decisions/inbox/` and `git status` for other sessions' in-flight work before assuming a clean slate.

---

## Release Pipeline (confirmed working)

Bump `VERSION` → git-bash `scripts/aks/20-build-push-images.sh` (async) →
```powershell
$env:TENANT_ID = "72f988bf-86f1-41af-91ab-2d7cd011db47"
$env:IDENTITY_CLIENT_ID = "58c78df1-8cd0-466f-9d70-f150537a203c"
```
then `scripts/aks/30-deploy.sh` → `scripts/aks/40-verify.sh`.

**Convention reminder:** always re-verify a bug still reproduces on current `main`/staging before "fixing" it.
