# Agentweaver Continuous Validation Plan

_Last updated: 2026-07-13_

## Goal

Run **continuous autopilot validation** of Agentweaver on staging AKS, built around two standing workstreams that run in parallel, indefinitely:

1. **An LLM-powered E2E test harness** — an LLM designs scenarios, drives them via the API + `gh` bearer token, monitors execution, and judges output quality/suitability (not a fixed manual script).
2. **Continuous triage over open epics/issues** — periodically re-scan the backlog for staleness, already-fixed items, and re-prioritization — interleaved with harness work, not a one-time pass.

The coordinator (Squad) approves issue closure, priority, and scope changes only after the user (@sabbour) confirms; fixes are only closed once deployed and validated, with occasional explicit overrides.

---

## Current State (as of v0.9.46-rc1 deploy)

**Deployed & live:** `v0.9.46-rc1` on staging AKS — all 4 workloads confirmed, `/api/version` = `0.9.46-rc1`, 23/23 post-deploy checks passed.

**Closed this session:** #268, #304, #254, #263, #214, #249, #262, #273, #277, #279.

**Open — backend/runtime work needed:** #216, #226, #227, #266, #270, #269 (bwrap fix theory pending rubber-duck), #224 (deferred).

**Open — needs live validation:** #213, #215.

**Open — will be tested by the harness, not yet run:** #176 (blueprint/workflow suitability — blueprint gen lacks gate-awareness, under-selects to generic PM workflow), tracked under epic #296.

**Blocked:** Neo's RAI/Scribe sub-run-ID wiring — reverted, no commit. Needs a retry/resumption redesign (attempt-qualified IDs or monotonic resumption) before re-attempting; durable storage's `(RunId, Sequence)` dedup makes naive retry unsafe.

---

## Operating Rules (standing, apply to all workstreams)

- **Always trigger the Squad agent** — never work inline; route every scenario/fix/triage task through Squad's dispatch mechanism.
- **Use Fleet to parallelize as much as possible** — fan out independent scenarios/issues concurrently rather than serializing.
- **Never work on an issue without first validating it's not stale** — re-confirm it still reproduces against current `main`/staging before touching it.
- **Never take shortcuts.** Root-cause fixes only, no symptom-plastering.
- **Don't scope creep** — stay within the requested task; flag adjacent issues rather than silently expanding scope.
- **Model assignment:**
  - **Planning, design, complex debugging** → `gpt-5.6-sol` and `claude-opus-4.8`.
  - **Scoped implementation work** → `gpt-5.6-terra` and `claude-sonnet-5`.
- **Periodically trigger Scribe** to store decisions and perform memory hygiene (dedup, archive stale entries) — don't let this lapse during long harness/triage runs.

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

## Workstream 2: Continuous Epic/Issue Triage

Run in parallel with the harness — not blocking, not one-shot:
- Re-scan open epics (#115, #288–#298 incl. #296) and backlog issues periodically.
- Flag stale/already-fixed items for closure (with evidence).
- Surface re-prioritization candidates as harness findings land (e.g. #176 evidence, #269 root cause).

---

## Next Steps on Resume

1. Launch the priority-1 moderately-complex-app scenario; watch full lifecycle to preview URL; cross-check kubectl + App Insights throughout.
2. Regression-check FitTrack/BookClub/TrailMix.
3. Run discipline-spanning generation-quality probes; compile findings against #176.
4. Get live validation on #213/#215.
5. Rubber-duck the #269 bwrap-removal fix theory before implementing.
6. Pick up `epic-293-implement` when ready.
7. Interleave continuous triage passes throughout — don't wait for a "batch" moment.

---

## Release Pipeline (confirmed working)

Bump `VERSION` → git-bash `scripts/aks/20-build-push-images.sh` (async) →
```powershell
$env:TENANT_ID = "72f988bf-86f1-41af-91ab-2d7cd011db47"
$env:IDENTITY_CLIENT_ID = "58c78df1-8cd0-466f-9d70-f150537a203c"
```
then `scripts/aks/30-deploy.sh` → `scripts/aks/40-verify.sh`.

**Convention reminder:** always re-verify a bug still reproduces on current `main`/staging before "fixing" it.
