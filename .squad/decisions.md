# Squad Decisions

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