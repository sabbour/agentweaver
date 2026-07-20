# Squad Decisions

## 2026-07-16T17-19-26-07-00 — v0.9.68 P0 regression, emergency revert (v0.9.69), and stale-image fix (v0.9.70)

**Status**: RESOLVED & DEPLOYED (v0.9.70 live on staging, 4/4 images provenance-verified, E2E smoke passed).
**By**: Tank (root cause + hotfix), Link (deploy + provenance fix), Coordinator (releases + live verification)
**Refs**: `ee1c8044` (P0 hotfix), `4c276761` (docs-landing merge), `59a90c14` (v0.9.70 version bump)

### What happened

v0.9.68 (previous entry above) shipped Tank's durable rehydration fix alongside re-enabling the
Copilot SDK's native session store (`EnableSessionStore`/`InfiniteSessions`) for
`OperatorAssistantAgent`, on the theory — per github/copilot-sdk#1814 — that the SDK's
"database is locked" issue was scoped to one-shot ephemeral sandbox containers only, and therefore
safe to re-enable for a long-lived in-process agent.

That theory was **wrong for this agent** and caused a live P0 within minutes of the v0.9.68 deploy:
every new assistant run immediately failed with
`System.InvalidOperationException: Session error: Execution failed: Error: database is locked`.

### Root cause

`OperatorAssistantAgent.RunTurnAsync` creates a **brand-new Copilot SDK session on every turn**
(calls `agent.CreateSessionAsync()`, never resumes an existing session — no
`ResumeSessionAsync`-equivalent path exists for this agent). With `EnableSessionStore` turned on,
every turn of every concurrent conversation in a pod hammers the **same pod-local SQLite
session-store file** with a fresh session-create, causing lock contention.

This means the "one-shot ephemeral containers only" framing from copilot-sdk#1814 was
**incomplete**: any workload that creates many concurrent fresh SDK sessions against one local
SQLite file hits this, not just one-shot sandboxed containers. The real qualifying condition is
"does this agent resume sessions or always create new ones," not "is this a one-shot pod."

### Fix (v0.9.69 hotfix)

Emergency-reverted `EnableSessionStore`/`InfiniteSessions` back to `false`/disabled in
`OperatorAssistantAgent.BuildSessionConfig`
(`packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs`), with an updated code comment
recording the real mechanism (quoted below) so the mistake isn't repeated:

> `#1814 / v0.9.68 REGRESSION (reverted)`: EnableSessionStore/InfiniteSessions were briefly
> flipped to true here on the theory that #1814's "database is locked" only affects
> one-shot/ephemeral sandbox workloads, not a long-lived in-process agent. That theory was
> wrong for THIS agent: RunTurnAsync creates a brand-new SDK session on EVERY turn (it never
> resumes one), so enabling the store means every turn, across every concurrent conversation in
> this pod, hammers the SAME pod-local SQLite session file — exactly the concurrent-write
> contention #1814 describes. Reverted to false/disabled. Re-enabling this safely would require
> first switching RunTurnAsync to actually resume the deterministic SessionId across turns (like
> CopilotAIAgent.ResumeSessionAsync) — out of scope for this hotfix. Durable rehydration in
> AssistantRunService (from the persisted RunEvents log) is unaffected and remains the correct
> fix for cross-pod/idle-timeout/restart continuity.

Committed **directly to `main`** (commit `ee1c8044`) — no worktree — per P0 emergency-fix
convention. Shipped as v0.9.69.

### Separate finding: two false rollout-failure alarms (operational learning, not a regression)

`scripts/aks/30-deploy.ps1` reported "API deployment rollout failed" (exit 1) **twice** in this
session — once deploying v0.9.69, once again deploying v0.9.70. Both were **false alarms**:
`kubectl` events showed transient `FailedScheduling` (insufficient CPU / untolerated taints on
some nodes) delaying new-pod scheduling by roughly 1-2 minutes beyond the script's wait timeout,
compounded by normal image-pull time. Pods reached `1/1 Running/Ready` shortly after, and a manual
`kubectl rollout status` re-check confirmed success both times. This is **not a code regression** —
it's a cluster capacity/scheduling-latency-vs-script-timeout mismatch.

**📌 Reusable operational pattern:** when `30-deploy.ps1` (or `.sh`) reports rollout failure, do
NOT assume a real break — first manually check `kubectl get pods` / `kubectl rollout status`
before escalating. The deploy script's wait timeout is tighter than worst-case pod-scheduling +
image-pull latency under transient node pressure; a script exit-1 here is a known false-failure
mode, not proof the deployed image/config is broken.

### Separate finding: stale-image regression from an unrelated docs merge (#251 failure mode, caught and fixed)

Per explicit user request, the long-lived local branch `merge-docs-landing-main` (docs landing
page redesign: VitePress theme, `LandingWorkflowDemo.tsx`, `deploy-docs.yml`) was merged into
`main` via `git merge --no-ff` (commit `4c276761`). This merge touched `apps/web/src` paths
**after** the v0.9.69 images had already been built from an earlier commit, which made the
already-deployed `agentweaver-frontend:v0.9.69` image provenance-stale.
`scripts/aks/25-verify-image-provenance.ps1` correctly caught this: "STALE IMAGE (this is exactly
the #251 failure mode)". Fixed by bumping to v0.9.70 (commit `59a90c14`) and rebuilding **only**
the frontend image; api/mcp/agent-host were correctly retagged as unchanged against the new HEAD.

**Why this matters as a pattern:** merging unrelated feature branches into `main` after images are
already built — even completely disjoint work like a docs redesign — can silently invalidate the
already-deployed image's provenance if the merge touches any watched path. Provenance verification
caught it here exactly as designed (#251 precedent); the reusable takeaway is to run
`25-verify-image-provenance` after **any** merge to `main` that lands after a build, not only after
merges that look feature-related to the deployed service.

### Final verification (v0.9.70)

- All 4 images (api, frontend, mcp, agent-host) rebuilt/retagged as appropriate and confirmed
  **4/4 provenance-verified** against the correct source commit, with no drift in watched paths.
- Live E2E smoke test via real API calls: `POST /api/assistant/runs` then
  `POST /api/assistant/runs/{id}/messages` for a second turn — both turns succeeded.
- Log line `"Rehydrated operator run ... from durable storage (2 history messages restored)"`
  confirmed in production, proving the v0.9.68 durable-rehydration feature (the correct, unaffected
  part of that release) works live.
- `kubectl logs` grep for `"database is locked"` / `"InvalidOperationException"` / `"Session
  error"` across the trailing 5 minutes returned **zero matches** — P0 regression confirmed
  resolved.
- Test run cleaned up via `DELETE /api/runs/{id}`.

### Outstanding follow-up (queued, not started)

Implement **real SDK session resumption** for `OperatorAssistantAgent`: change `RunTurnAsync` so
turn 2+ resumes the existing SDK session (deterministic `SessionId=agentweaver-operator-
{conversationId}`, already computed in `BuildSessionConfig`) instead of always calling
`CreateSessionAsync()`, mirroring `CopilotAIAgent.ResumeSessionAsync`. Only once that lands and is
tested should `EnableSessionStore`/`InfiniteSessions` be safely re-enabled again for
`OperatorAssistantAgent`. This is the next real piece of work on this thread — assign to Tank when
picked up; do not re-enable the session store before the resumption fix ships.

---

## 2026-07-16T19-15-00Z — Assistant session persistence and UI — v0.9.68 release

**Status**: MERGED & DEPLOYED (v0.9.68 live on staging)
**By**: Tank, Trinity, Coordinator (merge & release)
**Branch leads**: 
  - Tank: `feat/assistant-session-recall` (commit `dfc5c2e7`) — durable session rehydration + SDK session store re-enable
  - Trinity: `feat/assistant-delete-run` (commit `4e78744`) — delete action on Sessions page
**Coordinator merge**: `origin/main` fast-forward to commit `79f0d393`, VERSION bumped to v0.9.68, built+pushed all 4 images, deployed to staging, all 4 images pass provenance check.

### Tank: Operator assistant durable session recall

**Problem**: When an operator-assistant conversation fell out of the in-memory `_runs` cache
(idle timeout after 30 min, pod restart, cross-pod routing on 2-replica deployment), the conversation was permanently lost — `RunTurnAsync` would return 404 and new messages couldn't be sent.

**Decision**: On cache-miss, rehydrate from durable `RunEvent` storage:
1. Added `IRunEventStream.GetPersistedEventsAsync(runId, fromSequence)` — a point-in-time durable read that rebuilds `OperatorRunState.History` from persisted `agent.message` events (capped at 24, most-recent-first).
2. Restore `ProjectId`/`ModelId` from persisted `Run` record; transition status from `Completed` back to `InProgress` if messages arrive after idle close.
3. Rehydration does NOT count against `MaxConcurrentRunsPerUser` quota (only new-start does), so existing conversations remain resumable even when the user's concurrency limit is occupied.
4. **Architectural fact recorded**: the Copilot SDK's `EnableSessionStore` / `InfiniteSessions` flags (which we had disabled via copy-paste from one-shot sandboxed agents) are safe to re-enable ONLY for long-lived in-process agents like the operator assistant. Those same flags must remain `false` for one-shot/ephemeral agents (one-shot pods churn local SQLite unnecessarily). The disable on those was documented vs. a real bug (github/copilot-sdk#1814), making the risk/benefit calculation different per agent type.

**Files changed**: `AssistantRunService.cs`, `IRunEventStream.cs`, `SqliteRunEventStream.cs`, `EfRunEventStream.cs`, `OperatorAssistantAgent.cs`, `AssistantRunEndpointsTests.cs`.

**Validation**: 24/24 targeted tests passed.

### Trinity: Sessions page with delete action

**Problem**: No list endpoint for past assistant conversations; no way to delete/archive old runs.

**Solution**: Reused existing generic `apiClient.deleteRun(runId)` (already calls the shared endpoint, no backend work needed since assistant runs live in the shared run store).
- New `SessionsPage.tsx`: lists user's conversations via Tank's `GET /api/assistant/runs` endpoint (added as a backend change — see Tank's final state below).
- Added delete button + confirm dialog per row.
- Updated `LeftNav` to surface Sessions at `/projects/:projectId/sessions`.

**Files changed**: `SessionsPage.tsx`, `SessionsPage.test.tsx`.

**Validation**: 5/5 tests passed, eslint/tsc clean.

### Backend: `GET /api/assistant/runs` endpoint

Tank added `GET /api/assistant/runs` to `AssistantEndpoints.cs` (lists caller's active/past conversations: `run_id`, `status`, `title`, `created_at`, newest-first). This was required for Trinity's Sessions page.

### Coordinator merge and v0.9.68 release

- Merged both `feat/assistant-session-recall` and `feat/assistant-delete-run` into main at `79f0d393` (no conflicts, disjoint files).
- VERSION 0.9.67 → 0.9.68, committed, annotated tag created, GitHub release published.
- All 4 images built fresh (api, frontend, mcp, agent-host).
- Deployed to `agentweaver-aks-2` via `scripts/aks/30-deploy.sh`.
- Provenance verification: `scripts/aks/25-verify-image-provenance.sh` returned 4 passed, 0 failed against commit `79f0d393`.
- Worktrees `aw-wt-assistant-recall` and `aw-wt-assistant-delete` removed post-merge.

### Why this matters

The assistant is now conversation-persistent across pod restarts, redeployments, and cross-replica routes — a fundamental reliability improvement for long-running troubleshooting sessions. Users can close their browser and come back hours later, click a saved run ID or find it in the Sessions list, and the conversation continues from exactly where it was. The SDK session-store re-enable optimizes the hot-path (repeated short calls benefit from in-memory overhead reduction) while Part 1's durable rehydration provides the actual correctness guarantee across pod boundaries.

---

## 2026-07-14T15-39-56-07-00 — Active decisions reset after size gate

**By:** Scribe  
**What:** Archived the previous active decisions snapshot to `decisions/archive/2026-07-14T15-39-56-07-00-pre-harness-archive.md` before merging the current inbox because `decisions.md` had grown to 340824 bytes.  
**Why:** The active decisions file exceeded the 50KB / 7-day size gate. Historical entries remain preserved in the archive snapshot; the active file now carries only the current batch onward.

---

**Merged from inbox file:** `link-release-script-acr-retag-fix.md`

# Decision: release.sh ACR retag-source bug fix + release v0.9.53 — 2026-07-14

**Status**: DECIDED (bug — fix implemented, merged, released)
**By**: Link (Platform Engineer)
**Refs**: origin/main `9f9b947e`; fix commit `e2322372`; tag `v0.9.53`
**Trigger**: Ephemeral staging recreation (empty ACR) + batched patch release of 7 bugfixes
(#314, #315, #317, #267, #242, #240, #318) merged at `f211cd37`.

## Context

After the staging environment (`agentweaver-rg`) was recreated from scratch, the container registry
`agentweaverregistry` was **empty**. Running `bash scripts/release.sh patch` to ship v0.9.53 would
have failed partway: the script classifies each image as *build* (sources changed since last tag) or
*retag* (unchanged → `az acr import --source ACR/IMAGE:<LAST_TAG>`). For v0.9.53 the frontend was
UNCHANGED since v0.9.52, so the script would try to retag from `agentweaver-frontend:v0.9.52` — an
image that does not exist in the freshly recreated (empty) ACR — and abort **after** already pushing
the version bump, tag, and GitHub release. That leaves a published tag with no images.

## Root cause

`release.sh`'s build-vs-retag decision only considered `git diff` since the last tag. It assumed the
retag *source* image always exists in ACR. That assumption is false whenever the ACR has been
recreated / emptied (a periodic event for ephemeral staging), making the "unchanged → retag"
optimization unsafe.

## Resolution (Option A — principled root-cause fix)

Fixed `scripts/release.sh` (commit `e2322372`, merged to `origin/main`):

- Added `acr_source_tag_exists(image, tag)` → `az acr repository show --name ACR --image image:tag`
  (exit 0 = present).
- Added `image_needs_build(image, paths)` → build when no baseline tag, OR sources changed, OR the
  retag source tag is **absent** from ACR.
- Image loop gains an `elif ! acr_source_tag_exists ...` branch that builds fresh instead of failing
  when the retag source is missing. The normal case (source tag present) still retags — the
  optimization is preserved; behavior only changes when the source is genuinely absent.
- Frontend-dist guard updated to use `image_needs_build` so `dist/` is prepared whenever the frontend
  will be built.

Verified `az acr repository show` returns non-zero for a nonexistent tag and zero for an existing tag
against the real `agentweaverregistry`. `bash -n` clean.

## Release v0.9.53 outcome

- VERSION 0.9.52 → 0.9.53, committed `9f9b947e`, annotated tag `v0.9.53`, GitHub release created.
- ACR empty ⇒ all 4 images built fresh (api, frontend, mcp, agent-host) at `v0.9.53` — the fix worked
  as intended (frontend correctly built rather than failing a retag).
- Deployed to `agentweaver-aks-2` via `scripts/aks/30-deploy.sh`. `40-verify.sh`: 22/22 infra checks
  pass; the public `/api/projects` probe returned 401 (expected, unauthenticated) after gateway DNS
  propagation. Live image confirmed: `agentweaverregistry.azurecr.io/agentweaver-api:v0.9.53`.
- `origin/main` fast-forwarded `e2322372 → 9f9b947e`.

## Second latent bug found during deploy (openssl -subj / MSYS path mangling)

`scripts/aks/gen-a2a-mtls-certs.sh` calls `openssl req ... -subj "/CN=agentweaver-a2a-ca/O=agentweaver"`
(three such invocations). Under Git Bash (MSYS) on Windows, the leading-slash `-subj` argument is
mangled into a Windows path (`C:/Program Files/Git/CN=...`), so `openssl` rejects it and
`30-deploy.sh` fails at the A2A mTLS cert step. Worked around for this run by exporting
`MSYS2_ARG_CONV_EXCL="/CN="` (excludes only the openssl subj args from MSYS conversion while leaving
`kubectl -f /c/...` paths converted normally) — the generated certs are correct.

**Recommended permanent fix** (not yet applied — flagging for decision): make the script portable,
e.g. prefix the subj with an empty leading component (`-subj "//CN=.../O=agentweaver"`), or set the
MSYS exclusion inside the script. This will otherwise bite on every Windows/Git-Bash deploy.

## Operational notes / gotchas (for future recreations)

- `MSYS_NO_PATHCONV=1` is required for the **infra** scripts (they pass `/subscriptions/...` args) but
  BREAKS git (`fatal: Invalid path '/c'`); the **release/deploy** path must run WITHOUT it — except
  the openssl `-subj` args, which need the narrow `MSYS2_ARG_CONV_EXCL="/CN="` exclusion above.
- Manual follow-up owned by Ahmed: update the GitHub OAuth App callback URL to the recreated staging
  ingress host for browser-based login.

---

**Merged from inbox file:** `Morpheus-added-persistent-learnings-md-persona-catalog-memo.md`

### 2026-07-14T21-28-22: Added persistent learnings.md + persona catalog memory for the harness system
**By:** Morpheus
**What:** Added persistent learnings.md + persona catalog memory for the harness system
**References:** scripts/harness-shared/learnings.md, scripts/harness-shared/record-learning.mjs, scripts/persona-briefs/catalog.json, scripts/persona-briefs/find-similar.mjs, .github/agents/harness.agent.md, scripts/persona-briefs/SKILL.md
**Why:** **Task**: Build lightweight, structured persistent memory for the harness system so a future Harness run (or agent session) doesn't have to rediscover the same facts by reading source/logs — following directly from this session's MCP stdio target-guard bug, the `/mcp` endpoint + OAuth fact, and the `priya.api` intentional-stop-at-gate insight all being manually rediscovered.

**Design decisions**:

1. **`learnings.md` as Markdown, not JSON.** Chose Markdown over JSON for `scripts/harness-shared/learnings.md` because its primary consumer is a reasoning agent reading it at the start of a run (per the new instruction in `harness.agent.md`) — Markdown is directly skimmable without a parse step, and diffs/reviews cleanly in PRs. To keep it script-appendable/query-able despite being prose, I standardized a strict per-entry shape (`## <title>` heading, then `- date:`/`- category:`/`- surface:`/`- status:` metadata lines, then a free body, entries separated by `---`) and wrote `record-learning.mjs` with a small regex-based parser (`parseEntryTitles`) for dedup rather than a full markdown AST — sufficient because the only structured query needed today is "does this title already exist."

2. **Dedup key = exact title (case-insensitive).** Simpler than fuzzy/semantic dedup and avoids false-positive rejections of genuinely distinct facts; the cost (a near-duplicate with a slightly different title could slip through) is acceptable for a human/agent-curated log where titles are already deliberately chosen to be distinct.

3. **`catalog.json` as JSON, not Markdown** — the opposite choice from learnings.md — because its primary consumer is `find-similar.mjs`, a script that needs structured fields (`tags`, `surfaces`, `runsToCompletion`) to score against, not prose a human reads top-to-bottom. It's hand-curated (not script-appended) since new entries are rare (one per reviewed persona/adapter) and require human judgment about tags/description; a future append-helper could be added if volume grows.

4. **Matching algorithm for `find-similar.mjs`: cheap tag/keyword overlap, not embeddings or an LLM call.** Per the task's explicit ask ("cheap keyword/tag match... no LLM call needed"). Tokenizes the query and each catalog entry's `id`+`description`+`tags`, scores tag-token matches at 2x weight vs. description-token matches at 1x (tags are curated signal, description is free text), and returns entries with score > 0 sorted descending. Verified this correctly ranks a "ticket triage severity support" query to `priya` and a "competitive marketing brief" query to `maya` in both a fixture-catalog unit test and against the real checked-in `catalog.json`.

5. **Scope discipline**: did not touch `generate-core.mjs`/`generate-adapter.mjs`'s actual generation logic — only added the catalog-check step as a prior instruction in `harness.agent.md` and `persona-briefs/SKILL.md` (run `find-similar.mjs` first, generate only if nothing close exists). The generators themselves are unchanged.

**Incidental fix**: `scripts/persona-briefs/test/index.test.mjs` had a pre-existing failure unrelated to this task — `listPersonas()` assertions didn't yet include the `lena` persona (added in an earlier session, api-only adapter). Fixed as a one-line expectation update since it blocked getting a clean test baseline for this task and was trivially low-risk.

**Verification**: `scripts/persona-briefs` test suite 16/16 passing (7 new: `find-similar.test.mjs`), `scripts/harness-shared` test suite 10/10 passing (7 new: `record-learning.test.mjs`), `scripts/mcp-harness` test suite re-run unaffected at 11/11.

**Commit**: `159252c8` on main — "feat(harness): add persistent learnings + persona catalog memory".

---

**Merged from inbox file:** `morpheus-rai-verdict-rationale-fix.md`

# RAI verdict card leaking raw JSON as rationale — root cause & fix

**Author:** Morpheus (Runtime Engineer)
**Date:** 2026-07-14
**Branch:** fix/rai-verdict-rationale-json-leak
**Merged commit:** 25e46212366f25edc50b7a6fffd1eeef8c4b5e89 (fast-forwarded onto origin/main, base 9f9b947e)

## Bug report

Coordinator run UI's "RAI verdict" card showed raw JSON as the rationale text, e.g.
`RAI verdict: 🟢 — [{"title":"Analyze and classify the five support tickets", ...}]`.

## Diagnosis (confirmed, with one correction)

File: `packages/Agentweaver.AgentRuntime/Workflow/RaiTurnExecutor.cs`, `ExtractRationale`.

Confirmed: `ExtractRationale`'s fallback path (`return TruncateOneLine(line);`, previously
~line 548) blindly returned the FIRST non-blank, non-sentinel line of the reviewer's raw
response with **zero validation** that it was natural-language prose. When the Rai model's
response was (or started with) a JSON-shaped blob — most likely because it echoed a
structured work-plan/diff back instead of writing prose — that raw JSON was surfaced
verbatim as the "rationale" in the UI.

Correction to the original hypothesis: there is only **one** RAI check type/prompt in this
codebase (`RaiTurnExecutor.HandleAsync` reviews `AgentTurnOutput.Diff`) — there is no
separate "check category" with its own ambiguous prompt. The single existing prompt already
instructed a `VERDICT: <TOKEN>` sentinel format, but did not explicitly forbid echoing raw
structured input (e.g. a JSON work-plan diff) back as the "explanation" portion of the
response, which is the likely trigger for this turn's non-conforming output.

Additional finding beyond the original report: `HandleAsync`'s bounded re-ask path
(triggered when the first response has no parseable `VERDICT:` sentinel) calls
`EmitVerdict(writer, subWriter, input.RunId, verdict, response)` using the **original**
non-conforming `response`, not the re-ask's response, for rationale extraction. So even when
the verdict itself was correctly recovered via the sentinel-only re-ask, the rationale was
still extracted from the very response that had already failed to produce a valid sentinel —
making the JSON-leak fallback path reachable via two routes (a JSON response with a trailing
sentinel that parses fine, AND a JSON response with no sentinel that only recovers a verdict
via re-ask). Both routes are covered by new regression tests.

## Fix

1. **Prompt hardening** (`RaiTurnExecutor.cs`, the `task` prompt template): added an explicit
   instruction that the explanation must be plain prose for a human reader, and must never
   quote/echo raw JSON, code blocks, or other structured data verbatim — even when the diff
   itself is structured data (e.g. a JSON work plan) — describing it in the reviewer's own
   words instead.
2. **Defense-in-depth in `ExtractRationale`** (regardless of prompt behavior): added
   `LooksLikeJson(string)` — true if the candidate starts with `{`/`[`, or fully parses via
   `JsonDocument.Parse`. Before the naive first-line fallback returns a candidate line, it is
   checked with `LooksLikeJson`; if true, the loop breaks and the method falls through to the
   existing per-verdict default message (e.g. "RAI reviewer completed without a written
   rationale.") instead of ever surfacing raw JSON.

## Tests added

`tests/Agentweaver.Tests/RaiVerdictParserTests.cs`:
- `HandleAsync_RawJsonResponseWithSentinel_DoesNotLeakJsonAsRationale` — JSON response with a
  valid trailing `VERDICT: GREEN` sentinel; asserts the emitted rationale contains no `{` and
  none of the echoed JSON text.
- `HandleAsync_UnparseableJsonThenSentinel_RecoversViaReask_DoesNotLeakJsonAsRationale` — JSON
  response with NO sentinel (triggers the bounded re-ask), re-ask recovers `VERDICT: GREEN`;
  asserts the rationale (extracted from the original JSON response per current re-ask
  behavior) still does not leak the JSON, now that the defensive fallback guards it.

All 29 tests in the `RaiVerdict` filter pass (27 pre-existing + 2 new).

## Validation

```
dotnet restore agentweaver.sln
dotnet build agentweaver.sln --no-restore   # Build succeeded, 0 errors
dotnet test tests\Agentweaver.Tests\Agentweaver.Tests.csproj --no-build --filter "FullyQualifiedName~RaiVerdict"
# Passed! - Failed: 0, Passed: 29, Skipped: 0, Total: 29
```

## Merge

Worked in a fresh worktree `C:\Users\asabbour\Git\agentweaver-rai-fix` on branch
`fix/rai-verdict-rationale-json-leak` off `origin/main` (base `9f9b947e`), to avoid touching
the main worktree (which had unrelated uncommitted staged work from another session).
Re-fetched `origin/main` immediately before pushing (no concurrent pushes from Tank/Trinity
landed in the window); fast-forward push:
`git push origin fix/rai-verdict-rationale-json-leak:main` → `9f9b947e..25e46212`.

Final commit merged to main: `25e46212366f25edc50b7a6fffd1eeef8c4b5e89`.

---

**Merged from inbox file:** `Seraph-default-harness-judge-agent-native-task-tool-judge.md`

### 2026-07-14T21-51-48: Default harness judge: agent-native (task tool + judge.agent.md), not a subprocess wrapper around the copilot CLI
**By:** Seraph
**What:** Default harness judge: agent-native (task tool + judge.agent.md), not a subprocess wrapper around the copilot CLI
**References:** scripts/harness-judge/core.mjs, scripts/harness-judge/save-verdict.mjs, .github/agents/judge.agent.md, .github/agents/harness.agent.md, scripts/api-harness/README.md, scripts/harness-shared/learnings.md
**Why:** ## Problem

`AGENTWEAVER_JUDGE_CMD` (consumed by `scripts/harness-judge/core.mjs`'s `makeDefaultJudge()`/`makeCommandJudge()`) has never been configured anywhere in this repo or environment. Every real harness run to date — including the two live smoke runs earlier this session — has only ever produced the safe `CANNOT_DETERMINE` fallback verdict. The judge's core evaluative feature (an LLM judging quality/frustration/pushback from evidence) has never actually executed.

## First approach (built, then discarded)

I initially built `scripts/harness-judge/default-judge-cli.mjs`: a Node wrapper that would read a prompt from stdin and `spawnSync('copilot', [...lockdown flags], { input: prompt })`, to be set as the `AGENTWEAVER_JUDGE_CMD` default.

To lock it down I ran `copilot --help` / `copilot help permissions` and tested live:
- `--available-tools=` (empty) and `--available-tools=<bogus-value>` did **not** block tool execution — I proved this by asking the CLI to run `whoami` and write a file, and it did both, despite the docs describing `--available-tools` as a visibility filter.
- `--allow-all-tools` (docs: "required for non-interactive mode") auto-approves everything, including destructive actions — confirmed by the same test.
- The only flag that reliably blocked action-capable tools in live testing was explicit `--deny-tool='shell' --deny-tool='write' --deny-tool='url'`: the CLI returned a clean text explanation instead of executing, with exit code 0, no hang.
- Even with that working lockdown, this design left an unavoidable residual gap: I could not enumerate or block MCP servers registered in the *caller's* ambient `~/.copilot/mcp-config.json`, since `--deny-tool` for MCP requires a specific server name I don't control. It also meant maintaining a second full CLI process's worth of manually-verified flags as an ongoing security-review surface, re-verified by hand against every `copilot` CLI upgrade.

## Ahmed's correction: agent-native design (what shipped)

Discarded the subprocess wrapper entirely. Instead:

1. **`.github/agents/judge.agent.md`** ("Judge") — a new custom agent with `tools: []` in its frontmatter. This is a platform-level guarantee, not a manually-verified flag combination: the agent has no file/shell/network/MCP tool access at all, structurally, regardless of what's in the (possibly untrusted/adversarial) evidence it's asked to judge. The judging methodology (P0/P1 discipline, frustration/pushback rules, the three surface appendices JUDGE.api.md/JUDGE.ui.md/JUDGE.mcp.md) is baked into its system prompt as standing knowledge.
2. **`scripts/harness-judge/core.mjs --prompt-out`** already worked standalone without requiring a judge command configured — verified by running it against a real evidence fixture; it wrote a complete, self-contained prompt and only the (separate, expected) fallback verdict path touches `CANNOT_DETERMINE`. No code change was needed for this.
3. **`scripts/harness-judge/save-verdict.mjs`** (new) — parses the Judge subagent's raw text response with `parseVerdictText()`, validates it with `validateVerdict()`, and either writes the validated verdict or falls back to `buildFallbackVerdict()`'s schema-valid `CANNOT_DETERMINE`. No subprocess involved.
4. **`.github/agents/harness.agent.md`** updated: added `task` to its `tools:` list (previously `['execute']` only, now `['execute', 'task']`) and a new "Judging" section describing the flow: build prompt → dispatch via `task` tool with `agent_type: "Judge"` (sync) → parse/validate/persist via `save-verdict.mjs`. `AGENTWEAVER_JUDGE_CMD` is now documented as the secondary path only, for headless/CI contexts with no agent session to dispatch a `task` call from.

## Why agent-native beats the subprocess wrapper

- No nested CLI process to spawn, monitor, or timeout-manage.
- No hand-verified permission flags to keep in sync with `copilot` CLI releases — the platform's own custom-agent `tools: []` scoping is the security boundary, and it isn't subject to the `--available-tools`/`--allow-all-tools` surprises I found while testing the CLI directly.
- No residual "unknown ambient MCP server" gap: `tools: []` is agent-config-level, not permission-flag-level, so no other tool of any kind (including MCP) is exposed to that agent regardless of ambient config.
- Verified end-to-end via `copilot --agent judge` (a stand-in for the platform's own agent registry, since custom agents load at session start and a fresh in-session `task` call couldn't discover the new file without a restart) with a real evidence fixture (persona `priya`, `priya-ticket-triage` scenario): got back a real, schema-valid `PASS`/`PASS` verdict grounded in cited turn evidence — not `CANNOT_DETERMINE` — confirmed independently against `verdict-schema.mjs`'s `validateVerdict()` (`{ ok: true, errors: [] }`). Notably this test did **not** pass any `--deny-tool`/`--allow-*` flags at all; the agent still took no action, which is exactly the platform-level guarantee this design relies on instead of manual flag verification.
- `scripts/harness-judge` test suite: all 10 existing tests still pass (`npm --prefix scripts/harness-judge test`), confirming no regression to `core.mjs`'s existing behavior.

## Known residual gap

Within *this* session I could not get the in-process `task` tool to resolve `agent_type: "Judge"` — custom agents from `.github/agents/*.agent.md` are evidently registered at session start, not hot-reloaded (`extensions_reload` only affects `.github/extensions/`, not custom agent definitions). Sabbour/Harness should confirm `task`/`agent_type: "Judge"` resolves correctly the next time an agent session starts fresh with this commit checked out — I verified the agent definition works correctly via the equivalent `copilot --agent judge` CLI invocation instead, which exercises the same underlying agent file and tool-scoping mechanism.

---

**Merged from inbox file:** `tank-run-event-timestamp-fix.md`

# Tank — RunEvent timestamp fix (Coordinator timeline "just now" bug)

**Status:** Fix complete and validated locally. NOT merged/pushed — coordinator will merge all
three agents' branches into local main. Branch left untouched per correction from Ahmed.

**Branch:** `fix/run-event-timestamp`
**Worktree:** `C:\Users\asabbour\Git\agentweaver-ts-fix`
**Commit:** `07f0a48068fa6ed4a7f9f53e173fff82e90457dd`
**origin/main:** confirmed back at `9f9b947e5f4c72a0e7b7c0f3384233506c41619b` (pre-fix) after
force-push revert — my fix commit above was never re-pushed after the revert.

## Root cause (confirmed diagnosis)

- `packages/Agentweaver.Domain/RunEvent.cs` defined `RunEvent` as
  `record RunEvent(int Sequence, string Type, object Payload)` — no timestamp field anywhere.
- `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs` `RecordNext`/`Record` never stamped a
  server-side UTC time when appending an event — only a monotonic `Sequence`.
- Some individual payloads embed their own `timestamp_utc` string by convention (e.g.
  `workflow.step`, sandbox/preview events), but most — including coordinator narration events like
  `coordinator.work_plan` — do not.
- Frontend `apps/web/src/components/AgentSessionPanel.tsx` `readTimestamp()` reads only
  `payload.timestamp_utc`/`timestampUtc`/`timestamp`, and `turnsToTimelineModel` falls back to
  `Date.now()` at RENDER time when absent. Because that fallback re-evaluates on every re-render,
  timestamp-less events showed "just now" and kept changing.

Diagnosis was correct as given; no corrections needed.

## Fix (backend root cause, no required frontend change)

1. `RunEvent` gains an optional `DateTimeOffset TimestampUtc = default` positional parameter —
   backward compatible with ~130 existing `new RunEvent(...)` call sites across production code
   and tests (no signature-breaking changes needed).
2. `RunStreamStore.RecordNext` (both overloads) and `Record(RunEvent)` now stamp
   `DateTimeOffset.UtcNow` at the moment the event is appended to `_history` — the single source
   of truth for "when it happened", overriding whatever the caller passed in (or didn't).
3. `EndpointHelpers.WriteSseEventAsync` (SSE wire format) now runs the payload through a new
   `StampTimestamp(RunEvent)` helper that merges a `timestamp_utc` (ISO-8601 "O") key into the
   serialized JSON *only if* the payload doesn't already carry
   `timestamp_utc`/`timestampUtc`/`timestamp` — existing per-emitter timestamps are left
   untouched, exactly matching the key `readTimestamp()` already reads. No frontend change was
   needed.
4. `GET /api/runs/{id}/events` (REST replay/seed endpoint) updated the same way, sourcing the
   timestamp from the persisted `RunEventRecord.CreatedAt` column so a finished run's timeline
   reflects when each event actually happened, not "now" at fetch time.
5. `SqliteRunEventStream` / `EfRunEventStream` now persist `RunEvent.TimestampUtc` as the durable
   `CreatedAt` column (instead of an independent second `DateTime.UtcNow` at persist time) and
   restore it into `RunEvent.TimestampUtc` on replay/subscribe, so reconnects and REST replay both
   see the original append time.

## Validation

- `dotnet build agentweaver.sln --no-restore` — 0 errors.
- Added tests:
  - `RunStreamStoreTests.Record_StampsNonDefaultMonotonicUtcTimestamp_OnEveryEvent` — verifies
    `Record`/`RecordNext` assign a non-default, monotonic-or-equal UTC timestamp.
  - `RunEventTimestampSerializationTests` (new file) — verifies `EndpointHelpers.StampTimestamp`
    adds the key when missing, never overwrites an existing emitter-supplied `timestamp_utc`, and
    falls back to "now" for legacy events with a default `TimestampUtc`.
- Targeted run (`RunStreamStoreTests`, `RunEventTimestampSerializationTests`,
  `SqliteRunEventStreamTests`, `EfRunEventStreamTests`): 31/31 passed.
- Related suite run (`StreamEndpointTests`, `CrashSafeReplayTests`,
  `CoordinatorChildObservationTests`, `UnboundedLogTests`): 29/29 passed.
- Full `dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj --no-build`: 2207 passed,
  33 skipped, 1 failed (`RemoteAgentProxyDeadlineTests.QuietTurn_LongerThanFormerIdleWindow_ButWithinConfiguredIdle_Completes`)
  — a pre-existing timing-sensitive/flaky test unrelated to this change; re-run in isolation and it
  passed (11/11), confirming it is not caused by this fix.

## Merge note for coordinator

This branch is rooted at `origin/main@9f9b947e` (release commit v0.9.53). It was briefly pushed
directly to `origin/main` and then force-reverted back to `9f9b947e` per Ahmed's correction — the
fix itself was never lost, it remains on `fix/run-event-timestamp` at `07f0a480` in the worktree
above, ready for the coordinator to merge alongside Trinity's and Morpheus's branches.

---

**Merged from inbox file:** `trinity-coordinator-timeline-ui-fixes.md`

# Trinity — Coordinator run-panel UI bug fixes

**Branch:** `fix/coordinator-timeline-ui` (worktree: `C:\Users\asabbour\Git\agentweaver-ui-fix`)
**Final local commit SHA:** `4e78744176da04ddbb8a80ab33ece91204ef9cb5`
**Status:** Committed locally only. NOT pushed to origin — per Ahmed's explicit correction, the
coordinator (Ahmed) will merge Tank/Trinity/Morpheus's branches into local main himself once all
three report back, to avoid concurrent branch-pointer races.

## Scope
Fixed 3 independent UI bugs reported by Ahmed (with screenshots) in the Coordinator run panel:
`apps/web/src/components/AgentSessionPanel.tsx`, `apps/web/src/components/RunTimeline.tsx` (read-only,
no change needed there — see Bug 2 below), and `apps/web/src/pages/CoordinatorRunPage.tsx`.
Independent of Tank's backend timestamp fix — no coordination needed, consumption code already
reads `payload.timestamp_utc/timestampUtc/timestamp` correctly.

## Bug 1 — Redundant "Coordinator" label repeated before every message
**Root cause:** `coordinatorNarrationSteps()` in AgentSessionPanel.tsx (~line 1780, was ~1766)
hardcoded `intent: 'Coordinator'` for every synthesized coordinator-activity timeline step, so
RunTimeline's `headerText={{ children: <Body>{step.intent}</Body> }}` rendered a long stack of
identical "Coordinator" headers with no distinguishing information.
**Fix:** Added `coordinatorEventIntent(evt)` — a switch over the same event types already handled
by `coordinatorActivityLine()` — that returns a short, specific label per event (e.g. "Coordinator
started", "Dispatched subtask", "Subtask completed", "Assembly started", "Review requested", falling
back to "Coordinator" only for truly unknown event types). Added `intent?: string` to
`ConversationRow`, set it when pushing each activity row in `buildCoordinatorTurns()`, and
`coordinatorNarrationSteps()` now uses `row.intent ?? 'Coordinator'` as the step header instead of
the literal string.
**Plain-language before/after:** Before, every single coordinator activity line in the timeline
showed the exact same "Coordinator ⌄" header stacked with almost no visual distinction. After, each
step's header describes what actually happened — "Coordinator started", "Dispatched subtask",
"Subtask completed", etc. — so scanning the timeline tells you the story at a glance instead of a
wall of repeated labels.

## Bug 2 — Raw JSON dumped as message text instead of formatted Markdown
**Root cause:** `AgentSessionPanel.tsx`'s `buildTurns()` already had `parseOutcomeSpecMessage` /
`formatOutcomeSpecMessage` to turn the outcome-drafting agent's raw JSON
(`{"desired_outcome":...,"scope":...}`) into a friendly "### Outcome plan" Markdown block — but this
transform ran ONLY inside `buildTurns()`, which is used for the coordinator's own turn history. The
*subtask/child* "Working" step content is built by a completely separate function,
`buildRunTimeline()` in `timeline/runTimelineSteps.ts`, which had no equivalent transform — so a
child/subtask agent drafting the same outcome spec showed the raw, illegible JSON object verbatim
(exactly matching Ahmed's screenshot: a JSON blob under a "Working" step). Messages ARE already
routed through `SafeMarkdown` in RunTimeline.tsx (no bypass there) — the bug was purely that the
text handed to SafeMarkdown was unformatted JSON, not a markdown-bypassing render path.
**Fix:** Extracted `parseOutcomeSpecMessage` / `formatOutcomeSpecMessage` out of
AgentSessionPanel.tsx into the shared `timeline/coordinatorPlanFilter.ts` module (alongside the
existing `isSerializedWorkPlan` helper) and applied the SAME transform unconditionally in
`buildRunTimeline()` (runTimelineSteps.ts), right next to the existing serialized-work-plan strip.
Unlike the work-plan strip (coordinator-only via `stripSerializedWorkPlan`), the outcome-spec
reformat applies on every scope, since the raw JSON is illegible everywhere it can appear.
AgentSessionPanel.tsx now imports the shared helpers instead of duplicating them.
**Plain-language before/after:** Before, a subtask's "Working" step could show a raw, unreadable
JSON dump like `{"desired_outcome":"...","scope":"...","assumptions":"..."}`. After, that same
content renders as a clean "Outcome plan" section with **Desired outcome:** and **Scope:** labels
and readable paragraph text — identical to how the confirmed outcome plan already looked.

## Bug 3 — Topology graph thumbnail only in the left-rail minimap
**Root cause:** The clickable topology minimap (ReactFlow + MiniMap wired to
`setTopologyPanelOpen(true)`) only existed inline in the left rail's JSX in CoordinatorRunPage.tsx
(~line 4275-4281 originally). There was no equivalent in the Work Plan step's detail content.
**Fix:** Extracted the minimap's ReactFlow/MiniMap markup (context providers, node/edge data,
styling) into a single `renderTopologyThumbnail(variant: 'rail' | 'workplan')` closure defined once
in CoordinatorRunPage, right after `hasGraph` is computed — no duplicated graph-rendering logic.
The left rail now calls `renderTopologyThumbnail('rail')` (unchanged visual behavior). Added a new
`workPlanTopologyThumbnail?: ReactNode` prop on `AgentSessionPanel`; CoordinatorRunPage passes
`renderTopologyThumbnail('workplan')` (slightly narrower via a new `workPlanTopologyThumbnail` style,
max-width 260px) into it. AgentSessionPanel renders that node directly beneath the Work Plan step's
`RunTimeline` content, gated on `selectedItem.nodeId === 'work-plan'` so it never appears on other
scopes. Both thumbnails open the same topology dialog via the same `setTopologyPanelOpen(true)`
handler — single source of truth, no dialog duplication.
**Plain-language before/after:** Before, the small topology graph preview only lived in the sidebar;
there was no way to see/open it from inside the Work Plan detail view itself. After, the same
clickable thumbnail also appears right under the numbered subtask list in the Work Plan panel, and
clicking either one opens the identical full topology dialog.

## Validation
- `npm --prefix apps/web run build` — passes (tsc -b && vite build), no new warnings/errors.
- `npm --prefix apps/web test -- --run` — full suite: **81 test files / 750 tests pass** (0
  failures, 0 regressions). Ran the 5 targeted files first
  (AgentSessionPanel/CoordinatorRunPage/CoordinatorRunPage.coordUx/RunTimeline/runTimelineSteps),
  then the full suite as a final check.
- Added 4 new tests: 3 in `AgentSessionPanel.test.tsx` (varied narration headers for Bug 1,
  outcome-spec Markdown formatting on a subtask scope for Bug 2, work-plan-only thumbnail prop
  scoping for Bug 3) and 1 in `runTimelineSteps.test.ts` (outcome-spec reformat at the
  `buildRunTimeline` layer, covering child/subtask scopes directly).
- Verified the existing "does not leak outcome-plan content into the RAI reviewer activity" test
  still passes unmodified — confirms no regression on the RAI-gate scope where that content is
  filtered out entirely (unrelated to this fix).

## Files changed
- `apps/web/src/components/AgentSessionPanel.tsx`
- `apps/web/src/pages/CoordinatorRunPage.tsx`
- `apps/web/src/timeline/coordinatorPlanFilter.ts`
- `apps/web/src/timeline/runTimelineSteps.ts`
- `apps/web/src/__tests__/AgentSessionPanel.test.tsx`
- `apps/web/src/__tests__/runTimelineSteps.test.ts`

## Next step
Not pushed. Coordinator (Ahmed) will merge this branch tip
(`4e78744176da04ddbb8a80ab33ece91204ef9cb5`) into local main after Tank and Morpheus also report.

## 2026-07-15T13-55-00Z — Deduplicated inbox merge after size-gate reset

**By:** Scribe  
**What:** Merged the current `decisions/inbox` backlog into the active decision log as a deduplicated summary after the 2026-07-14 active-file reset.  
**Why:** Preserve durable decisions without restoring the pre-reset duplication and raw-note sprawl.

### Release and deploy operations
- `v0.9.54` staging repair: rebuilt `agentweaver-api` and updated the gateway `HTTPRoute` so `/openapi` is live in staging; WSL DNS issues required Git Bash for deploy execution.
- `v0.9.55` shipped from `main` with #329, #331, #332, #333, and #334; the post-rollout `SandboxTemplate` false negative was identified as verifier drift rather than release health.
- Provenance hardening now has two layers: `20-build-push-images.sh` verifies `prov-<sha>` tags by digest and propagates background-job failures deterministically, and `30-deploy.sh` now invokes `25-verify-image-provenance.sh` so mismatched running images fail the deploy.

### Harness and persona infrastructure
- API harness moved fully to dynamic persona driving: fixed scenario scripts were removed, Harness dispatches a fresh `PersonaActor` per run, and persona turns are driven from the live OpenAPI surface rather than curated subcommands.
- That dynamic path was tightened further: YAML OpenAPI is preferred, `drive.mjs` was then removed entirely, PersonaActor now curls the live API/spec directly, and the old approval helper library was deleted once the prompt-level safety model became the chosen path.
- Harness operator observability was upgraded: live transcript tailing became automatic, then human-readable (`TURN | METHOD path -> status | THOUGHT`), then actively relayed by running PersonaActor in background mode, and finally timing summaries were derived from per-turn transcript timestamps.
- MCP harness now mirrors the API harness architecture: `scripts/mcp-harness/run-persona.mjs` prepares/finalizes runs, the Harness agent dispatches the real persona-driving agent, smoke checks stay as deterministic connectivity/capability checks, and the parity directive is recorded as a standing design rule.
- Oracle was added as a full-lifecycle PM persona, then generalized so only durable PM behavior stays in the core brief while concrete journey shape comes from invocation-time goals and the live spec; Oracle adapters no longer prescribe ordered phases or tool-specific mechanisms.

### Coordinator steering and recovery
- Outcome-spec confirmation via normal chat/steering is now a first-class path: `steer kind=send` reuses the same confirm/revise machinery as the UI gate instead of maintaining a parallel flow.
- #272 had two real failures and both are now part of team memory: orphaned deferred spec decisions on pod-per-run are drained by heartbeat-driven recovery, and confirm/revise intent classification must use an LLM-backed classifier with fail-closed `Revise` fallback instead of regex matching. Earlier regex-only notes are superseded by this LLM rule.
- #332: coordinator `POST /retry` first attempts in-place resume from the preserved failure point and only falls back to minting a fresh run when no recoverable work plan exists or recovery is exhausted.
- #331: if a coordinator child run ends its stream after a successful agent turn but before the watcher observes the terminal output edge, the watch loop may recover it as `assemble-ready` instead of discarding verified work.

### API, MCP, runtime, and tooling fixes
- OpenAPI generation is a shipped API feature at `GET /openapi/v1.json`, exposed in staging and exempted from GitHub-org auth so harnesses can discover the live contract.
- #337/#338/#339: MCP run tools now declare typed structured output schemas, mark optional parameters via actual defaults, and surface real JSON `{ error, hint }` messages by deriving `McpApiException` from the SDK's `McpException`.
- #334: `start_preview` belongs to preview lifecycle tooling, not the project/agent identity-gated coordination toolset, so it was consolidated into `PreviewRunnerToolProvider` and registered alongside the rest of the preview tools.
- #333: `POST /api/projects` now treats `working_directory` as provider-driven; it remains required only for providers that truly need a client-supplied path.
- #321: notifications now emit a real `tool_approval` type by reusing the shared pending-approval projection logic already used by the board.
- #224: shell commands may use an explicit run-scoped scratch directory outside the worktree, but file tools remain rooted at the worktree.

### Agent runtime identity, memory, and skills
- #335 final root cause: warm-pool `/configure` never carried `projectId` and `agentName`, so native loopback memory/decision tools were silently omitted from warm-pod agent sessions; the fix is to plumb identity end-to-end through `/configure`.
- #336 is distinct from #335. The durable conclusion is that pod-per-run A2A bridging dropped the per-turn `AgentSetupParams` context (skills, memory prompt text, identity) and only preserved `IsRevision`; the fix is to apply full per-turn context on the pod while preserving the pod-base manifest context. Earlier #336 notes about missing wiring or missing observability are intermediate findings and are superseded by the per-turn A2A bridge root cause.

### UI and UX decisions
- #316: session history belongs as a `Team memory` tab, while per-agent memory should live behind a team-drawer `View memory` affordance; reuse the existing paginated envelope and pager UI.
- Timeline activity should collapse adjacent lightweight continuation-style intents into one top-level step instead of promoting every micro-update to its own major activity row.
- Coordinator artifact panes now refetch from the existing run SSE stream, keeping files/diffs current without waiting for status polling.
- #319: the notification bell shows a typed badge (`Human Review`, `Tool Approval`, or fallback) and the frontend deliberately tolerates future notification types.


### Addendum from remaining inbox items
- #97: coordinator `assembly_blocked` failures must durably persist enriched `ineligible_subtask` detail at block time and surface readable blocking-subtask detail in the UI instead of opaque ids.
- Harness transcripts keep raw response evidence but cap `response.body` to about 1.5KB with explicit truncation markers; persona reasoning about the previous response belongs in `thought` so transcripts stay auditable without exploding in size.
- The OpenAPI contract was further enriched with XML-backed lifecycle documentation, YAML serving, stable names/tags, and bearer-security metadata for persona-critical routes.
- Oracle adapter cleanup continued past the first refactor: the API adapter is now fully live-spec-driven and intentionally stripped of residual journey-shape hints, leaving only discovery framing and epistemic guardrails.

---

# Decision: Graceful shutdown fix for assistant-chat mid-turn termination — 2026-07-15

**Status**: MERGED to main (commit c68b9055), released as v0.9.67, verified live in staging.
**By**: Link (Platform Engineer)
**Branch**: fix/assistant-graceful-shutdown (worktree: C:\Users\asabbour\Git\aw-wt-assistant-shutdown)
**Refs**: origin/main `c68b9055` (merge commit)

## Root cause

`k8s/api-deployment.yaml` had no `terminationGracePeriodSeconds` (K8s default 30s) and no
`preStop` hook. ASP.NET Core's Generic Host `HostOptions.ShutdownTimeout` was also unconfigured
(default 30s). On every rolling deploy (this repo ships multiple releases/day, `maxSurge: 1,
maxUnavailable: 0`), SIGTERM to the old pod triggered graceful shutdown, and after ~30s the host
cancels `HttpContext.RequestAborted` for in-flight requests — including long operator assistant
turns (verified live: a legitimate 18-tool-call turn took 101s to succeed when not torn down).
That cancellation surfaces as `System.OperationCanceledException` from `Channel.ReadAllAsync` in
`GitHubCopilotAgent.RunCoreStreamingAsync`.

## Fix and chosen values

- `k8s/api-deployment.yaml`: `terminationGracePeriodSeconds: 120` + `preStop: sleep 5` (lets
  Service/Endpoints deregister the pod before it stops accepting connections; in-flight requests
  keep draining).
- `apps/Agentweaver.Api/Program.cs`: `builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout =
  TimeSpan.FromSeconds(100))`. 100s leaves ~20s margin under the 120s k8s grace period for actual
  process teardown after the app-level graceful drain window elapses.
- Values chosen to comfortably exceed observed 60-100s legitimate assistant turn durations while
  keeping deploy rollout time reasonable (120s per pod max drain).

## Scope decision: AgentHost excluded

Checked `k8s/sandbox-template-agenthost.yaml` (no separate `agenthost-deployment.yaml` exists).
AgentHost runs as a `SandboxTemplate` with per-run pods (`restartPolicy: Never`), not a
rolling-update `Deployment` — pods are created/torn down per agent run, not rolled during
releases. The termination-during-deploy gap this fix addresses does not apply to that
architecture, so no change was made there.

## Validation performed

- `dotnet build apps/Agentweaver.Api/Agentweaver.Api.csproj -c Release
  -p:CopilotSkipCliDownload=true` → Build succeeded, 0 warnings/errors. (Note:
  `CopilotSkipCliDownload=true` was required only because this sandbox's network blocks the
  unrelated npm download of the Copilot CLI binary used elsewhere in the SDK's build target — not
  related to this change.)
- YAML parsed successfully with `python -c "import yaml; yaml.safe_load_all(...)"`.
- Not applied to any live cluster; coordinator owns build/push/deploy/verify.

## Deployment outcome (post-merge)

- Merged to `origin/main`, committed as `c68b9055`.
- Released as v0.9.67, deployed live to staging.
- Verified: `terminationGracePeriodSeconds=120` and `preStop` hook confirmed live on both new API pods.
- Live smoke test of a real assistant turn succeeded post-deploy — no interruption observed.

---

**Merged from inbox file:** `copilot-directive-2026-07-16T11-21-21-871-07-00.md`

### 2026-07-16T11:21:21.871-07:00: User directive
**By:** Ahmed Sabbour (via Copilot)
**What:** Release carefully because other agents are working; never wipe or reset their active work.
**Why:** User request — captured for team memory

---

**Merged from inbox file:** `copilot-directive-2026-07-17T17-16-11-02-00.md`

### 2026-07-17T17:16:11+02:00: User directive
**By:** sabbour (via Copilot)
**What:** "I never want runs to simply timeout if a human isn't around to approve them. They can sleep, sure, but they can and must be resumable." — applies to ALL run types (Assistant/Operator conversations AND Coordinator runs), not just the Assistant idle-timeout case already fixed this session. No run may transition to a permanently-dead terminal state (Failed/Completed) solely because a human took too long to respond to an approval/confirmation gate. Wall-clock ceilings must bound genuine stuck/runaway processing only, never elapsed human-response time.
**Why:** User request — captured for team memory. Investigation (this session) found two live violations: (1) `RunWatchLoopService`'s 4h `Runs:WatchLoopTimeout` wraps the entire per-run watch task including time parked at ANY RequestPort/HITL suspension (outcome-spec confirmation, review gates) — a human taking >4h causes `watch_loop_timeout` -> permanent Failed. (2) `AssistantRunService`'s 30-min `IdleTimeout` unconditionally seals+closes a run even while a tool-approval is armed and pending, clearing the approval gate. The existing `AssemblyReviewGate` (indefinite wait, no timeout, durable reconciler recovery) is the correct reference pattern already proven in this codebase and should be generalized to the other two gates.

---

**Merged from inbox file:** `copilot-resumability-design-2026-07-17T17-20-41-02-00.md`

### 2026-07-17T17:20:41+02:00: Design decision — literal same-run resumability for Assistant/Operator conversations
**By:** Squad (Coordinator), per sabbour's directive
**What:** Confirmed app-level solution (no Kubernetes pod snapshotting needed) — Assistant/Operator runs have no per-run sandbox pod, so all state needed to "un-sleep" a conversation already lives in the durable event log + DB. Design: add a new non-terminal `RunStatus.Idle` (appended LAST in the enum — status is persisted as an int ordinal, no HasConversion found, so insertion order matters). Idle-timeout now CAS-transitions InProgress -> Idle instead of -> Completed, and does NOT seal the event stream (no terminal `run.completed`; a new non-terminal `run.idle` marker is appended instead). `RehydrateRunAsync`'s existing rebuild-from-events machinery (already built for the cross-replica/cache-miss case) is reused unchanged to wake an Idle run back to InProgress via a new CAS "wake" transition, continuing the SAME run id/conversation. Reserves true stream-sealing (409 `operator_run_closed`) only for genuinely terminal end-of-conversation cases. The earlier-shipped "auto-seed a new run from resume_from_run_id" feature becomes a fallback for genuinely non-resumable runs, not the primary idle-timeout recovery path.
**Why:** User directive: "I never want runs to simply timeout if a human isn't around to approve them. They can sleep, sure, but they can and must be resumable... literal conversation, un-sleep, rehydrate." Avoids repeating the prior zombie-run bug because the new design never conflates "dormant" with "genuinely terminal" (the old bug flipped a sealed/Completed run back to InProgress while the stream stayed sealed — inherently inconsistent). Dispatched to Morpheus (already deep in this exact file/logic) as a follow-up layered on top of the in-flight watch-loop-timeout and approval-armed-guard fixes.

---

**Merged from inbox file:** `link-346-config.md`

# Link — #346 Assistant:McpEndpoint Config

**Date:** 2026-07-15  
**Worktree:** `.worktrees/link-346-config`  
**Branch:** `chore/assistant-mcp-config-346`  
**Commit:** `d1cebe45`

---

## Config Keys Added

### `apps/Agentweaver.Api/appsettings.json`

```json
"Assistant": {
  "McpEndpoint": "http://localhost:5100/mcp",
  "MaxConcurrentRunsPerUser": 3,
  "IdleTimeout": "00:30:00",
  "SweepInterval": "00:01:00"
}
```

- `Assistant:McpEndpoint` — required; read directly via `IConfiguration["Assistant:McpEndpoint"]` in `Program.cs:187`. Throws `InvalidOperationException` at DI resolution time if empty.
- `Assistant:MaxConcurrentRunsPerUser`, `Assistant:IdleTimeout`, `Assistant:SweepInterval` — bound via `IOptions<AssistantRunOptions>` from the `"Assistant"` section (`Program.cs:196`). All have code-level defaults (3, 30 min, 1 min) so they are optional — included in config for discoverability and easy operator tuning.

### `k8s/api-deployment.yaml`

```yaml
- name: Assistant__McpEndpoint
  value: http://agentweaver-mcp:8080/mcp
```

Added after the `Agentweaver__ApiBaseUrl` env var in the `agentweaver-api` container spec. ASP.NET Core translates `__` → `:` for section-separator binding, so this maps to `Assistant:McpEndpoint`.

---

## In-Cluster MCP Service URL

**URL used:** `http://agentweaver-mcp:8080/mcp`  
**Confidence:** HIGH — confirmed from two independent sources:

1. **`k8s/mcp-service.yaml`** — Kubernetes Service named `agentweaver-mcp`, namespace `agentweaver`, `ClusterIP`, port 8080 → targetPort 8080. This is the authoritative in-cluster DNS name.
2. **`k8s/mcp-deployment.yaml`** — MCP container binds containerPort 8080. `AGENTWEAVER_API_URL=http://agentweaver-api:8080` uses the same naming pattern, confirming the convention.
3. **`docs/guide/architecture-aks.md:163`** — explicitly documents `Service: agentweaver-mcp ClusterIP :8080`.

---

## Network Policy Gap Fixed

**Finding:** The existing `allow-gateway-to-mcp` NetworkPolicy (`k8s/networkpolicy-mcp.yaml`) only admitted ingress to the MCP pod from gateway pods — NOT from `agentweaver-api` pods. The `allow-app-internal-egress` policy already opened egress from `agentweaver-api` on port 8080, but east-west API→MCP connections would be **silently dropped** at the MCP pod ingress without a matching ingress rule.

**Fix:** Added a new `allow-api-to-mcp` NetworkPolicy document (appended to `k8s/networkpolicy-mcp.yaml`) admitting ingress to `app: agentweaver-mcp` from `app: agentweaver-api` on TCP:8080.

Precedent: the existing `allow-mcp-to-api` policy in `networkpolicy-default-deny.yaml` handles the reverse direction (MCP→API for JWKS fetch); this is its mirror.

---

## Local Dev Default

`appsettings.json` sets `McpEndpoint = http://localhost:5100/mcp`.

**Rationale:** The API occupies port 5000 (per `start-dev.ps1`). Port 5100 is the chosen local dev port for the MCP server; developers must launch it separately with:

```bash
dotnet run --project apps/Agentweaver.Mcp --urls http://localhost:5100
```

The MCP server is not currently started by `start-dev.ps1`. The feature will throw a `InvalidOperationException` on first use (lazy singleton) if the MCP server is not running — not at startup — so it does not break the existing dev loop for developers not using the operator assistant.

**Open question for Tank/Ahmed:** Should `start-dev.ps1` be updated to start the MCP server on port 5100 as a third process? This is outside the config-only scope of this task.

---

## OAuth / Audience Compatibility

**Finding:** COMPATIBLE — no additional config required for the assistant bearer passthrough.

**Detail:**

`AssistantRunService` threads the **caller's GitHub bearer token** (not an Agentweaver-minted JWT) through to the MCP server on each call (per `AssistantRunService.cs:236-251`).

The MCP server's `McpBearerTokenMiddleware` supports two auth paths:
1. Agentweaver-minted OAuth JWT — validated offline against AS JWKS (iss/aud/exp/RS256).
2. Raw GitHub OAuth token — validated by calling `GET https://api.github.com/user`, cached 5 min.

Path 2 is active when `Auth:Mcp:AllowGitHubPassthrough != "false"` (default: `true`).

**In the MCP deployment** (`k8s/mcp-deployment.yaml`):
```yaml
- name: Auth__Mcp__AllowGitHubPassthrough
  value: "true"
```

This is already set. The GitHub bearer from the in-API caller will be accepted by the MCP server via path 2. The audience/issuer check only applies to Agentweaver-minted JWTs (path 1), which are not used here — so there is **no audience mismatch risk** for the assistant's per-call passthrough.

**Watch flag:** If `Auth__Mcp__AllowGitHubPassthrough` is ever flipped to `false` (planned for after all clients migrate to AS-minted tokens), the assistant will break unless `AssistantRunService` is updated to mint an Agentweaver access token first (via `McpTokenService`) rather than passing through the caller's GitHub token. This is a future dependency to track in #346.

---

**Merged from inbox file:** `link-53-webhook.md`

# Decision: GitHub webhook receiver design (#53 follow-up)

**Author:** Link (Backend Dev)
**Branch:** feat/53-webhook-event-source (worktree: fix-53-event-source)
**Date:** 2026-07-16

## Context

Issue #53 shipped a manual trigger mechanism only: `WorkflowEventTriggerService.FireEventAsync` +
`POST /api/projects/{id}/workflow-events`. This follow-up wires a real GitHub webhook so external
GitHub events (push/pull_request/issues) actually fire event triggers.

## Decisions

1. **No `WorkflowTrigger` schema change.** The task suggested adding a `source: github` + repo
   filter field to the trigger schema. This turned out to be unnecessary: `Project.Origin` already
   carries `SourceRepository` ("owner/repo") for GitHub-origin projects. The webhook receiver fans a
   delivery out to every ACTIVE project whose `Origin.SourceRepository` matches the payload's
   `repository.full_name` (case-insensitive), then fires events only into workflows in that project.
   This keeps the existing `WorkflowTrigger.EventName`-only matching intact — zero migration risk.

2. **Event naming convention:** `github.<event>` (e.g. `github.push`, `github.issues`) always fires;
   when the payload carries an `action` field (pull_request/issues-style events), an additional
   `github.<event>.<action>` (e.g. `github.issues.opened`) also fires. This lets workflow authors
   subscribe coarse or granular without any new trigger fields — just pick the `event_name` string
   they want in their existing `trigger.event_name` YAML.

3. **Auth model:** `/api/webhooks/github` is exempted from both `GitHubTokenAuthMiddleware` (bearer
   token) and `GitHubOrgAuthorizationMiddleware` (org membership) — GitHub's delivery carries neither.
   The HMAC-SHA256 `X-Hub-Signature-256` check (constant-time compare) is this endpoint's sole
   authentication. An unconfigured `GitHubWebhook:Secret` fails closed (500), never open.

4. **Dedupe:** reuses the existing `FireEventAsync(dedupeKey)` idempotency mechanism from #53, keyed
   off `X-GitHub-Delivery` + the specific event name fired, so GitHub's at-least-once delivery
   retries never double-fire a run.

5. **Scope discipline:** only push/pull_request/issues are exercised in tests as representative
   examples; the mechanism is generic (any `X-GitHub-Event` value routes the same way), so no code
   changes are needed to support other GitHub event types later.

## Follow-ups intentionally NOT done (out of scope for this pass)

- No webhook management UI (create/rotate secret, delivery history, replay).
- No per-workflow GitHub App/installation-token awareness — this only fires the trigger, it doesn't
  change how a fired run authenticates back to GitHub.
- No signature-secret rotation support (single `GitHubWebhook:Secret` value); rotating requires a
  config change + restart, same pattern as other secrets in this app.

---

**Merged from inbox file:** `Morpheus-binding-roles-to-skills-is-blocked-pending-live-po.md`

### 2026-07-16T14-43-08: binding-roles-to-skills is blocked pending live PostgreSQL concurrency validation
**By:** Morpheus
**What:** binding-roles-to-skills is blocked pending live PostgreSQL concurrency validation
**References:** binding-roles-to-skills, EfSkillStoreConcurrencyTests, Code Review/Trinity rejection
**Why:** The independent revision addresses the reviewer findings: canonical preview state hashing and exact atomic preconditions; EF Serializable bounded retries/conflict mapping; SQLite immediate transaction parity; cancellation-safe rollback; Fluent in-dialog 409 handling; and accessible BlueprintPicker role-to-skill details. Solution/API/SQLite/web/docs checks pass. The new EfSkillStore Postgres concurrency test could not execute because `docker info` failed: Docker Desktop's `npipe:////./pipe/dockerDesktopLinuxEngine` does not exist. The repository guard skipped the test. Start Docker and run `dotnet test tests\\Agentweaver.Tests\\Agentweaver.Tests.csproj --no-build -p:CopilotSkipCliDownload=true --filter "FullyQualifiedName~EfSkillStoreConcurrencyTests"`; only then set this task done.

---

**Merged from inbox file:** `morpheus-operator-dock-design.md`

# Decision: Replace "operator dock" with an MCP-driven operator assistant on a CoordinatorRunPage-style page — 2026-07-15

**Status**: PROPOSED (design/investigation only — no code written)
**By**: Morpheus (Runtime Engineer)
**Requested by**: Ahmed Sabbour (sabbour)
**Trigger (verbatim)**: "get rid of the current 'operator dock' and replace with a brand new page based on the coordinator run page. The chat there will directly integrate with copilot and load up the Agentweaver agent definition and the AgentweaverMCP server."

## What "operator dock" is today (grounded)

- Frontend trigger: `apps/web/src/components/shell/LeftNav.tsx:174-178` ("Open Agentweaver operator dock" Chat button) → `apps/web/src/components/shell/ConsolePanelContext.tsx` → `apps/web/src/console/BrowserConsole.tsx` (+ `consoleCommands.ts` slash-command catalog).
- Backend: `POST /api/console/turn` (+ `/api/console/messages`) `apps/Agentweaver.Api/Endpoints/ConsoleEndpoints.cs:76-77` → `ConsoleTurnService.HandleAsync` (`apps/Agentweaver.Api/Console/ConsoleTurnService.cs`). This service is a **deterministic regex pre-router** (`LooksLikeGateRequest`/`LooksDestructive`/`LooksLikeStart`/`LooksLikeReadOnlyStatus`, ~line 100-140+) — the "why a regex, we have the LLM" surface Ahmed dislikes.
- For read-only status it calls `CopilotConsoleFacadeAgent` (`packages/Agentweaver.AgentRuntime/ConsoleFacadeAgent.cs`): an **in-API MAF GitHub Copilot turn** whose system prompt IS `.github/agents/agentweaver.agent.md` (passed as `AgentDefinition`, appended at `ConsoleFacadeAgent.cs:167-232`), BUT with only **15 hand-wrapped READ-ONLY API tools** (`ConsoleFacadeApiTools.Build`, `ConsoleFacadeAgent.cs:236-360`) and `EnableConfigDiscovery = false` (`ConsoleFacadeAgent.cs:94`). It is **not** connected to the real MCP server.

## Hypothesis verdict: PARTIALLY REFUTED — and the refutation makes the task EASIER

The parent hypothesis (route the assistant through the SAME agent-host/A2A sandbox-pod provider mechanism used for real team-member runs) is the **wrong mechanism**, for grounded reasons:

1. **agentweaver.agent.md is an OPERATOR/DRIVER, not a code-editing WORKER.** It drives the platform via MCP tools; it needs no git worktree/repo sandbox. The sandbox-pod/A2A path exists to give WORKER agents an isolated writable repo: `RemoteAgentProxy` A2A `POST {pod}/message:stream` (`packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:39-45,152-182`); real turn expects WorktreePath/RepositoryPath (`Workflow/AgentTurnExecutor.cs:83-110`); pod git worktree prep (`apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:44-127`). Routing operator chat here pays pod cold-start + worktree prep for zero benefit.
2. **The agent-host runtime does NOT and cannot "load the AgentweaverMCP server" as-is.** Every Copilot SDK session sets `EnableConfigDiscovery=false` and injects hand-built AIFunctions; there is ZERO MCP-server wiring in AgentRuntime/AgentHost (`ConsoleFacadeAgent.cs:94`, `CopilotAIAgent.cs:484`, `GitHubCopilotAgentRunner.cs:334`; grep for mcpServers/McpConfig/modelcontextprotocol found only tool-permission handling, no server config). So "reuse the real run mechanism" would STILL require net-new MCP integration.
3. **No lightweight single-agent non-coordinator run path exists.** Single-run endpoints are 410-deprecated (`RunEndpoints.cs:37-40`, `ProjectEndpoints.cs:238-242`); only run creation is coordinator (`ProjectEndpoints.cs:250-285` → `CoordinatorRunService.StartCoordinatorRunAsync:113-173`). Closest primitive `RunOrchestrator.StartChildRunAsync` requires ParentRunId+SubtaskId (`RunOrchestrator.cs:196-210`).

## Recommended architecture (grounded)

The operator assistant is the SAME shape as today's ConsoleFacadeAgent (in-API Copilot chat loop seeded with agentweaver.agent.md), with three changes:

A. **Give it the real AgentweaverMCP surface (all 91 tools)** instead of the 15 read-only hand-wraps — one source of truth, killing the drift risk flagged in `consoleCommands.ts`. The MCP server is a standalone ASP.NET process exposing `/mcp` streamable-HTTP (stateless) + stdio (`apps/Agentweaver.Mcp/Program.cs:9,60-70,104`), RFC-9728 OAuth metadata (`Program.cs:78-101`, prod audience pin `:13-30`), per-call GitHub bearer passthrough via `mcp.bearer_token` (`AgentweaverApiClient.cs:295-333`, README:31-39). MCP tools are thin API wrappers (e.g. `coordinator_start` → `POST /api/projects/{id}/orchestrations`, `Tools/CoordinatorTools.cs:12-23`). Preferred impl: an MCP client that connects to `/mcp` with the caller bearer, enumerates tools, adapts each to an `AIFunctionDeclaration` for `SessionConfig.Tools`.
B. **Delete the regex pre-router.** Let the LLM + MCP tool descriptions route. Move gating/safety to **per-tool approval** (reuse the existing `OnPermissionRequest` gate + the UI's `approveTool/denyTool` wiring in `apps/web/src/components/AgentSessionPanel.tsx:2630-2639`). Destructive/gated MCP tools route through approval instead of being hidden.
C. **Model the conversation as a lightweight "operator run"** persisted like a Run (`IRunStore`, AgentName="Operator", ParentRunId=null, no work plan/children), emitting RunEvents onto the existing `IRunEventStream` so `GET /api/runs/{id}/stream` + `/events` (`RunEndpoints.cs:310-317,498-545`) and the frontend `useSeededRunStream` hook work unchanged. This is the net-new backend piece (`AssistantRunService`): a multi-turn Copilot+MCP loop that appends user turns and streams events — no work plan, no assembly/merge.

## Frontend reuse (strong)

CoordinatorRunPage ALREADY supports "single agent, no work plan, no children" via `isChildRun`/`noWorkPlan` (`apps/web/src/pages/CoordinatorRunPage.tsx:2333,2336`; child branches hide OutcomePlan/Changes/Files/steering at `:4577,4598,4610,3858`; noWorkPlan empty-state `:2128`). Conversation surface = `AgentSessionPanel` (transcript timeline + composer + tool/shell approvals). New page ≈ an "assistant" variant where noWorkPlan/no-graph is normal (center = transcript) and the composer's hard-coded `apiClient.steerCoordinator` call (`AgentSessionPanel.tsx:2197`) is generalized to an injected send handler. `useSeededRunStream` reused as-is.

## Latency / warm-pool → NOT a blocker

Because the operator agent needs no worktree it does NOT touch the agent-host pod path — pod cold-start is moot. It runs as an in-API Copilot session like today's synchronous ConsoleFacadeAgent (sub-second→few-seconds/turn). The warm-pool IS real (`apps/Agentweaver.AgentHost/AgentHostStartupService.cs:11-17,67-77` standby+`/configure`; `apps/Agentweaver.Api/Sandbox/KubernetesPodAgentEndpointResolver.cs:35-38,71-88` lazy launch) and would only matter if we WRONGLY routed through pods. Real risk to manage: a long-lived in-API MCP-tool chat loop holds a Copilot session + MCP HTTP connection per active conversation → needs session idle-timeout + per-user concurrency bound (runtime already sets `EnableSessionStore=false` for concurrency safety, `CopilotAIAgent.cs`).

## Task breakdown

Backend:
1. (Morpheus/Tank) `AgentweaverMcpToolProvider`: MCP client → `/mcp` streamable-HTTP with caller bearer; enumerate 91 tools; adapt to AIFunctionDeclarations. (Gated on open-question 1.)
2. (Morpheus) `OperatorAssistantAgent` (replaces CopilotConsoleFacadeAgent): MAF Copilot session seeded with agentweaver.agent.md + full MCP tool set + OnPermissionRequest → approval gate; multi-turn.
3. (Tank/Morpheus) `AssistantRunService` + persistence: create/track operator run (no work plan/children), append user turns, run loop, emit RunEvents on IRunEventStream. New endpoints `POST /api/assistant/runs`, `POST /api/assistant/runs/{id}/messages`; reuse `/api/runs/{id}/stream` + `/events`.
4. (Tank) Retire `/api/console/turn` + `ConsoleTurnService` (remove regex pre-router); redirect to assistant run or delete after FE cutover.
5. (Link/Morpheus) Deploy/config: MCP server reachable from API (in-cluster URL + audience), bearer forwarding, RFC-9728 audience pin (`Mcp/Program.cs:13-30`).

Frontend:
6. (Trinity) New `AssistantRunPage` (or `assistant` variant of CoordinatorRunPage): reuse AgentSessionPanel + useSeededRunStream; hide DAG/work-plan/assembly chrome; center = transcript.
7. (Trinity) Generalize AgentSessionPanel composer submit (remove hard steerCoordinator; inject send). Keep tool/shell approval wiring for gated MCP tools.
8. (Trinity) Rewire LeftNav operator-dock trigger + ConsolePanelContext/BrowserConsole: "Chat" opens/creates an assistant run → new page. Retire BrowserConsole/consoleCommands (slash-commands decision below).
9. (Trinity) Remove `AgentweaverConsoleResponse`/`ToolCall` types + old facade client method after cutover.

Cross-cutting:
10. (Smith) Tests: MCP tool-adapter contract; approval-gate-blocks-destructive (replacing regex-gate tests); assistant-run SSE event-shape; FE AssistantRunPage render + composer submit.
11. (Seraph) Security: MCP bearer passthrough + prompt-injection (untrusted tool output), gated-tool approval coverage, keep installation-token rejection (already in `ConsoleFacadeAgent.cs:70-78`).

## Open questions for Ahmed

1. **MCP integration mechanism**: does the GitHub Copilot SDK (`Microsoft.Agents.AI.GitHub.Copilot` / `SessionConfig`) support declaring MCP servers natively (mcpServers honored when EnableConfigDiscovery on), or must we adapt MCP tools → AIFunctions ourselves? Biggest impl-shape unknown; needs a short SDK spike. (Recommend adapter approach.)
2. **Full surface vs safe subset**: expose ALL 91 MCP tools behind per-tool approval prompts (recommended), or a curated subset? Today's dock is deliberately read-only.
3. **Run substrate**: model chat as a first-class lightweight "operator run" in the run store + existing run SSE (recommended, maximizes CoordinatorRunPage reuse), or keep stateless per-turn like today (loses transcript-as-run + page reuse)?
4. **Slash commands**: keep `/projects /use /orchestrate ...` as shortcuts, or fully retire for pure NL + MCP?
5. **Scope of "get rid of"**: delete BrowserConsole + ConsoleTurnService + facade entirely, or leave dormant behind a flag during rollout?

---

**Merged from inbox file:** `Morpheus-use-one-immutable-catalog-conformance-snapshot-for.md`

### 2026-07-16T10-35-36: Use one immutable catalog conformance snapshot for runtime catalog assets
**By:** Morpheus
**What:** Use one immutable catalog conformance snapshot for runtime catalog assets
**References:** auditing-catalog-exportability
**Why:** Built-in blueprints and workflows are now evaluated once through production loaders and the workflow graph binder. Blueprint listing keeps unavailable entries visible with sanitized exportability codes, while registry, suggestions, project application, and generator prompts consume only exportable snapshot entries. This avoids divergent direct resource-loading decisions across runtime paths.

---

**Merged from inbox file:** `neo-cleanup-console.md`

# Neo — cleanup-console-backend scope adjustment

**Branch:** chore/cleanup-console-backend
**Commit:** e30cb0d6

## Context
Task: retire the dead legacy browser-Console dock backend now that
BrowserConsole.tsx (its only frontend caller) was removed in
fix/346-remove-operator-dock.

## Finding that adjusted scope
`packages/Agentweaver.AgentRuntime/ConsoleFacadeAgent.cs` was not 100% dead.
It defined the `ConsoleFacadeHistoryMessage` record, which is genuinely
reused by the **live** #346 operator assistant subsystem:
- `apps/Agentweaver.Api/Assistant/AssistantRunService.cs` (`_history` field)
- `packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs`
  (`OperatorAssistantRequest.History`)

Both are wired into DI and mapped via `MapAssistantEndpoints()` — this is
the actively-developed replacement chat feature, not the dead code.

## Decision
Rather than stopping the whole cleanup, I surgically preserved the shared
type and removed everything else:
- Moved `ConsoleFacadeHistoryMessage` into `OperatorAssistantAgent.cs`
  (with a short doc comment explaining it's shared conversation-history
  shape), and deleted the rest of `ConsoleFacadeAgent.cs`.
- Deleted `ConsoleEndpoints.cs`, `ConsoleTurnService.cs`, all
  `Console*`/`ConsoleTurn*` DTOs in `Dtos.cs`, the `IConsoleFacadeAgent` /
  `IConsoleTurnService` / `ConsoleConversationStore` DI registrations
  (Program.cs + `AgentRuntimeServiceCollectionExtensions.cs`), the
  `MapConsoleEndpoints()` call, and the two dedicated dead-code test
  files (`ConsoleFacadeAgentTests.cs`, `ConsoleEndpointsTests.cs`).
- Updated two dangling `<see cref="CopilotConsoleFacadeAgent">` XML doc
  comments (in `AgentweaverMcpToolProvider.cs` and
  `OperatorAssistantAgent.cs`) to plain-text references since that type
  no longer exists.

## Verification
- Confirmed zero remaining references to `ConsoleEndpoints`,
  `ConsoleTurnService`, `CopilotConsoleFacadeAgent`, `IConsoleFacadeAgent`,
  `ConsoleFacadeApiTools`, or `/api/console/*` anywhere in
  apps/packages/tests (repo-wide grep, excluding git history/docs/harness
  transcripts).
- `dotnet build` (with `-p:CopilotSkipCliDownload=true` to bypass a
  sandbox network restriction unrelated to this change) succeeds with
  0 errors/warnings.
- `dotnet test --filter "Assistant|Console|Operator"`: 21/21 passed.
- `dotnet test --filter "Endpoints"` (144 tests, boots the full API host
  via WebApplicationFactory): 144/144 passed — confirms Program.cs DI
  wiring is intact after removing the Console registrations.

No frontend caller (apps/web) referenced Console or Assistant routes at
all, so this is purely a backend-only cleanup as scoped.

---

**Merged from inbox file:** `seraph-assistant-sandbox-audit.md`

# Seraph Security Audit — Operator Assistant Sandbox / Tool Surface (#346)

**Verdict: CONFIRMED VULNERABILITY (critical) — FIXED.**
**Reviewer:** Seraph (Security Reviewer)
**Date:** 2026-07-15
**Requested by:** Ahmed Sabbour
**Branch:** `fix/assistant-sandbox-restriction`  **Commit:** `97178cab`

---

## TL;DR

The "Agentweaver operator assistant" chat feature ran the GitHub Copilot SDK
**in-process inside the API pod with NO OS-level sandbox** and did **not** disable
the SDK's built-in native tools. It therefore had **live, ungated host shell and
filesystem execution** reachable directly from the chat interface. The `Running bash`
tool call Ahmed saw was the SDK's real built-in `bash` tool — **not** one of the 91
MCP tools. This is arbitrary command execution against the production API pod.

Fixed by constraining the assistant's `SessionConfig` to only the MCP tool
declarations (SDK `AvailableTools` allowlist) plus a deny-by-default
`OnPermissionRequest` backstop. Regression test added.

---

## Evidence (ground truth)

### 1. The assistant runs in-process with no sandbox
- `OperatorAssistantAgent.RunTurnAsync` creates the Copilot client and session
  directly in the API process (`apps/Agentweaver.Api/Assistant/AssistantRunService.cs`
  → `OperatorAssistantAgent`). There is **no** `bwrap` / `SandboxExecutorRouter`
  boundary around it, unlike real agent runs.

### 2. The SDK ships built-in native tools that are present by default
- `GitHub.Copilot.SDK` 1.0.2. `CopilotTool` XML doc: *"Tool identifier (e.g.,
  `bash`, `grep`, `str_replace_editor`)."* String-table extraction from the SDK DLL
  confirms native tools `read`, `write`, `shell`, etc.
- The SDK raises `PermissionRequestShell` / `PermissionRequestRead` /
  `PermissionRequestWrite` / `PermissionRequestUrl` for these built-ins — they exist
  independent of `SessionConfig.Tools`.

### 3. Sandboxed runs contain built-ins two ways; the assistant did neither
- `GitHubCopilotAgentRunner.cs:155` — *"The deny-by-default OnPermissionRequest
  handler is the authoritative sandbox gate: it fires for every native tool call
  (read/write/shell/mcp)…"*. Both `GitHubCopilotAgentRunner` and `CopilotAIAgent`
  set `OnPermissionRequest = BuildPermissionHandler(...)` **and** run inside the
  linux-bwrap sandbox.
- `OperatorAssistantAgent.BuildSessionConfig` (pre-fix, ~line 315) set only
  `Tools`, `EnableConfigDiscovery=false`, `Streaming`, `SessionId`,
  `EnableSessionStore=false`, `InfiniteSessions`, `Model`, `SystemMessage`.
  It set **no** `OnPermissionRequest`, **no** `AvailableTools`, **no**
  `WorkingDirectory`. With no permission handler and no sandbox, the native
  built-ins (bash/view/write/str_replace_editor/grep/web_fetch) were reachable and
  auto-invoked → arbitrary shell/FS access from chat. **Confirmed.**

### 4. `Running bash` was the built-in, not an MCP tool
- The 91 MCP tools are namespaced platform operations (e.g. `coordinator_start`,
  `run_submit`, `project_delete`); none is named `bash`. The transcript entry
  matches the SDK's built-in `bash` display name.

---

## Fix (commit `97178cab`)

`packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs`:
1. **`AvailableTools = tools.Select(t => t.Name).ToList()`** — SDK allowlist
   (*"only these tools will be available when specified"*), so **every** SDK
   built-in is removed from the model's tool surface; only the MCP tools remain.
2. **`OnPermissionRequest = RejectNativeToolPermissionHandler`** — defense-in-depth
   backstop that fail-closed **rejects** any native `Shell`/`Read`/`Write`/`Url`
   permission request and approves MCP/custom tool requests (whose consequential
   subset is already human-gated by `ApprovalGatingAIFunction` and governed by the
   MCP server).
3. **System prompt** now states the assistant has **no** direct file/shell/code
   execution and must route such work through an orchestrator run
   (`coordinator_start` / `run_submit` / `run_task`).

`tests/Agentweaver.Tests/Assistant/OperatorMcpAdapterSpikeTests.cs`:
- New regression test `OperatorSessionConfig_RestrictsToolSurfaceToMcpToolsOnly_NoSdkBuiltins`
  asserts `AvailableTools` == exactly the MCP tool names (and contains no
  `bash`/`shell`/`view`/`write`/`str_replace_editor`/`grep`/`web_fetch`), and that
  the permission backstop returns `PermissionDecisionReject` for native
  shell/read/write requests.

Scope: minimal, additive, no unrelated changes.

---

## How Ahmed can verify

```powershell
git checkout fix/assistant-sandbox-restriction
dotnet test tests\Agentweaver.Tests\Agentweaver.Tests.csproj `
  -p:CopilotSkipCliDownload=true `
  --filter "FullyQualifiedName~OperatorMcpAdapterSpikeTests"
# => Passed! Failed: 0, Passed: 6
```
(The `-p:CopilotSkipCliDownload=true` flag only avoids a network CLI-binary download
that is blocked in this environment; it is unrelated to the fix.)

Then re-run the assistant and confirm no `bash`/file built-in ever appears in the
tool-call transcript; execution requests now surface as an instruction to start a run.

---

## Notes / follow-ups (not blocking this fix)
- `CopilotConsoleFacadeAgent` (the older 15-tool read-only facade) similarly sets no
  `OnPermissionRequest` / `AvailableTools`. Its tool set is read-only wrappers, but
  it also runs in-API and would inherit the same SDK built-ins. Recommend applying
  the same `AvailableTools` allowlist there in a follow-up (out of scope for this
  urgent fix — flagged for Tank/Morpheus).

**Verdict for merge-to-main + prod deploy of the operator assistant: was BLOCK
pre-fix; now PASS with this fix applied and the regression test green.**

---

**Merged from inbox file:** `smith-345-smoke-confirm.md`

# Decision: MCP Smoke Confirm-Gate Fix (#345)

- date: 2026-07-15
- agent: smith
- branch: fix/mcp-smoke-confirm-gate-345
- worktree: .worktrees/fix-345-smoke-confirm
- issue: #345
- status: complete

## What was broken

`scripts/mcp-harness/smoke/mcp-cli-smoke.mjs` polls for terminal run statuses
(`completed`, `failed`, `cancelled`, `archived`) but ignores `coordinator_status`.
When a coordinator run reaches the outcome-spec confirmation gate, the top-level
`status` stays non-terminal (e.g. `running`) while `coordinator_status` becomes
`awaiting_confirmation`. Nothing in the smoke path ever called a confirm tool, so
the run stayed at the gate for the full timeout window (240s) and exited non-zero.
This was a false failure: no product regression, just a missing state-machine step
in the smoke script.

## Fix applied

### 1. `lib/smoke-confirm-gate.mjs` (new)

Pure helper `classifySmokeStatus(content, { terminal, alreadyConfirmed })` that
classifies a poll-run response as `break` (terminal), `confirm` (awaiting_confirmation
gate, not yet confirmed), or `continue` (keep polling). Extracted to a library so it
is unit-testable in isolation.

### 2. `smoke/mcp-cli-smoke.mjs` (updated)

Imports `classifySmokeStatus` and wires it into the poll loop:
- On `confirm` → call `coordinator_outcome_spec_confirm` once (guarded by
  `alreadyConfirmed` flag to prevent re-confirming on repeated polls)
- On `break` → exit the poll loop as before
- On `continue` → sleep and poll again

### 3. `required-capabilities.json` (updated)

Added `confirm-outcome-spec` capability entry for `coordinator_outcome_spec_confirm`
so the contract check now exercises this tool. Any future removal or rename will
be caught as a surface regression.

### 4. `test/smoke-confirm-gate.test.mjs` (new, 14 tests)

Unit tests covering terminal detection, case-insensitivity, the awaiting_confirmation
gate, the no-re-confirm guard, and two end-to-end state-machine sequence simulations.

### 5. `test/run-persona.test.mjs` (updated)

Added `coordinator_outcome_spec_confirm` to the fakeClient used by `runCapabilityCheck`
so the extended contract still passes.

### 6. `harness-shared/learnings.md` (appended)

Durable record of the finding via `record-learning.mjs`.

## Point-3 verification: Does MCP confirm/steer GENUINELY work?

**Answer: YES. The confirm mechanism works over MCP. This is not a regression.**

Evidence (static code analysis):

1. `apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs`:
   `coordinator_outcome_spec_confirm` calls
   `POST /api/runs/{run_id}/outcome-spec/confirm`

2. `apps/Agentweaver.Api/Endpoints/CoordinatorEndpoints.cs`:
   That endpoint calls `coordinator.ConfirmOutcomeSpecAsync(id, caller.User, ct)`

3. `apps/Agentweaver.Api/Coordinator/CoordinatorRunService.cs`:
   `ConfirmOutcomeSpecAsync` calls `SubmitDecisionAsync` — the same
   `PendingRequestStore` + `RunWorkflowRegistry.SendResponseAsync` resume seam
   used by the existing human-review gate, which was confirmed working in #272.

4. The same `ConfirmOutcomeSpecAsync` is also used internally by
   `ScheduleUnattendedConfirm` for automated (backlog-task) confirms — further
   evidence the pathway is exercised in production.

There is no MCP-specific code that could block the confirm. MCP is a thin RPC
wrapper around the identical API endpoint. The smoke test simply never called it.

## Test result

```
38 pass, 0 fail
```

All 38 tests pass including 14 new smoke-confirm-gate tests.

## What to do next

Merge `fix/mcp-smoke-confirm-gate-345` into `main`. No live staging run was
performed (no `AGENTWEAVER_TOKEN` available in this session); the fix can be
validated end-to-end by running:

```powershell
npm --prefix scripts/mcp-harness run smoke -- `
  --target https://agentweaver.6a568feb1abb750001bf4a24.westus2.staging.aksapp.io/mcp `
  --token $env:AGENTWEAVER_TOKEN `
  --project-id <disposable-project-id>
```

Expected: completes with `DRIVE+CAPTURE OK` instead of timing out at 240s.

---

**Merged from inbox file:** `Smith-binding-roles-to-skills-blocked-on-ui-suite-stabil.md`

### 2026-07-16T15-57-49: binding-roles-to-skills blocked on UI suite stability
**By:** Smith
**What:** binding-roles-to-skills blocked on UI suite stability
**References:** binding-roles-to-skills, apps/web/src/__tests__/SkillsPage.test.tsx
**Why:** The revision is blocked: required backend digest and narrow concurrency mapping changes are implemented and targeted tests pass, but the full SkillsPage test file still has intermittent 409/re-preview Dialog lifecycle failures and did not achieve the mandated 12 clean consecutive runs. Do not mark binding-roles-to-skills done until that evidence is obtained.

---

**Merged from inbox file:** `smith-harness-seam-fix.md`

# Smith: fix/harness-seam-adapter-version

## Bug
`node scripts/api-harness/run-persona.mjs --scenario generated-artifacts-seam` crashed
with `invalid normalized evidence: metadata.adapterVersion/personaCoreVersion must be a
non-empty string` instead of producing a verdict.

## Root cause (confirmed)
`generated-artifacts-seam` is a `kind: 'generation-seam'` STRUCTURAL scenario — a
deterministic generator regression check with no persona behind it at all.
`loadPersona()` correctly returns `null` for it (there is no persona brief). But
`run-persona.mjs` fed `sharedPersona?.adapter?.version ?? null` /
`sharedPersona?.version ?? null` into the metadata passed to
`scripts/harness-judge/core.mjs`'s `validateEvidenceShape`, which enforces
`REQUIRED_JOIN_KEY_FIELDS` (from `scripts/harness-judge/verdict-schema.mjs`) as
non-empty strings unconditionally for every surface. `null` failed that check and
threw before a verdict could ever be produced.

## Fix (scoped narrowly)
Only touched `scripts/api-harness/run-persona.mjs` (the file that exclusively drives
`kind: 'generation-seam'` scenarios — persona-behavior scenarios are no longer run
through this file per `scripts/api-harness/SKILL.md`). Added a
`NO_PERSONA_VERSION_SENTINEL = 'unknown'` fallback for `metadata.adapterVersion` /
`metadata.personaCoreVersion` when `sharedPersona` is null.

Chose `'unknown'` (not a novel sentinel like `'structural'`/`'n/a'`) because that's
already the established convention for this exact no-persona case elsewhere in the
codebase: `scripts/mcp-harness/run-persona.mjs:189-190` uses `'unknown'` for the same
two fields, and `scripts/ui-harness/agent-driver-ui/tools.mjs:145` uses `'unknown'`
for `targetRevision` when it's not supplied. Did NOT touch
`scripts/harness-judge/verdict-schema.mjs` or `core.mjs` — shared validation logic is
unchanged and unweakened for real persona scenarios.

## Verification
- `npm --prefix scripts/api-harness test` — 16/16 pass (after installing the
  harness's own missing `yaml` dependency, unrelated to this bug).
- `npm --prefix scripts/harness-judge test` — 10/10 pass, including
  "validateVerdict accepts a fully conforming cross-surface verdict" with real
  (non-sentinel) version strings — confirms persona validation path untouched.
- Live run against staging (`https://agentweaver.6a568feb1abb750001bf4a24.westus2.staging.aksapp.io`):
  `generated-artifacts-seam` now runs to completion — driver: DRIVE+CAPTURE OK, all
  P0 structural checks pass, verdict JSON written with
  `adapterVersion=personaCoreVersion="unknown"` (schema-valid) and a
  CANNOT_DETERMINE judge fallback (no `AGENTWEAVER_JUDGE_CMD` configured locally —
  expected, unrelated to this bug).
- Live regression check: drove a real Jordan persona scenario through the MCP
  harness end-to-end (7 real tool-call turns against the same staging target).
  Normalized evidence carried genuine content-hash version strings
  (`adapterVersion="jordan.mcp@059afcd64cdd"`, `personaCoreVersion="jordan@5ad0190758f6"`
  — never null, never the `'unknown'` sentinel), and `validateVerdict()` accepted the
  judged verdict (`p0=PASS`, `p1=PARTIAL`) with zero schema errors. Confirms the fix
  did not regress normal persona evidence validation.

## Docs
Appended an entry to `scripts/harness-shared/learnings.md` via
`record-learning.mjs` (title: "generation-seam scenarios must not send null
adapterVersion/personaCoreVersion", category: bug, surface: api, status: fixed).

## Branch
`fix/harness-seam-adapter-version`, committed and pushed.

---

**Merged from inbox file:** `tank-346-backend-service.md`

# Backend service + endpoint contract: MCP-driven operator assistant (#346)

**Status**: IMPLEMENTED (local worktree, committed, not pushed — no PR)
**By**: Tank (Backend Engineer)
**Requested by**: Ahmed Sabbour (sabbour)
**Branch**: `feat/assistant-run-service-346` (worktree `.worktrees/impl-346-assistant-backend`, commit `5a936f34`)
**Builds on**: spike `bbcd7c5e` (`AgentweaverMcpToolProvider` + `OperatorAssistantAgent`) and Morpheus's design (`morpheus-operator-dock-design.md`, item C).
**For**: Trinity (frontend) — this is the contract your new AssistantRunPage consumes.

## What shipped (backend, additive)

- `apps/Agentweaver.Api/Assistant/AssistantRunService.cs` — models an operator chat as a lightweight
  "operator run" persisted in `IRunStore` (`AgentName == "Operator"`, `ParentRunId == null`, no work
  plan/children), runs one turn at a time via `IOperatorAssistantAgent` (the spike's in-API Copilot+MCP
  loop), and emits `RunEvent`s onto the existing `IRunEventStream`.
- `apps/Agentweaver.Api/Endpoints/AssistantEndpoints.cs` — two new endpoints (below).
- `OperatorAssistantAgent.RunTurnAsync` now takes an `IOperatorAssistantTurnSink` so tool-call/result
  and text-delta steps stream out in order; it also returns the ACTUAL invoked tool names (the spike
  previously returned the whole tool catalog — fixed).
- DI in `Program.cs`: `IAgentweaverMcpToolProvider` (bound to `Assistant:McpEndpoint`),
  `IOperatorAssistantAgent`, `AssistantRunOptions` (section `Assistant`), `IAssistantRunService`;
  `app.MapAssistantEndpoints()`.
- **Untouched / still live**: `/api/console/turn`, `ConsoleTurnService`, `ConsoleFacadeAgent` (parallel
  run-alongside per phased rollout, open question #5 default).

## Endpoint contract (what Trinity codes against)

Auth: standard `Authorization: Bearer <token>` (same as every other `/api` route). No token => 401
(enforced by `GitHubTokenAuthMiddleware`, not the endpoint). The caller's bearer is threaded per-call to
the MCP server — never cached/shared.

### 1. Start a run — `POST /api/assistant/runs`
Request body (all fields optional):
```json
{ "message": "list my projects", "project_id": "<guid>", "run_id": "<guid>", "model_id": "claude-sonnet-4.6" }
```
- `message` (optional): if present, the opening turn runs immediately and its reply is returned inline.
- `project_id` (optional): operator context (which project the chat is about).
- `run_id` (optional): a run the operator is asking about (context only; NOT the operator run's own id).
- `model_id` (optional).

Response `201 Created`:
```json
{ "run_id": "<guid>", "status": "in_progress", "message": "<assistant reply|null>", "tools_invoked": ["project_list"] }
```
- `message` / `tools_invoked` are null when no initial `message` was supplied.
- `run_id` is the operator run id — use it for streaming and for posting further turns.

Errors: `429` `{ "error": "operator_run_limit", "limit": N }` when the user is at the per-user concurrent
cap; `401` unauth; `503`/`429`/`401` on provider failure `{ error, message, kind, retryable }`.

### 2. Send the next turn — `POST /api/assistant/runs/{id}/messages`
Request: `{ "message": "start the bug-fix workflow on project X" }` (required).

Response `200 OK`:
```json
{ "run_id": "<guid>", "role": "assistant", "message": "<assistant reply>", "status": "in_progress", "tools_invoked": ["coordinator_start"] }
```
Errors: `400` `message_required`; `404` `run_not_found` (unknown or idle-closed run); `403` `forbidden`
(not the owner); `401`; provider-failure shape as above.

### 3. Stream / seed — REUSE EXISTING, UNCHANGED
- Live SSE: `GET /api/runs/{id}/stream` (same `useSeededRunStream` hook — no change).
- REST seed: `GET /api/runs/{id}/events` -> `[{ sequence, type, payload }]`.

## RunEvent shapes emitted onto the stream (transcript projection)

All `payload`s are JSON objects. Event `type` values are the existing `EventTypes` constants:

- `run.started` — `{ runId, kind: "operator", agentName: "Operator", projectId, contextRunId }` (once, at create).
- `agent.message` — **carries a `role` discriminator** so one event type covers both sides of the chat:
  - user turn: `{ messageId, role: "user", content }`
  - assistant turn: `{ messageId, role: "assistant", content, toolsInvoked: [..] }`
  - NOTE for Trinity: existing coordinator/worker `agent.message` payloads are assistant-only and may
    omit `role`. Treat a missing `role` as `"assistant"`. `content` is always present (back-compat).
- `tool.call` — `{ messageId, name, arguments }` (`arguments` is a JSON string or null), per invoked MCP tool.
- `tool.result` — `{ messageId, name, success: true }`; `tool.error` — `{ messageId, name, success: false }`.
- `run.completed` — `{ runId, reason: "idle_timeout" }` when the idle sweeper closes the conversation.
- `run.error` — `{ error, message, kind }` on a provider failure during a turn.

`agent.message.delta` is NOT persisted for operator runs in v1 (whole assistant message is emitted once
per turn as `agent.message`). If you want token-streaming later, say so — the sink hook already exists.

## Lifecycle / bounds (Trinity-relevant behavior)

- Run stays `in_progress` for the life of the conversation and accepts turns.
- Idle-timeout (default 30 min, `Assistant:IdleTimeout`) auto-closes an idle run -> `run.completed`
  (`reason: idle_timeout`) + stream completes; a later `POST .../messages` then returns `404 run_not_found`.
  Frontend: on 404 for an existing chat, start a fresh run.
- Per-user concurrency bound (default 3, `Assistant:MaxConcurrentRunsPerUser`) -> `429 operator_run_limit`.

## Deviations from the design (grounded)

1. **No new run "kind"/discriminator field.** There is no such field on `Run`; coordinator-vs-child is
   already inferred from `AgentName` + `ParentRunId`. I followed that convention exactly:
   `AgentName == "Operator"` is the operator-run marker (constant `AssistantRunService.OperatorAgentName`).
   No schema/migration change — nothing force-fit.
2. **`RepositoryPath`/`OriginatingBranch` are empty strings** (they are `required` non-null on `Run`, and
   an operator run has no worktree/repo). This is the intended "no workspace" shape.
3. **In-memory concurrency + idle state** (single-instance v1). If/when the API scales out (Postgres/
   multi-replica), this needs a distributed bound — flagged for Link/Morpheus as a fast-follow. The run
   record + events are already durable; only the live conversation registry is in-process.
4. **Tool events reflect ACTUAL invocations** captured from the streaming loop (`FunctionCallContent` /
   `FunctionResultContent`), not the whole 91-tool catalog. Per-tool approval gating
   (`OnPermissionRequest`) is still a separate follow-up before exposing destructive tools ungated
   (spike recommendation #4) — this service does not itself gate; it relies on the agent/MCP layer.
5. **Config keys added**: `Assistant:McpEndpoint` (required in prod — the in-cluster `/mcp` URL; Link to
   set), `Assistant:MaxConcurrentRunsPerUser`, `Assistant:IdleTimeout`, `Assistant:SweepInterval`.

## Tests (all green)

`tests/Agentweaver.Tests/Assistant/AssistantRunEndpointsTests.cs` (real host, fake `IOperatorAssistantAgent`
seam — no live model): run creation persists as an Operator run; full message round-trip yields
`run.started` + user/assistant `agent.message` + `tool.call`/`tool.result` on `GET /api/runs/{id}/events`;
per-user concurrency bound returns 429; both endpoints require auth (401 without token).

- Full solution build: 0 warnings, 0 errors.
- `--filter ~Assistant|~Mcp|~Console`: **87 passed, 0 failed, 10 skipped** (skips are pre-existing
  OAuth/JWT stubs, unrelated).

---

**Merged from inbox file:** `tank-346-mcp-adapter-spike.md`

# Spike finding: MCP tool-adapter for the operator assistant (#346, open question #1)

**Status**: SPIKE COMPLETE — feasibility PROVEN (adapter approach), no PR opened
**By**: Tank (Backend Engineer)
**Requested by**: Ahmed Sabbour (sabbour)
**Branch**: `spike/mcp-tool-adapter-346` (local worktree `.worktrees/spike-346-mcp-adapter`, commit `c1b5888c`)
**Answers**: Morpheus design open question #1 — "native Copilot SDK MCP config support vs hand-written adapter?"

## Verdict

**A thin adapter is the right answer — and it is much smaller than Morpheus feared.**

- The GitHub Copilot MAF SDK (`Microsoft.Agents.AI.GitHub.Copilot` 1.11.1-rc1 + `GitHub.Copilot.SDK` 1.0.2) does **NOT** natively connect to an MCP server. Every in-process session sets `SessionConfig.EnableConfigDiscovery = false` and injects hand-built `AIFunctionDeclaration`s via `SessionConfig.Tools` (confirmed in `ConsoleFacadeAgent.cs:94`, `CopilotAIAgent.cs:484`). There is no `mcpServers`/`McpConfig` surface on `SessionConfig`. So "point the SDK at /mcp" is not an option.
- **BUT** we do NOT need to hand-write `tools/list` + `tools/call` plumbing or hand-wrap each tool as an `AIFunctionDeclaration`. The official **`ModelContextProtocol` C# SDK** already does all of it: `McpClientFactory.CreateAsync(transport)` → `client.ListToolsAsync()` returns `IList<McpClientTool>`, and **`McpClientTool` derives from `Microsoft.Extensions.AI.AIFunction`** (which derives from `AIFunctionDeclaration`). Invoking that AIFunction issues a real `tools/call` over the transport. So the tools drop **directly** into `SessionConfig.Tools` with a single `.Cast<AIFunctionDeclaration>()`.

Net: the "adapter" is ~40 lines of connection/transport wiring, not a per-tool translation layer.

## What was built (all additive; regex router + existing facade untouched)

1. `packages/Agentweaver.AgentRuntime/AgentweaverMcpToolProvider.cs`
   - `IAgentweaverMcpToolProvider.ConnectAsync(callerBearerToken, ct)` → `AgentweaverMcpToolSession`.
   - Uses `SseClientTransport` with `TransportMode = HttpTransportMode.StreamableHttp` (matches the server's stateless streamable-HTTP `/mcp`).
   - **Per-call bearer passthrough**: sets `AdditionalHeaders["Authorization"] = "Bearer {callerToken}"` on the transport. In stateless streamable-HTTP each `tools/call` is its own HTTP POST, so every call carries the caller's token — exactly the identity the MCP server's `McpBearerTokenMiddleware` forwards to the backend (`AgentweaverApiClient.cs:295-333`). No shared/installation identity, no auth bypass.
   - `AgentweaverMcpToolSession.AsToolDeclarations()` → `IReadOnlyList<AIFunctionDeclaration>` for `SessionConfig.Tools`. Session owns the live MCP connection; `IAsyncDisposable`.
2. `packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs`
   - New agent (NOT a modification of `CopilotConsoleFacadeAgent`) that is the same in-API MAF Copilot chat loop but sources its tools from the MCP provider instead of the 15 hand-wraps. Seeds `agentweaver.agent.md` as the system prompt; rejects the installation token (keeps `ConsoleFacadeAgent.cs:70-78` guardrail); no regex pre-router.
   - `BuildSessionConfig(...)` is the internal testable seam proving MCP tools land in `SessionConfig.Tools`.
3. `tests/Agentweaver.Tests/Assistant/OperatorMcpAdapterSpikeTests.cs`
   - Stands up a **real** in-process MCP server (streamable-HTTP, stateless, same shape as `apps/Agentweaver.Mcp`) on a loopback port with two tools (`spike_echo`, `spike_whoami`) and drives it through the production `AgentweaverMcpToolProvider` over the wire.

## Package added

- `ModelContextProtocol` **0.3.0-preview.2** → `packages/Agentweaver.AgentRuntime/Agentweaver.AgentRuntime.csproj`.
- **Deliberately version-matched to the server's `ModelContextProtocol.AspNetCore` 0.3.0-preview.2** (not the latest `2.0.0-preview.3`). Both projects load into the `Agentweaver.Tests` assembly, so matching versions avoids a two-major-version `ModelContextProtocol.Core` conflict. The client references `Microsoft.Extensions.AI.Abstractions >= 9.6.0`; the solution unifies it up to the existing 10.6.0 with no runtime issue (verified by passing tool round-trips).

## What worked / what to watch

- WORKED: real `initialize` + `tools/list` + `tools/call` round-trip end to end; `McpClientTool` invoked via `AIFunction.InvokeAsync`; per-call bearer verified (the `spike_whoami` tool echoes the exact `Authorization` bearer the server received → equals the caller token supplied to `ConnectAsync`).
- API gotcha: the concrete `McpClient` type is `internal`; program against the public **`IMcpClient`** (returned by `McpClientFactory.CreateAsync`, exposes `ListToolsAsync`/`DisposeAsync`).
- NOT exercised in the test (needs a live Copilot-entitled token, same limitation as the existing facade which is also not unit-run): the actual model-driven tool selection inside a real Copilot session. `BuildSessionConfig` is unit-covered; the live loop mirrors the proven `ConsoleFacadeAgent` streaming loop verbatim.
- Not in scope for the spike (called out, not built): per-tool approval gate (`OnPermissionRequest`) for destructive MCP tools; operator-run persistence/SSE; retiring the regex router.

## Test results

- `dotnet build agentweaver.sln`: 0 warnings, 0 errors.
- Spike tests (`FullyQualifiedName~OperatorMcpAdapterSpike`): **5/5 passed**.
- Full targeted filter (`~Mcp|~Assistant|~Console`): **82 passed, 0 failed, 10 skipped** (all skips pre-existing OAuth/JWT stubs, unrelated).

## Recommendation for the next implementation wave (real, non-spike)

1. **Ship the adapter as-is in shape.** Keep `AgentweaverMcpToolProvider` returning a disposable session that owns one `IMcpClient` per caller conversation (bearer is fixed per signed-in user; per-call passthrough is satisfied at the HTTP layer). Pin `ModelContextProtocol` to the SAME version as `apps/Agentweaver.Mcp`'s `ModelContextProtocol.AspNetCore` and bump both together.
2. **Register via DI/config, not hard-coded.** Add `AgentweaverMcpConnectionOptions` bound from config (`Assistant:McpEndpoint` = in-cluster MCP URL; audience per RFC-9728 pin `Mcp/Program.cs:13-30`). Register `IAgentweaverMcpToolProvider` singleton (options + `ILoggerFactory`); leave the optional `Func<HttpClient>` null in prod, inject only in tests.
3. **Lifecycle bounds (the real risk Morpheus flagged).** A long-lived operator chat holds an `IMcpClient` HTTP connection + Copilot session per active conversation. Add: session idle-timeout, per-user concurrency cap, and deterministic `await session.DisposeAsync()` on conversation end (the spike already disposes per turn via `await using`). Reconnect+re-`tools/list` on token refresh so a rotated bearer is picked up.
4. **Wire the approval gate BEFORE exposing all ~91 tools.** Do NOT let destructive MCP tools run ungated. Reuse `CopilotAIAgent`'s `SessionConfig.OnPermissionRequest` handler (`CopilotAIAgent.cs:~470`) against the MCP tool set so `coordinator_start`, `*_confirm`, merge/delete/stop route through `OnPermissionRequest` → the UI `approveTool/denyTool` wiring. This replaces the regex `LooksDestructive` gate. Seraph should review MCP tool-output prompt-injection surface (untrusted tool results) here.
5. **Then** build `OperatorAssistantAgent` out to a persisted "operator run" (Morpheus item C: `IRunStore`, `AgentName="Operator"`, no work plan, RunEvents on `IRunEventStream`) and only after cutover retire `/api/console/turn` + `ConsoleTurnService` (the regex router) — kept intact by this spike.
6. **Cost/latency**: no agent-host pod path (confirmed by Morpheus); this is an in-API session like the current facade. No warm-pool dependency.

Bottom line: the biggest design unknown is resolved — **use the `ModelContextProtocol` C# client; no native SDK MCP support exists but none is needed. The adapter is thin, real, and bearer-safe.**

---

**Merged from inbox file:** `tank-approval-scope.md`

# Tank — Fix: Cross-User Persistent Tool-Approval Authorization

**Verdict: CONFIRMED VULNERABILITY (cross-tenant privilege boundary break) — FIXED.**
**Author:** Tank (Backend Engineer) — independent revision after a prior rejected attempt
**Date:** 2026-07-17
**Requested by:** Ahmed Sabbour (@sabbour)
**Branch:** `fix/assistant-approval-scope`  **Worktree:** `.worktrees/fix-assistant-approval-scope`

---

## The vulnerability

`DurableToolApprovalGate` persisted a `scope:"always"` grant under a process-wide
constant bucket (`__agentweaver_tool_approvals__`, aka `GlobalRunId`), and
`IsAutoApproved` consulted that global bucket for **any** run. Owner/tenant A's
"always" grant therefore silently auto-approved future runs of owner/tenant B — a
cross-user / cross-tenant privilege-boundary break. The warm-pooled `AgentHost`
in-memory gate had the analogous in-process leak: an "always" grant survived pod
reconfiguration to a different user.

## The security invariant (one sentence)

A persistent (`always`) or run-scoped approval authorizes **only** the run's
canonical server-persisted owner (`Run.SubmittingUser`, resolved via `IRunStore`
in the API and via `AgentHostRuntimeState.UserId` for the matching configured run
in the warm pod) — the owner is the durable authorization subject, is derived from
server state (never from the request body), is stored on the grant and re-checked
on lookup, and if it cannot be resolved the path **fails closed** (no persistence,
no auto-approval).

## What changed (surgical)

- **`DurableToolApprovalGate`**: removed `GlobalRunId`. `Always` grants are stored in
  an **owner-scoped bucket** `"__agentweaver_tool_approvals_owner_sha256_v1__" + SHA256(owner)`
  as a `PolicyGrant(Owner, ToolId, RiskSemantics)`. `GrantAsync` resolves the owner
  from `IRunStore.GetAsync(runId).SubmittingUser`; if null → the `always`/run policy
  is **not** persisted. `IsAutoApproved` resolves the owner and matches on
  bucket **and** the stored `Owner`/`ToolId`/`RiskSemantics`. Legacy `{policyKey}`
  payloads deserialize to an all-null `PolicyGrant` and match nobody (fail-closed);
  the global bucket is never consulted. Parent-run propagation only when
  `parentOwner == owner`.
- **`RunEndpoints`** (`tool-approvals`, `tool-denials`): the server-resolved
  `targetRun` (owning child run, resolved from persisted state — not the body) now
  gets a null-check (`404`) and `EndpointHelpers.IsOwner(httpContext, targetRun)`
  (`403`) before the grant/deny is applied.
- **`InMemoryToolApprovalGate`** (AgentHost, defense-in-depth for the warm-pool leak):
  owner-scoped run allowlist + owner-scoped always policies, owner resolved via a new
  `IToolApprovalOwnerResolver`.
- **`AgentHostToolApprovalOwnerResolver`** + `AddAgentHostRuntime()`: resolves owner
  from `AgentHostRuntimeState.UserId` **only when** `runId` equals the configured run;
  otherwise null → fail closed. Registered ahead of `AddAgentRuntime()`.
- **DTO/doc**: `ToolApprovalRequest` `scope:"always"` documented as "future runs owned
  by the same persisted user"; the DTO carries **no** owner field (no caller-supplied
  owner anywhere).

## Decision on the AgentHost changes (kept)

**Kept.** AgentHost pods are warm-pooled and reconfigured across runs/users via
`POST /configure`. An in-memory "always" grant genuinely leaks across users within a
reused process, so owner-scoping the in-memory gate is real defense-in-depth, not dead
work. The `IToolApprovalOwnerResolver` seam keeps the runtime package free of an API/
run-store dependency while binding the owner to server state; it fails closed when the
run isn't the configured one.

## Test evidence (zero skips on approval tests)

- **SQLite** (`--no-build`): `ToolApprovalGateTests`, `ToolApprovalEndpointTests`,
  `DurableRunControlStateTests`, `AgentHostToolApprovalEndpointTests`
  → **60 passed, 0 skipped**. Plus `PermissionDecisionRegressionTests` +
  `SecurityAndRaceTests` → **20 passed, 0 skipped** (no endpoint regressions).
- **Forced PostgreSQL** (`AGENTWEAVER_FORCE_POSTGRES_TESTCONTAINERS=1`, Testcontainers
  `postgres:16-alpine`): `ToolApprovalPersistenceTests` → **1 passed, 0 skipped**.
- Cross-owner denial proven by: `AlwaysApproval_ByAlice_DoesNotAutoApproveBobsPersistedRun`
  (gate), `ApproveAlways_AffectsOnlyPersistedInitiatingOwner` +
  `Approve_ParentOwnerCannotGrantApprovalOwnedByDifferentPersistedChildOwner` (endpoint, 403),
  and `OwnerScopedAlwaysGrant_IsReplicaSafeAndRejectsLegacyAndOtherOwner` (Postgres, 2 replicas).
- Legacy fail-closed proven by: `LegacyGlobalAndUnscopedOwnerBucketGrants_AuthorizeNobody`
  and the Postgres test (seeds an old global `{policyKey}` grant → authorizes nobody).

## Residual concern (non-blocking, out of scope)

`IsAutoApproved` is a synchronous interface method, so owner resolution does a
sync-over-async `IRunStore.GetAsync` on each check (including the 250 ms wait loop).
Correct and non-deadlocking (no sync context in the host), but a future perf pass could
make the gate lookup async or cache the owner per run. Not a security issue.

---

**Merged from inbox file:** `tank-approval-still-broken.md`

# Tank — "Assistant still narrates permission denied" — ROOT CAUSE FOUND + FIXED

**Date:** 2026-07-15
**Branch:** `fix/assistant-approval-sink` (local only, NOT pushed/merged)
**Author:** Tank (Backend)
**Re:** Ahmed's live retest — assistant said *"tool access is currently blocked in this
session (permission denied) … Once you approve tool access, I'll add this to your Blog
project backlog."*

---

## Confirmed root cause: (b) — a different denial path, at the SDK permission layer

**It is NOT (a) a stale build, and NOT (c) a frontend problem.** The real cause:

> The operator assistant's Copilot session (`OperatorAssistantAgent.BuildSessionConfig`)
> did **not register an `OnPermissionRequest` handler**. Every MCP tool the model calls
> raises an SDK **permission request** (MCP tools carry no `skip_permission` flag). In the
> headless in-API session there was nothing to answer that prompt, so the GitHub Copilot
> SDK resolved **every** tool call — including read-only discovery like resolving the Blog
> project id — as **DENIED**. The model faithfully narrated that denial as "permission
> denied" with no approval card.

My earlier approval-gating wrapper (`ApprovalGatingAIFunction`, commit `17a8fb11`) runs
*inside* the tool invocation, so it was **never reached** — the SDK denied the call one
layer earlier, at the permission gate. That is why my first fix appeared to do nothing
live, and why a plain rebuild of `17a8fb11` alone would *still* have been broken.

### Evidence
- `OperatorAssistantAgent.BuildSessionConfig` at `17a8fb11` sets `Tools` but **no**
  `OnPermissionRequest` and **no** `AvailableTools`.
- GitHub.Copilot.SDK docs: every example sets `OnPermissionRequest = PermissionHandler.ApproveAll`;
  tools opt out of prompting only via `CopilotTool.SkipPermissionKey` ("skip_permission": true),
  which MCP-adapted tools do **not** carry. No handler ⇒ prompts resolve as denied.
- The screenshot symptom (blanket denial, *including* the read needed to resolve the project id,
  and the model explicitly waiting for "approve tool access") matches an SDK-permission-layer
  denial, not my in-invocation gate and not a missing allowlist entry.
- The scenario tool is `backlog_capture_task` ("add a backlog task"). It is **intentionally
  ungated** by `OperatorToolApprovalPolicy` (a low-consequence write), so it should simply run
  — it was never supposed to show an approval card. Adding it to the allowlist would **not**
  have fixed anything, because the denial came from the SDK permission layer, not my policy.

## The fix

`OnPermissionRequest = RejectNativeToolPermissionHandler` + `AvailableTools` allowlist added to
`BuildSessionConfig`. The handler **approves** MCP/custom tool requests (so calls proceed to the
tool, where the consequential subset is still human-gated by `ApprovalGatingAIFunction`) and
**rejects** native shell/file/URL requests (defense-in-depth for the unsandboxed in-API session).

This landed as **Seraph's commit `97178cab`** ("restrict operator assistant to MCP tools; disable
SDK built-in bash/file tools"), which is built directly on top of my `17a8fb11`. The approve-MCP
branch of that handler *is* the approval-bug fix; the native-tool rejection is Seraph's parallel
sandbox hardening — same handler, two concerns.

I fast-forwarded `fix/assistant-approval-sink` to include `97178cab` so the branch works
end-to-end, and added a **regression test dedicated to the approve path** (Seraph's tests only
covered native rejection).

## Exact commits / files (branch `fix/assistant-approval-sink`)
- `17a8fb11` (mine) — approval gate + `GET /api/assistant/runs`.
- `97178cab` (Seraph) — `OnPermissionRequest` handler (**the actual "permission denied" fix**) +
  `AvailableTools` allowlist. Files: `packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs`,
  `tests/.../Assistant/OperatorMcpAdapterSpikeTests.cs`.
- `692cb9f3` (mine) — regression test
  `OperatorPermissionHandler_ApprovesMcpAndCustomToolCalls_SoTheyReachTheApprovalGate` asserting the
  handler **approves** `PermissionRequestMcp` (`backlog_capture_task`) and `PermissionRequestCustomTool`
  (`project_list`) rather than denying them.

## Validation
- `dotnet test … --filter "FullyQualifiedName~Assistant"` → **17/17 pass** (16 existing + my new one).
- Build requires `-p:CopilotSkipCliDownload=true` (offline).

## Step-by-step verification for Ahmed
1. Use a build that includes commit `97178cab` (or `692cb9f3`) — i.e. build from
   `fix/assistant-approval-sink` (or `fix/assistant-sandbox-restriction`). **A build from `17a8fb11`
   or from `main` will still be broken** — this was the trap: the approval commit alone is not enough.
2. Rebuild the API + restart it (and MCP on :5100). Old binaries have no `OnPermissionRequest`.
3. In the assistant, send: *"add a backlog task to the Blog project: Write a blog post about multi
   clusters."*
4. Expected now: the assistant resolves the Blog project id (read tools no longer denied) and calls
   `backlog_capture_task` — which runs directly (it's an ungated low-consequence write). The task
   appears on the Blog backlog. **No "permission denied", no spurious approval card.**
5. To see the approval card path, use a *gated* action instead (e.g. "start the coordinator on the
   Blog project" → `coordinator_start`): that surfaces a real `tool.approval_required` card, and the
   tool runs only after you approve.

## Coordination notes
- Shared main working tree is volatile: during this task it was switched to `fix/assistant-activity-ui`
  (trinity-3 active) and Seraph's edits appeared/reverted mid-build. I did all my work in a dedicated
  worktree `.worktrees/tank-approval-sink` to avoid disturbing siblings, and committed only my test file.
- **Seraph:** your `97178cab` handler is the load-bearing fix for the operator-assistant approval bug,
  not just sandbox hardening. Please keep the *approve-MCP/custom* branch intact — if it regresses to
  deny-by-default for MCP/custom requests, all assistant tool access breaks again. My regression test
  `OperatorPermissionHandler_ApprovesMcpAndCustomToolCalls_…` guards exactly that.
- Nothing here is a frontend bug — Trinity's approval UI is fine; the backend simply never let the
  tool calls through.

---

**Merged from inbox file:** `tank-assistant-approval-sink.md`

# Tank — Operator Assistant tool-approval sink + run list (#346)

**Date:** 2026-07-15
**Branch:** `fix/assistant-approval-sink` (off `main`, committed locally — NOT pushed)
**Author:** Tank (Backend)

## Problem
Trinity confirmed the frontend approval UI (`AssistantApprovalGate` + `derivePendingApprovals`
in `apps/web/src/pages/AssistantRunPage.tsx`) is correct and wired to the generic
`apiClient.approveTool`/`denyTool` endpoints, but **nothing on the backend ever emitted
`tool.approval_required` for operator-assistant runs** — gated MCP tool calls were silently
allowed/denied with no human-in-the-loop gate. Separately, there was no way to list a
caller's own assistant conversations.

## What I changed

### Task 1 — Real tool-approval gating for the operator assistant
- **New sink hook.** Added `OnApprovalRequiredAsync(requestId, toolName, argumentsJson, ct) -> ValueTask<bool>`
  to `IOperatorAssistantTurnSink` (`OperatorAssistantAgent.cs`).
- **Tool wrapping.** `RunTurnAsync` now builds tool declarations via `BuildToolDeclarations`, which
  wraps any tool that `OperatorToolApprovalPolicy.RequiresApproval` flags in a delegating
  `ApprovalGatingAIFunction`. On invocation it generates a requestId, calls the sink's approval
  hook, and only invokes the real MCP tool on approval; on denial it returns a clear
  "denied by operator" string to the model so the conversation continues sensibly.
- **Approval policy.** New `OperatorToolApprovalPolicy` — an explicit allow-list of ~18
  consequential tools (coordinator_start, run_submit, run_task, run_retry, start_preview,
  session_start, coordinator_steer, coordinator_outcome_spec_confirm, run_review,
  decision_inbox_merge/reject, squad_decide, project_delete, backlog_delete_task/archive_task,
  send_all_backlog_to_ready, run_archive, skill_delete, team_member_retire). MCP tools carry no
  machine-readable approval annotation, and the operator system prompt already enumerates these
  "ask first" categories — the read/discovery majority stays ungated (no behavior change for
  normal usage).
- **Sink implementation** (`AssistantRunService.RunEventSink.OnApprovalRequiredAsync`): reuses the
  **existing** `IToolApprovalGate` (production `DurableToolApprovalGate`) — no parallel mechanism.
  It starts `WaitForApprovalAsync` FIRST (the gate registers the approval context synchronously
  before its first await, so the generic approve/deny endpoint can already resolve the requestId),
  then emits `tool.approval_required` `{ requestId, displayId, toolName, arguments, message }`,
  heartbeats `tool.approval_pending` while waiting, and on resolution emits
  `tool.approval_resolved` `{ requestId, runId, approved }` on the run's own stream so the pending
  card clears on reload. 5-minute expiry == denial. The operator resolves via the SAME generic
  `POST /api/runs/{id}/tool-approvals` / `tool-denials` endpoints the frontend already calls.

### Task 2 — List a caller's assistant runs
- `GET /api/assistant/runs?limit=50` — newest-first, caller-scoped (`caller.Owns`), returns
  `{ runs: [ { run_id, status, title, created_at } ] }`. `title` is the first user message
  truncated to 80 chars.
- Added `GetRunsBySubmittingUserAsync` to `IRunStore` (default-throwing interface method so test
  fakes don't break) with real impls in `EfRunStore` and `SqliteRunStore`
  (filter SubmittingUser + not archived, optional agent-name filter, order by StartedAt desc, take).

## Key design findings (for future maintainers)
- **Two `tool.approval_resolved` events exist for operator runs.** The durable control-state append
  (`DurableToolApprovalGate` uses the constant `"tool.approval_resolved"` for its `RequestResolved`
  state) surfaces on `/events` as a PascalCase `ApprovalResolution` record `{ RequestId, Approved,
  Expired }`. The gate's camelCase `EmitResolved` goes through `RunStreamStore.Get(runId)`, which is
  a **no-op for operator runs** (they aren't registered there). So the sink emits its own camelCase
  `tool.approval_resolved` — that is the one the frontend's `derivePendingApprovals` matches
  (`requestId`/`approved`). Tests must select the camelCase event, not `.First()`.
- **The message POST blocks for the whole turn**, including the approval wait; the operator approves
  via a SEPARATE endpoint that resolves the gate and unblocks the POST. Matches the existing
  single-turn design.

## Validation
- `dotnet test ... --filter FullyQualifiedName~Assistant` → **15/15 pass** (incl. new approval
  emit/approve, deny-returns-false, list-scoping, list-requires-auth tests).
- `dotnet test ... --filter "FullyQualifiedName~ToolApproval|FullyQualifiedName~Coordinator"` →
  **801/801 pass** — no regression to coordinator/child approval gating.
- Build requires `-p:CopilotSkipCliDownload=true` offline (skips the Copilot CLI runtime download,
  not compilation).

## Not done / follow-ups
- No live end-to-end run against Ahmed's local API(:5000)/MCP(:5100) — avoided disrupting his live
  session; unit/integration coverage is sufficient per instructions.
- Frontend "recent conversations" list that consumes `GET /api/assistant/runs` is Trinity's follow-up.

## Files
- `packages/Agentweaver.AgentRuntime/OperatorToolApprovalPolicy.cs` (new)
- `packages/Agentweaver.AgentRuntime/OperatorAssistantAgent.cs`
- `apps/Agentweaver.Api/Assistant/AssistantRunService.cs`
- `apps/Agentweaver.Api/Endpoints/AssistantEndpoints.cs`
- `apps/Agentweaver.Api/Contracts/Dtos.cs`
- `apps/Agentweaver.Api/Infrastructure/IRunStore.cs`
- `apps/Agentweaver.Api/Infrastructure/Ef/EfRunStore.cs`
- `apps/Agentweaver.Api/Infrastructure/SqliteRunStore.cs`
- `apps/Agentweaver.Api/API.md`
- `tests/Agentweaver.Tests/Assistant/AssistantRunEndpointsTests.cs`

---

**Merged from inbox file:** `trinity-346-frontend-scaffold.md`

# Frontend scaffold: AssistantRunPage for MCP-driven operator chat (#346)

**Status**: SCAFFOLD COMPLETE — frontend page built, backend calls stubbed pending Tank
**By**: Trinity (Frontend Engineer)
**Requested by**: Ahmed Sabbour (sabbour)
**Branch**: `feat/assistant-run-page-346` (worktree `.worktrees/impl-346-assistant-frontend`, commit `0b7dc73d`)
**Design basis**: Morpheus `decisions/inbox/morpheus-operator-dock-design.md` (frontend items 6–7); Tank `decisions/inbox/tank-346-mcp-adapter-spike.md` (backend contract).

## What I built (all additive; BrowserConsole/operator-dock untouched)

1. `apps/web/src/pages/AssistantRunPage.tsx` — new page.
   - A **leaner, purpose-built** variant of CoordinatorRunPage's single-agent / no-work-plan
     path rather than a copy of the 1000+ line file. It reuses the SAME run-stream primitives
     the coordinator page uses: `useSeededRunStream` (seed via `getRunEvents` + live SSE),
     `buildRunTimeline` + `RunTimeline` (embedded transcript), the shared `Composer`
     (ui/copilot), and `ApprovalGate` (ui/agentic). No DAG / work-plan / assembly chrome —
     the center IS the transcript.
   - Composer submit: FIRST message calls `apiClient.createAssistantRun(...)` (creates the run,
     sets local runId, binds the stream); SUBSEQUENT messages call
     `apiClient.sendAssistantMessage(runId, ...)`.
   - Tool-approval UI: `derivePendingApprovals()` scans stream events for
     `tool.approval_required` / `shell.approval_required`, drops any resolved by a later
     `tool.approval_resolved` / `tool.auto_approved`, and renders a local `AssistantApprovalGate`
     wired to the existing `apiClient.approveTool/denyTool/approveShell/denyShell` (once / for
     session / always / deny). Operator run has no children, so approvals target the run itself.
2. `apps/web/src/routes/AssistantRoute.tsx` + route `/assistant` in `App.tsx`.
   - Feature-flagged rollout (per design): enabled by `?assistant=1` (persisted to
     `localStorage['agentweaver.assistant.enabled']`), cleared by `?assistant=0`; otherwise the
     persisted flag decides. Disabled → `<Navigate to="/" replace />`. The LeftNav operator-dock
     trigger is NOT rewired in this pass.
3. `apps/web/src/__tests__/AssistantRunPage.test.tsx` — 4 tests: renders + empty state;
   first submit calls create-run stub; follow-up calls send-message stub; tool-approval UI
   renders on a simulated `tool.approval_required` event and Approve wires to `approveTool`.

## What is STUBBED (pending Tank's `tank-346-backend`)

`apiClient.createAssistantRun` / `apiClient.sendAssistantMessage` (in `apps/web/src/api/client.ts`)
return a **local fake** (no network) with a clearly marked `// TODO(#346): wire to real backend
once tank-346-backend lands`. The real `this.request(...)` calls are present but commented out.
Swap the fake for the real call when the backend branch merges. Everything else
(`getRunEvents`, `useRunStream`, `approveTool/denyTool/approveShell/denyShell`) uses the EXISTING
run endpoints unchanged, matching Morpheus's "reuse the run SSE" design.

## Assumed stub API shape — RECONCILE against Tank's actual contract

Defined in `apps/web/src/api/types.ts` (block marked "Assistant (operator) run endpoints (#346)"):

- `POST /api/assistant/runs`
  - Request `CreateAssistantRunRequest`: `{ message: string; project_id?: string }`
    (first user message seeds the run; project_id scopes project-aware MCP tools).
  - Response `CreateAssistantRunResponse`: `{ run_id: string; status?: string }`
    (`run_id` is bound directly to the existing run-stream, same as a coordinator run id).
- `POST /api/assistant/runs/{id}/messages`
  - Request `SendAssistantMessageRequest`: `{ message: string }`.
  - Response `SendAssistantMessageResponse`: `{ status: 'queued' | 'applied' | string }`
    (modeled on the `/steer` response; reply arrives on the run stream).
- Transcript/approvals reuse UNCHANGED: `GET /api/runs/{id}/events`, `GET /api/runs/{id}/stream`,
  `POST /api/runs/{id}/tool-approvals` (+ `/tool-denials`, `/shell-approvals`, `/shell-denials`).

Points to confirm with Tank: (a) exact field names (`message` vs `content`; `project_id` casing);
(b) whether create returns the run in one call or requires a follow-up first-message POST;
(c) whether approvals on an operator run ever carry a `child_run_id` (assumed never — no children);
(d) response `status` enum values.

## Validation

- `npm --prefix apps/web run build` (tsc -b && vite build): PASS, 0 errors.
- `npm --prefix apps/web test -- --run`: **84 files, 783 tests, all pass** (includes the 4 new
  AssistantRunPage tests; no regressions). Full suite run, not just targeted.
- `eslint` on all changed files: clean.
- DESIGN.md "no private dependencies": followed — native FluentUI + `components/ui/copilot`
  (Composer) + `components/ui/agentic` (ApprovalGate) only; no direct `@1js/fluentai` imports added.

## Follow-up tasks (noted, not done this pass)

1. Wire `createAssistantRun` / `sendAssistantMessage` to the real endpoints once
   `tank-346-backend` lands; delete the local fake + reconcile the shapes above.
2. (Optional refactor) Extract CoordinatorRunPage's single-agent transcript path into a shared
   component consumed by both pages, instead of the lean reuse-the-primitives approach here.
3. LATER (explicitly out of scope now): rewire the LeftNav "operator dock" trigger to open an
   assistant run and retire `BrowserConsole` / `ConsolePanelContext` / `consoleCommands` once the
   new page is proven end-to-end.

---

**Merged from inbox file:** `trinity-346-wireup.md`

# Trinity — AssistantRunPage wireup notes (#346)

**Date:** 2026-07-15  
**Author:** Trinity (frontend)  
**Branch:** feat/assistant-wire-real-backend-346  
**Worktree:** .worktrees/wire-346-frontend  
**Commit:** cdc0472b

---

## What changed

### api/client.ts
Replaced the `createAssistantRun` and `sendAssistantMessage` stub methods (which
returned local fake responses) with real `this.request()` calls:
- `POST /api/assistant/runs` (201)
- `POST /api/assistant/runs/{id}/messages` (200)

Removed the `TODO(#346)` stub comment block entirely.

### api/types.ts
Updated `CreateAssistantRunResponse` and `SendAssistantMessageResponse` to match
the real backend contract from Tank's `StartAssistantRunResponse` /
`AssistantMessageResponse` DTOs:
- `CreateAssistantRunResponse`: `{run_id, status:'in_progress'|string, message?, tools_invoked?}`
- `SendAssistantMessageResponse`: `{run_id, role:'assistant'|string, message, status, tools_invoked?}`

Removed the `STUB CONTRACT` comment header.

### pages/AssistantRunPage.tsx
Added three new error-handling cases in `handleSubmit`:

1. **429 `operator_run_limit`** (on create): shows "You have too many active
   assistant conversations. End one before starting another." The run is not
   started and empty-state remains.

2. **404 `run_not_found`** (on send): resets `runId` to `''` (returns page to
   start state) and shows "This conversation timed out. Start a new one below."
   The previous transcript is visible below the notice since the RunTimeline is
   still rendered (idleTimedOut notice appears).

3. **`run.completed {reason:'idle_timeout'}`** (stream event): derived via a
   `useMemo` over the live event stream. When detected, renders a
   `data-testid="assistant-idle-timeout"` notice in the transcript area with
   "Conversation ended due to inactivity. Start a new one below."

Also imported `ApiError` from `'../api/client'` and `parseApiBody` from
`'../api/errors'` for precise error-code discrimination.

### timeline/runTimelineSteps.ts
Added `role?: 'user' | 'assistant'` to `RunTimelineMessage` (optional — existing
creation sites in `AgentSessionPanel` that don't set it continue to render as
assistant messages). Updated the `agent.message` and `agent.message.delta` case
handlers to capture `role` from the event payload (`asStr(payload['role'])`,
defaulting missing/unknown values to `'assistant'`).

### components/RunTimeline.tsx
Added `messageUser` and `messageUserLabel` styles. Updated `MessageBlock` to
render a `data-role="user"` variant with a "You" label and a light background
when `message.role === 'user'`. All other messages (role `'assistant'` or absent)
render unchanged.

---

## Tests

`AssistantRunPage.test.tsx` — 7 tests, all passing:
- Updated the 4 existing tests to use real response shapes
  (`{run_id:'assistant-run-1', status:'in_progress', ...}` vs the old stub shape)
- Added 3 new tests:
  - 429 `operator_run_limit` shows specific "too many conversations" message
  - 404 `run_not_found` shows timeout notice and resets to empty state
  - `run.completed {reason:'idle_timeout'}` renders the idle-timeout banner

Full suite: 786 tests, 785 passing. 1 timeout in
`CoordinatorRunPage.coordUx.test.tsx > declutters the run header` — this is a
pre-existing flaky timeout (5000ms limit; passes in isolation and on main).

Build: clean (0 TypeScript errors, vite bundle 2.96 MB).

---

## Field-name reconciliation

| Frontend stub (before)      | Real backend (after)                    |
|-----------------------------|-----------------------------------------|
| `status: 'created'`         | `status: 'in_progress'`                 |
| `status: 'queued'`          | `status: 'in_progress'` (send response) |
| _(no role)_                 | `role: 'assistant'` on send response    |
| _(no message/tools_invoked)_| `message?: string`, `tools_invoked?: string[]` |

---

## Constraints met

- Feature flag `?assistant=1` gating untouched (AssistantRoute unchanged)
- Backend code not touched
- No hacks to fake test passes
- All changes inside the worktree

---

## 2026-07-17T08-44-16-07-00 — Cross-user persistent tool-approval authorization fixed and approved

**Status**: APPROVED security fix, recorded after independent author/reviewer separation.  
**Author**: Tank (Backend Engineer), independent revision author.  
**Reviewer**: Seraph (Security Reviewer), independent security review.  
**Commit**: `cfcd76c5` (`fix/assistant-approval-scope`).  
**Requested by**: Ahmed Sabbour.

### Decision

Persistent (`always`) and run-scoped tool approvals authorize only the run's canonical server-persisted owner: `Run.SubmittingUser` resolved through `IRunStore` in the API, or `AgentHostRuntimeState.UserId` for the matching configured run in the warm-pool AgentHost. The owner is never trusted from caller-supplied request data.

If the run owner cannot be resolved, approval persistence and auto-approval fail closed. Legacy global/unscoped grants authorize nobody, and the former process-wide `GlobalRunId` bucket is not a valid authorization source.

The rejected-fix separation requirement was honored: Tank authored the revision in `.worktrees/fix-assistant-approval-scope`, and Seraph independently reviewed commit `cfcd76c5`. Seraph's verdict was **APPROVE**, with six attack vectors verified: cross-user auto-approval, caller-supplied owner trust, fail-open behavior, warm-pool leakage, parent/child cross-run leakage, and semantics spoofing.

---

## 2026-07-18T02:58:30-07:00 — Staging AKS recovery and GitHub OAuth credential incident

**Status:** Resolved and live-verified.
**Environment:** `agentweaver-rg` staging, now
`https://agentweaver.6a5ae3080033ff0001ec6c42.westus2.staging.aksapp.io`.

### Recovery

The staging `agentweaver-rg` environment was found deleted and was reprovisioned using the new
PowerShell ports of the AKS provisioning workflow (commit `f5053ea3`). During recovery, a
background Link agent incorrectly wrote LOCAL-DEV's GitHub OAuth App credentials into the staging
Key Vault. The browser GitHub login flow broke; bearer-token/API access remained unaffected.

### Root cause and correction

The auto-resolve fallback added to `15-setup-identity.ps1` and `.sh` read .NET user-secrets. That
source is local-development-only and is invalid for staging because the environments use separate
GitHub OAuth Apps. Commit `75a84f38` removed auto-resolution entirely; staging credentials now
require explicit operator input at the prompt fallback.

The correct staging credentials were recovered from Key Vault version history by matching the
`github-client-id` / `github-client-secret` versions timestamped
`2026-07-14T20:03:46Z` / `2026-07-14T20:03:50Z` to the user-confirmed client ID
`Ov23liDx3W5jbG4KxA8l`. They were reapplied as the newest secret versions; the incorrect versions
were retained for audit. This recovery established that the vault had not actually been purged,
despite `az keyvault list-deleted` showing nothing — an unresolved discrepancy to investigate.

API and worker pods were rollout-restarted. `40-verify.ps1` passed **23/23**, including a live OAuth
redirect check. The correct OAuth App callback is
`https://<host>/auth/github/callback`, not `/api/auth/github/callback`; the earlier coordinator
reminder was corrected after checking `k8s/api-deployment.yaml`.

---

**Merged from inbox file:** `copilot-directive-2026-07-18T02-45-00.md`

### 2026-07-18T02:45:00-07:00: User directive
**By:** Ahmed Sabbour (via Copilot)
**What:** Commit work locally to the `main` branch, but do not push to `origin/main` until explicitly
told to do so. This supersedes the earlier standing convention of pushing directly to `main` after
verification.
**Why:** Reduce push conflicts while multiple agents concurrently commit to `main`.

---

**Merged from inbox file:** `copilot-directive-2026-07-18T02-41-57.md`

### 2026-07-18T02:41:57-07:00: User directive
**By:** Ahmed Sabbour (via Copilot)
**What:** Use pnpm/npm scripts (`infra:deploy`, `release:images`, `release:deploy`, `dev:web`,
`dev:api`, and `dev`) as the canonical workflow for AKS build/release/deploy and local development,
rather than calling `scripts/aks/*.ps1` or `.sh` directly.
**Why:** User request — captured for team memory.

### Documentation update

Commits `f3663762` and `837b90ff` updated the docs to make
`npm run infra:deploy`, `release:images`, `release:deploy`, `dev:web`, `dev:api`, and `dev` the
plainly documented primary workflows. Direct `scripts/aks/*.ps1` / `.sh` use remains documented
separately under a simple **Running an individual step** section, without “under the hood” or
“advanced” framing.

---

**Merged from inbox file:** `link-1-cross-platform-package-scripts.md`

## Cross-platform package-script launcher

- **Decision:** Root AKS package scripts call `scripts/run-os-script.mjs`. It runs the `.ps1`
  counterpart on Windows and the `.sh` counterpart on POSIX, with a Windows fallback to the bash
  script while a PowerShell port is unavailable.
- **Rationale:** This preserves the existing paired AKS scripts without changing package managers
  or requiring callers to select a shell-specific command.
- **Compatibility:** `dev` and `start` explicitly invoke `pwsh.exe` so a WSL bash invocation uses
  Windows PowerShell for the intentional Windows/WSL orchestrator.

---

**Merged from inbox file:** `link-powershell-aks-recovery.md`

## PowerShell-only AKS staging recovery

- **Owner:** Link
- **Date:** 2026-07-17
- **Decision:** Port the AKS provisioning and verification workflow to PowerShell while preserving
  the bash scripts' resource names, flags, ordering, and idempotency guards.
- **Rationale:** The staging environment must be recoverable from Windows without invoking Bash,
  WSL, or `.sh` scripts.
- **Implementation:** Added PowerShell ports for cluster, identity, monitoring, OAuth key,
  PostgreSQL, deployment verification, and the A2A mTLS helper. Updated `30-deploy.ps1` to invoke
  only PowerShell helpers.

**Superseded detail:** The original inbox note said identity provisioning resolves GitHub OAuth
credentials from local .NET user-secrets before prompting. That unsafe fallback caused the
incident recorded above and was removed in `75a84f38`; staging credentials now require explicit
operator input.

---

## 2026-07-18T04:55:22-07:00 — Push workflow restored

**By:** Ahmed Sabbour
**Decision:** The temporary hold-push directive is lifted. Once work is verified, commit and push
directly to `origin/main`; no separate push confirmation is needed. This supersedes the
2026-07-18T02:45:00-07:00 local-commit-only directive.
