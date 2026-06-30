# Squad Decisions Archive

Archived entries older than 2026-06-04.

[See active decisions at: .squad/decisions.md]

## Archived Decisions (2026-06-04 and older)

### 2026-06-07: Monorepo Scaffold for 001-single-agent-run (link-scaffold-001)

Scaffolded complete monorepo: scaffolders.sln with 5 .NET projects + web app. Full solution builds clean. Deviation: targeting net9.0 (no .NET 10 SDK available); retarget to net10.0 when SDK installed.

### 2026-06-07: Domain Models and Sandboxed File Tools (morpheus-domain-sandbox-001)

Implemented Scaffolder.Domain (10 types + 15 payload records) and Scaffolder.SandboxFs (hardened sandbox validator with open-then-verify, ancestor walk, reparse-point rejection, 7-layer defense). Both build clean.

### 2026-06-07: Single Agent Loop, Provider Adapters, Content Safety (morpheus-runtime-001)

Implemented Scaffolder.AgentRuntime: MAF loop with GitHub Copilot + Foundry providers, content-safety gate, run-bounds enforcement. Moved IAgentRunner to Domain contract (circular-ref fix). AppendAsync now returns event with allocated sequence. Solution builds 0 warnings / 0 errors.

### 2026-06-07: Backend API — run lifecycle, streaming, review, merge (tank-api-001)

Implemented Scaffolder.Api: SQLite append-only event store (WAL + triggers rejecting UPDATE/DELETE), SSE fan-out broadcaster with sequence dedup, LibGit2Sharp worktree manager, run orchestrator, 5 endpoints with bearer auth + approval gate. Full solution builds clean.

### 2026-06-07: CLI and Web clients — submit, watch, review, show (trinity-clients-001)

Built Scaffolder.Cli (Spectre.Console: 4 commands, SSE client, bearer auth) and apps/web (React 19 + Fluent 2: RunSubmitForm, RunWatcher, RunReview, RunDetail). Both build clean, web lints clean.

### 2026-06-07: Test Suite — 43/43 Passing (smith-test-suite)

Created test suite with 43 tests across 7 files: SandboxPathValidator (9), EventType (4), SqliteEventStore (6), RunEventBroadcaster (5), ApprovalGate (5), ModelSourceValidation (6), ContentSafetyChecker (8). All passing, no mocks/fakes.

### 2026-06-07: Security & RAI Review — 001-single-agent-run (seraph-pre-implementation)

YELLOW verdict on pre-implementation architecture review. 3 critical findings (S1: sandbox prefix-check bypassable, S7: content-safety tool-call gap, S9: SSE unauthenticated) flagged for design-in during implementation. 11 additional high/medium findings documented. Seraph to re-consult at post-implementation.

### 2026-06-08: Fix empty GitHub Copilot streaming chunks and late replay (morpheus-streaming-empty-chunks)

Root-caused MAF GitHub Copilot streaming: final AssistantMessageEvent text can live in RawRepresentation instead of TextContent, leaving chunk.Text empty. Initial fix reads final message content from RawRepresentation, emits delta events with messageId, and retains bounded completed-run stream history so late clients can replay deltas instead of a single final Result. Residual risk: durable per-event replay still requires SQLite persistence across API restarts.

### 2026-06-08: Reviewer Rejection Protocol for streaming fix (Squad-Coordinator-seraph-red-on-streaming-fix-triggers-reviewer-reje)

Seraph issued a RED verdict on the initial streaming fix because /api/runs/{id}/stream lacked per-run ownership checks and could disclose another user's run output. Rubberduck also found a GetSince/IsCompleted tail-event race and Channel SingleReader=true violation with multiple clients. Per protocol, Morpheus was locked out of the revision; Tank became owner, with Seraph required to re-review to Green/Yellow before ship.

### 2026-06-08: Streaming fix security review RED (seraph-streaming-fix-review)

Seraph blocked shipment of the initial streaming patch due to missing authorization on the SSE stream endpoint. Advisory findings also called out unbounded per-run history, eviction-race hardening, Info-level task content logging, raw exception messages sent to clients, and null-safety for chunk.Contents. Event sequence integrity for retained runs was assessed as sound.

### 2026-06-08: Streaming security and concurrency revision (tank-streaming-fix-revision)

Tank fixed the streaming revision after lockout: stream endpoint now authorizes before sending data, using in-memory entry.Owner for live runs and persistent Run.SubmittingUser as DB fallback for completed/evicted runs, returning 404 on non-owners. RunStreamStore now exposes atomic GetSnapshotSince, eliminates the multi-reader Channel design, caps per-run history at 10,000 events, evicts stale entries after 2 hours, moves task previews to Debug, genericizes client errors, null-guards chunk.Contents, and handles null messageId dedup. Build passed and 27 tests passed.

### 2026-06-08: Streaming fix re-review GREEN (seraph-streaming-rereview)

Seraph re-reviewed Tank's revision and cleared the prior RED. Authorization is applied before SSE headers or content, fails closed, and avoids run-id enumeration by returning 404 for non-owners. Prior yellow findings were verified fixed. Remaining advisory: GET /api/runs/{id} still returns 403 for non-owners while stream returns 404; consider aligning GET to 404.

### 2026-06-08: Per-event RunStreamStore wake-up (tank-stream-wakeup)

Tank removed the non-blocking ~1s per-event latency by adding a volatile TaskCompletionSource event signal to RunStreamStore. Record() atomically appends an event, swaps the signal, and wakes all clients blocked in WaitForChangeAsync; WaitForChangeAsync now races event signal, completion signal, timeout, and cancellation. No channel was reintroduced. Build passed and 28 tests passed, including a new wake-up latency test.

### 2026-06-08: index.css reduced to minimal reset (trinity-indexcss-cleanup)

Trinity replaced marketing-template index.css with a minimal reset (box-sizing, margin/padding reset, #root min-height, body bg fallback #f5f5f5). All theming now owned by Fluent. Updated stale AgentMessageBubble code-style comment. The foreign stylesheet's global `code { color: var(--text-h) }` + dark-mode block caused the invisible gray-box code rendering bug; removing it eliminates the cascade conflict and dead tokens.

### 2026-06-08: User directive — MAF-native HITL

Ahmed Sabbour requested human-in-the-loop review gate implementation using Microsoft Agent Framework workflow HITL (RequestInfoExecutor / RequestPort + checkpointing), not hand-rolled RunStatus.AwaitingReview state machine. Also: skip review entirely when agent produced no changes (empty diff), and align review-section UI (DiffViewer + ReviewPanel) styling with timeline lifecycle cards. Implementation target: direct cutover, no feature flag.

### 2026-06-09: MAF HITL direct cutover, no feature flag (copilot-maf-hitl-no-flag)

Implement the MAF Workflow-native HITL rewrite (design-maf-hitl.md) as a DIRECT CUTOVER with NO feature flag. Skip dual-path migration (design Phases 2-3). Build the workflow path as the only path and remove the hand-rolled RunOrchestrator state machine in the same change. User directive. Eliminates dual-path restart-recovery conflict (rubber-duck finding #6) but removes the rollback switch, so the full build + test suite must stay green through the change. Rubber-duck = SOUND WITH CONCERNS; Seraph = YELLOW (zero blockers). Top API risk R1 (conditional edge) verified resolved: WorkflowBuilder.AddEdge<T>(src, tgt, Func<T,bool>) exists natively in 1.9.0.

### 2026-06-09: MAF HITL post-impl gate outcome + remediation (copilot-maf-hitl-postimpl-gate)

Rubber-duck post-impl review SOUND WITH CONCERNS; Seraph post-impl YELLOW (safe to commit, 0 blocking). Blocking rubber-duck findings fixed: (1) review->merge type routing via adapter executors + state store (MAF 1.9.0 delivers exact edge-message type to executor handler; inserted FunctionExecutor adapters + workflow state to bridge types). (2) Zero integration tests on MAF workflow path — added 4 integration tests covering happy-path approve, decline, no-changes skip, and content-safety immediate-fail. All 4 pass green; workflow path proven. Non-blocking remediation: worktree cleanup for terminal paths (no-changes + content-safety), worktree validation in direct fallback, RequestInfoEvent double-processing guard, merge-conflict sanitization in logs. ExecuteDirectReviewAsync fallback kept (shares IMergeCoordinator/IWorktreeOperations + CAS, logic cannot diverge). Build 0/0; backend 133/135 (2 pre-existing ModelSourceValidationTests); web 49/49.

### 2026-06-09: MAF HITL design — hybrid event bridging, FileSystem checkpoint, requestId resolution

MAF HITL design document published at specs/001-single-agent-run/design-maf-hitl.md. Key decisions: (1) Hybrid event bridging — agent token stream stays on existing RunStreamEntry/RecordingChannelWriter side-channel; MAF workflow owns lifecycle, HITL gate, checkpointing. Avoids checkpoint bloat, zero added latency on high-frequency delta stream. (2) FileSystemJsonCheckpointStore — built-in zero-implementation-cost durability; SQLite remains queryable source of truth. (3) Review endpoint server-resolves requestId from PendingRequestStore (keyed by runId). (4) No-changes skip — conditional edge on empty diff bypasses RequestPort, transitions to Completed with result "no_changes".

### 2026-06-09: Security & RAI pre-implementation review — MAF HITL (seraph-maf-hitl-design)

Seraph pre-impl YELLOW (advisory only, zero RED blockers). Six yellow-level findings documented: (F1) checkpoint data-at-rest in plaintext JSON; (F2) abandoned checkpoint leak window on crash; (F3) content-safety gating implicit ordering assumption; (F4) IDOR risk in PendingRequestStore mitigated by ownership check (confirm implementation). (F5) replay/double-submit on review endpoint (use atomic TryRemove). (F6) request_id in SSE event (GREEN, no issue — GUID opaque). (F7) merge isolation preserved (GREEN). (F8) human-in-the-loop RAI gate preserved (GREEN). Verdict: YELLOW — proceed with recommendations. F1-F5 should be addressed in implementation before production ship. F1+F2: permission guards on checkpoint dir + GC sweep for abandoned checkpoints. F3: executor throw on safety violation, not downstream routing. F4: preserve ownership check with defense-in-depth. F5: atomic remove-before-resume pattern with 409 on duplicate.

### 2026-06-09: Security & RAI post-implementation review — MAF HITL (seraph-maf-hitl-impl)

Seraph post-impl YELLOW (zero RED blockers; safe to commit). Auth/IDOR PASS: IsOwner gate + atomic TryRemove + double-check in direct path. RAI gate PASS: workflow graph enforces ReviewDecision.Approved=true before merge edge; no-changes/decline paths terminal. Replay/double-merge PASS: layered CAS + registry + status guard + critical ordering (watch loop awaits terminal state before registry removal). Checkpoint at-rest YELLOW advisory (user-scoped AppData, pre-existing GC in place, but consider DPAPI encryption before multi-tenant). Logging YELLOW advisory (merge conflict reasons may leak repo structure; recommend sanitize/truncate libgit2sharp strings). Merge isolation/injection PASS. Verdict: Code safe to commit; address YELLOW items (checkpoint encryption, log sanitization) before multi-tenant deployment.

### 2026-06-09: Backend build fix — MAF HITL blockers (tank-maf-hitl-buildfix)

Tank fixed 6 build errors: (1) RunStatus name collision (WorkflowRestartService.cs) — added WfRunStatus alias for Microsoft.Agents.AI.Workflows.RunStatus; (2) 5x null-dereference CS8602 in RunWorkflowFactory edge predicates — added null guards. Also fixed 3 runtime issues: (3) RestartRecoveryAsync removed (ReviewEndpointHybridMergeTests.cs) — called WorkflowRestartService.RecoverAsync instead. (4) Checkpoint store exclusive lock on multiple test factories — made checkpoint path configurable via IConfiguration, per-test unique path + cleanup in Dispose. (5) Review endpoint returned 409 for all non-workflow runs — added ExecuteDirectReviewAsync direct fallback path sharing IMergeCoordinator/IWorktreeOperations + existing CAS/locks/auth (preserves all guardrails). Build 0/0; backend 133/135 (2 pre-existing ModelSourceValidationTests main-vs-master branch failures); web 49/49.

### 2026-06-09: MAF HITL verify — review-to-merge adapter + integration tests (morpheus-maf-hitl-verify)

Morpheus resolved post-impl rubber-duck blockers: (Issue 1) MAF 1.9.0 delivers exact edge-message type to executor handler; no automatic materialization. Review-to-merge type mismatch fixed via two FunctionExecutor adapter nodes + workflow state: review-adapter (AgentTurnOutput→WorkflowReviewRequest) stores full AgentTurnOutput in state; merge-adapter (WorkflowReviewDecision→MergeInput) reads stored output and constructs MergeInput. (Issue 2) Added 4 integration tests on real file-editing runner (TestFileEditAgentRunner) with 3 modes: happy-path approve→merged, decline→declined, no-changes→completed, content-safety→failed. All pass green. Also applied non-blocking fixes: Issue 5 worktree cleanup on terminal paths, Issue 3 worktree validation in fallback, Issue 4 double-process guard, Seraph#6 log sanitization. Build 0/0; backend 135/135 (new workflow tests + no-changes/content-safety assertions); web 49/49.

### 2026-06-09: Reference docs updated — MAF HITL events/endpoints (link-maf-hitl-docs)

Link updated user-facing reference docs (events.md, api.md) to reflect MAF Workflow-native HITL rewrite: corrected event payload fields (review.approved/declined now empty, run.failed uses `reason`, run.completed emits `result:"no_changes"`, review.requested adds `request_id`). Rewrote review endpoint section documenting primary async "merging" response and idempotent re-POST behavior. Fixed `completed` status description.

### 2026-06-08: Align review panel border with card language (trinity-review-border-align)

Trinity adjusted ReviewPanel root border from colorNeutralStroke1 to colorNeutralStroke2 to match timeline/tool-call card border weight. Prominence preserved via primary Approve button, semibold heading, and section Divider+Badge. Aligns review section visual consistency with timeline cards.
# Squad Decisions

## Active Decisions

### 2026-06-12T10:52:58Z: Feature 005 Phase 0–2 and Phase 6 implemented (Agent Team Casting)
**By:** Morpheus, Tank, Trinity, Smith (coordinated by Coordinator)
**What:** The Agent Team Casting feature is implemented across four phases. Scaffolder.Squad (Phase 0) is a standalone class library containing the domain model, embedded catalog (9 groupings, 31 roles), CatalogReader, SquadReader/Writer, CharterCompiler, and UniverseAllocator with 14 named pools. Phases 1+2 deliver the backend service layer (CastProposalStore, CastingService, CastingMappings), 12 API endpoints, all casting/team DTOs, and full CLI parity via TeamCommands.cs. Phase 6 delivers TeamPage, CastingWizardPage, and SyncPanel in the React/Fluent web UI with 14 new API client methods and TypeScript types. Tests cover SC-001–SC-007 with CastingWebApplicationFactory and SquadTestFixtureHelper.
**Why:** Feature 005 spec (FR-001 through FR-034) fully implemented for Squad library, API, CLI, and Web tiers.
**Build results:** 418 .NET tests passed, 14 skipped (expected), 0 failed; 112 Vitest tests passing; 0 build errors; 0 build warnings.

### 2026-06-12T10:52:58Z: MergeSafeStateTests use real git binary for merge=union tests
**By:** Coordinator
**What:** `MergeSafeStateTests` that exercise `.gitattributes` `merge=union` driver behaviour are run against the real `git` CLI rather than LibGit2Sharp.
**Why:** LibGit2Sharp does not honour `.gitattributes` custom merge driver declarations. The real git binary respects them correctly, so tests that rely on union merge behaviour must shell out to git. This is the authoritative pattern for any test that depends on `.gitattributes` merge drivers.
**Spec artifacts affected:** `tests/Scaffolder.Tests/` — `MergeSafeStateTests.cs` revised.

### 2026-06-12T10:52:58Z: Scaffolder.Squad project reference added to Api and Tests; Squad-dependent tests activated
**By:** Tank (wiring), Smith (test activation)
**What:** `Scaffolder.Api.csproj` and `Scaffolder.Tests.csproj` both carry a `<ProjectReference>` to `packages/Scaffolder.Squad/Scaffolder.Squad.csproj`. Initial test scaffolding used `#if SQUAD_AVAILABLE` guards; guards were removed and API mismatches fixed once the reference was live.
**Why:** Scaffolder.Squad is the canonical domain library for squad catalog and team composition; it must be a compile-time dependency of both the API and the test project to enable typing and IntelliSense across the solution.

### 2026-06-12T10:52:58Z: 14 universe name pools defined in UniversePools.cs
**By:** Morpheus
**What:** `UniversePools.cs` in `Scaffolder.Squad` defines 14 thematic name pools used by `UniverseAllocator` to assign deterministic, human-readable universe names to each cast squad composition.
**Why:** Universe names give each team cast a memorable, collision-resistant identity without relying on GUIDs. The 14 pools provide enough variety for the expected project volume while keeping names pronounceable.

### 2026-06-11T12:57:22-07:00: FR-005 refined — unified GitHub sign-in replaces Copilot API key (spec 003-projects)
**By:** Seraph (Security Reviewer), coordinated via Copilot CLI
**What:** A single "Sign in with GitHub" (OAuth device flow) grants both (a) repository access including cloning private repos and (b) authorization to use the GitHub Copilot provider, used IN PLACE OF a separately entered API key. Microsoft Foundry continues to use its own credentials (GitHub login cannot authorize Azure Foundry — distinction preserved deliberately). Provider credentials are stored global/installation-wide, never on the project record, never in logs/telemetry (Principle IX). SC-010 added: zero prompts for a Copilot-specific key when a valid GitHub sign-in exists.
**Why:** User decision — "Allow logging in with GitHub and getting access to repos and ability to use GitHub Copilot. Use that instead of the key."
**Spec artifacts affected:** specs/003-projects/spec.md (FR-005 broadened; FR-013, FR-016 augmented; Clarifications L23, US3, US4, Key Entities updated; SC-010 appended). specs/003-projects/checklists/requirements.md (re-validated).
**Planning notes carried forward (Seraph):** (1) OAuth scope minimization; (2) secure token storage for the global/installation-wide credential store; (3) token refresh/revocation + clean sign-out.

**ADDENDUM — 2026-06-11T13:05:16Z (Opus 4.8 Max-Rigor Re-Run)**
Seraph re-ran the FR-005 refinement on Claude Opus 4.8 (model-upgrade re-run per user request, not a reviewer rejection). All prior decisions affirmed; 9 surgical spec edits applied to FR-005, FR-013, FR-016, Clarifications, US3, US4, Key Entities, and SC-010 (hardened no-key rule, explicit Foundry boundary, CLI+Web parity, least-privilege scoping, broadened no-leak guarantee). Constitution-aligned (Principles II, III, IV, IX, X). Single [NEEDS CLARIFICATION] marker remains (FR-025 cloud storage, deferred to /speckit.plan).

**Expanded Planning-Time Security Notes (8 Items for Queued Phase)**
1. **OAuth scope minimization** — request only scopes for clone-incl-private + Copilot; enumerate exact scopes in the plan.
2. **Secure token storage** — OS keychain/credential manager locally; encrypted server-side secret store in hosted-cloud; never plaintext; never on the project record.
3. **Token lifecycle** — refresh, mid-run expiry handling, revocation detection, clean sign-out that purges cached tokens and fails subsequent Copilot/clone use.
4. **GitHub auth ≠ Copilot entitlement** — a valid sign-in does not guarantee an active Copilot seat; define how missing entitlement surfaces as a clear, non-leaking error distinct from auth failure.
5. **Hosted-cloud multi-tenant reconciliation** — "installation-wide, shared across all projects" assumes a single accountable human; a shared cloud deployment must reconcile with per-user/per-tenant credential isolation; couples to FR-025 (deferred, no marker).
6. **Centralized token redaction (Principle XI)** — enforce device-code/access/refresh-token redaction in the governance/telemetry layer across step stream, audit log, and telemetry; test asserting zero secret occurrences (backs SC-006/SC-010).
7. **Clone transport hygiene** — pass tokens via a git credential helper/ephemeral askpass; never embed tokens in remote URLs (avoids leaks into logs, process listings, cloned .git/config).
8. **Foundry credential separation** — distinct secure entry/storage path; ensure no code path lets a GitHub token satisfy a Foundry call (enforces FR-013/FR-016 boundary).

### 2026-06-11T10:53:00-07:00: Clarifications resolved for spec 003-projects (5 of 6)
**By:** Ahmed Sabbour (via Squad-facilitated /speckit.clarify)
**Decisions:**
- FR-005 (GitHub auth for private clones): app-initiated OAuth device flow; tokens handled so secrets never leak (Principle IX).
- FR-016 (provider credential storage): global / installation-wide, shared across all projects (not per-project).
- FR-006 (project working directory): user picks the directory per project at creation; no managed workspace root; the chosen directory is the run sandbox boundary (Principle X).
- FR-003 (blank project init): empty directory initialized with git init.
- FR-019 (delete scope): record-only delete; on-disk working directory and clones always preserved (non-destructive).
**Deferred:** FR-025 (cloud project-storage location) -> to be resolved during /speckit.plan (Principle VI Deployment Parity).
**Why:** Reduce ambiguity before planning.

### 2026-06-11T13:24:22-07:00: Clarify round 2 (003-projects) — 4 spec ambiguities resolved on Opus 4.8

**By:** Coordinator (with Squad agents: clarify-scan, clarify-encode; user answers: Session 2026-06-11)

**Decisions Integrated:**
- **FR-019** (delete with in-flight runs): deletion MUST require explicit confirmation; in-flight runs MUST first be cancelled to a visible terminal state, THEN the record is removed; working-directory files always preserved (record-only delete; Principle X).
- **FR-003 & FR-004** (chosen dir exists, non-empty): both blank and from-GitHub creation require an empty or non-existent working directory; a non-empty existing dir MUST be rejected with a clear reason and never overwritten or adopted (importing a pre-existing folder stays out of scope).
- **FR-026** (NEW): if a recorded working directory is missing/inaccessible at list/open, the project MUST still be listed but marked unavailable, runs MUST be blocked (sandbox boundary FR-022 invalid), and the user MUST be offered relink-to-new-dir or remove-record; existing files preserved.
- **FR-024** (accountable owner source): owner MUST be the GitHub-signed-in user when present (FR-005), else the local OS/installation user identity; recorded for accountability/audit (Principles IX, X).

**Spec Artifacts Updated:** specs/003-projects/spec.md (FR-019, FR-024, FR-026 added/refined; FR-003, FR-004 clarified). specs/003-projects/checklists/requirements.md re-validated (15/16 checkmarks).

**Deferred:** FR-025 (hosted-cloud project-storage location) — deferred to /speckit.plan; remains only [NEEDS CLARIFICATION] marker.

**Why:** Coordinator facilitated 4 clarification questions with the user (not via sub-agents; session 2026-06-11 round 2). Clarify-scan returned 4 prioritized spec-level questions; clarify-encode integrated answers on Opus 4.8 and re-validated checklist.

### 2026-06-11T10:47:03-07:00: Spec 003-projects initiated (Projects feature)

**By:** Ahmed Sabbour (via Squad → speckit.specify)

**What:** Created branch 003-projects and specs/003-projects/spec.md for a Projects capability: projects created blank or cloned from a GitHub repo into a local directory, with per-project AI provider settings (default model per provider) and a card-based landing page. 25 FRs, 6 user stories.

**Constitution alignment:** Provider settings constrained to the two permitted providers (GitHub Copilot CLI, Microsoft Foundry) per Principle II; API-first (III); CLI+Web parity (IV); project working directory = run sandbox boundary (X); clone-credential privacy (IX); local+cloud parity (VI, deferred to clarification).

**Open:** 6 [NEEDS CLARIFICATION] markers deferred to /speckit.clarify: blank-project init, GitHub auth for private clones, local workspace root dir, per-project vs global credential storage, delete scope, cloud storage location.

**Why:** User request to add Projects as a new spec.

## Archived Decisions

See: .squad/decisions-archive.md for decisions dated 2026-06-07 and earlier.


## Merged Inbox Decisions — Consolidated Archive

See .squad/decisions-archive.md for consolidated merged inbox decisions from 2026-06-08 batch and earlier, including:
- Governance and working agreements (Spec-Kit workflow, root-cause fixes, documentation standards, team cast, implementation gates)
- Sandbox governance and provider-native tool containment (threat model, AGT .NET, Copilot enforcement, MXC evaluation)
- Copilot provider tool-event parity and streaming documentation
- Story 4, 5, 6, 7, 8, 9 review/merge lifecycle and related decisions
- All other pre-2026-06-11 merged inbox entries

- Story 4 backend wired WorktreeManager into RunOrchestrator, added AwaitingReview/Merged/Declined/MergeFailed states, persisted worktree metadata/diff/step count, added `POST /api/runs/{id}/review`, and kept stream entries alive through review/merge. Builds passed.
- Hybrid working-tree-aware merge replaced binary success/conflict with Merged/Blocked/Conflict. Blocked is retriable and reverts Merging→AwaitingReview; Conflict is terminal MergeFailed; per-repository merge locks and CAS Merging state protect concurrent approvals.
- Web and CLI now treat 409 review responses as retriable, keep approve/decline enabled, and handle terminal `merge_failed` plus transient `merging` safely.
- Smith added Story 4 tests covering approve/decline, ownership, idempotency, stream events, diff metadata, hybrid merge behavior, CAS state changes, restart recovery, and safe blocked/conflict messages. New tests passed; two ModelSourceValidation failures were pre-existing.
- Seraph Story 4 design review was YELLOW advisory. Must-fix implementation obligations were folded in: owner check, safe conflict strings, operational log for review decisions, content-safety assumptions for served diffs, and terminal handling on tree-hash mismatch.
- Source inbox files: tank-story4-*.md, smith-story4-*.md, trinity-story4-retriable.md, seraph-story4-design.md.

### Run timeline UI redesign and Markdown rendering

- Trinity replaced the flat RunWatcher event list with reducer-based turn grouping, Timeline/TurnGroup/TurnDivider components, AgentMessageBubble, collapsible ToolCallCard, lifecycle cards, RunHeader, and reconnect reset handling. 36 web tests passed with build and lint clean.
- Reducer decisions: tool.call/result pairing by callId, O(1) incremental folding, monotonic turnCounter, synthetic turns when events arrive without turn.start, capped args/results at 50,000 chars, worktree/home prefix stripping for card headers, sandbox violations highlighted and expanded by default.
- Safe Markdown rendering was added only for settled agent message bubbles using react-markdown, remark-gfm, and rehype-sanitize with no rehype-raw and no dangerouslySetInnerHTML. Streaming/live partial text remains plain escaped text. Tool args/results/errors and diffs remain escaped monospace text.
- Seraph cleared the Markdown/XSS review GREEN; Y-3 is resolved. Tests prove script tags and event-handler attributes are inert. Exact dependency pins were recorded.
- Final reducer fix settles an orphaned streaming bubble when a new messageId starts without an intervening agent.message; T-11 covers it. web.md now documents the new timeline, live-vs-replay cursor behavior, sanitized Markdown, tool cards, lifecycle cards, and unchanged review gate.
- Source inbox files: trinity-run-watch-*.md, Trinity-implemented-run-timeline-*.md, Trinity-add-safe-markdown-*.md, Trinity-timeline-finalize-*.md, coordinator-markdown-agent-messages.md, seraph-timeline-design.md, seraph-markdown-postimpl.md.

### Foundry token streaming

- Tank replaced FoundryAgentRunner `GetResponseAsync` with `GetStreamingResponseAsync`, emits `agent.message.delta` with `{delta, messageId}` per text chunk, accumulates ChatResponseUpdate values, and reconstructs with `ToChatResponse()` before the existing tool-governance loop. The governance invariant is preserved: streaming updates never invoke tools inline.
- Foundry emits `agent.message` only as a fallback for turns with no token deltas. Both Copilot and Foundry now stream normal text as `agent.message.delta`; docs were updated in events.md, cli.md, and getting-started.md.
- Ten Foundry streaming tests passed; total suite reported 127 passed with two pre-existing ModelSourceValidation failures. Seraph pre- and post-implementation reviews were GREEN; rubber-duck was SOUND.
- Source inbox files: Tank-foundry-runner-*.md, tank-foundry-streaming-impl.md, Tank-accuracy-pass-*.md, seraph-foundry-streaming.md, seraph-foundry-postimpl.md.

### Timeline polish: Foundry turn grouping, empty-turn suppression, code-span legibility, and spinner settlement

- Tank moved Foundry `agent.turn.end` emission after tool-call emission so `tool.call`, `tool.result`, and `tool.error` events stay inside their originating turn. `run.completed` now emits an empty payload, avoiding duplicate final-message rendering; events.md was updated.
- Trinity added a defensive TurnGroup guard that hides closed zero-step turns while preserving active zero-step turns, so transient live turns still render but completed empty dividers do not.
- Trinity fixed inline code-span legibility by overriding the foreign global `index.css` code rule with explicit Fluent foreground color and inline display, including nested `pre code` color.
- Trinity added reducer `closeOpenTurn` handling for `run.completed`, `run.failed`, `merge.completed`, and `merge.failed`, settling any streaming bubble and closing synthetic Copilot turns when no `agent.turn.end` arrives; this stops replay/completed spinners.
- Rubberduck pre/post reviews were SOUND or SOUND-WITH-FIXES, with all fix fold-ins applied. Seraph pre/post reviews were GREEN; sanitizer behavior and governance gates were unchanged.
- Validation reported: Tank 129 tests passing; Trinity empty-turn pass 46 web tests; Trinity codebox/spinner pass 48 web tests.
- Follow-up: `apps/web/src/index.css` is still a foreign marketing-template stylesheet leaking global styles; replace with a Fluent-appropriate reset in a future task.
- Source inbox files: Tank-fix-foundry-timeline-group-tool-events-inside-thei.md, Trinity-defensive-guard-suppress-rendering-of-closed-empty.md, trinity-codebox-spinner.md.

### 2026-06-08: Three timeline UI polish fixes (accordion, background, heading ramp)

Trinity applied three visual refinements: (1) ToolCallCard accordion switched from controlled `openItems` to uncontrolled `defaultOpenItems`, fixing the freeze where rows did not expand/collapse on user click; (2) LifecycleEventCard background aligned from `colorNeutralBackground2` to `Background1` for visual consistency with ToolCallCard; (3) AgentMessageBubble Markdown headings given explicit Fluent type-ramp sizing and weight (h1 base500, h2 base400, h3 base300 bold, h4–6 base300) after the index.css cleanup exposed browser defaults. Rubberduck confirmed all three diagnoses with token breakdown; Seraph issued GREEN verdict. Build clean, 48/48 tests passing.

### 2026-06-08: ToolCallCard accordion regression test + web.md accuracy pass

Trinity added regression test C-07 to lock in the accordion toggle fix: test asserts that `aria-expanded` on the AccordionHeader transitions false → true → false on repeated user clicks, preventing reintroduction of the controlled-without-onToggle freeze bug. Updated web.md Markdown rendering paragraph with one-line note on Fluent type-ramp heading sizes (h1 → Base500, h2 → Base400, h3–6 → Base300). Build clean, 49/49 tests passing. Rubberduck post-review SOUND, Seraph GREEN.

### Security review status carried forward

- Post-implementation review for the original 001-single-agent-run slice was GREEN for the initial critical/high findings (S1, S7, S9, S2, S4, S5, S8, S10), with advisories for write-path leaf-symlink truncation TOCTOU, raw exception reasons, and limited credential pattern coverage.
- A later fresh Seraph ceremony produced YELLOW for hosted/cloud readiness: unscoped RepositoryPath, raw diff output without deep secret/PII scrubbing, weak provider-specific content-safety, missing global rate/concurrency limits, non-constant-time key lookup, and latent concurrent sequence allocation. These are carried as future hosted-release risks, not blockers for the local slice.
- Source inbox files: seraph-ceremony-001.md, seraph-post-001.md, seraph-post-implementation-security-review-*.md, seraph-pre-implementation-security-review-*.md, seraph-security-review-of-dual-provider-runtime-*.md.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction

### 2026-06-09: Merge Consolidation pre-implementation security review (seraph-merge-consolidation-preimpl)

Plan to consolidate duplicated merge logic into IMergeCoordinator.ExecuteMergeAsync is safe to implement. Three advisory items: (1) ensure SanitizeReason is applied to all logged reason strings in the shared method (existing gap at Program.cs:477); (2) sanitize reason strings in HTTP responses to avoid leaking git stderr/paths; (3) preserve the non-test-environment WARNING log in the caller, not the shared method.

### 2026-06-09: Merge Consolidation post-implementation security review (seraph-merge-consolidation-postimpl)

GREEN, no blockers, no advisories. Lock/CAS/authZ/path-safety/log-hygiene all intact. Safe to commit.

### 2026-06-09: Merge Consolidation — Unified Executor (morpheus-merge-consolidation)

Consolidated duplicate merge orchestration into MergeCoordinator.ExecuteMergeAsync; MergeExecutor and ExecuteDirectReviewAsync are now thin callers. Wire contract unchanged. Build 0 errors/0 warnings, tests 133 passed / 2 pre-existing failures. Committed c9374ae.


### 2026-06-09: Fresh MAF Workflow per Start/Resume (workflow-per-run)

MAF `Workflow` instances are single-use: a workflow takes exclusive ownership on first execution and cannot be reused by another runner. `RunWorkflowFactory` must build a fresh `Workflow` for every `StartAsync` call and every `ResumeAsync` call; it must never cache and reuse a workflow instance.

This fixes the second-run ownership failure in a long-lived API process and the latent recovery bug where a second resume in the same process could have failed. Resume is safe with a fresh workflow because MAF binds checkpoint recovery by deterministic topology/executor IDs, not by object instance identity. `Workflow` does not implement `IDisposable`, so no disposal path is required.

Validation: build clean with 0 warnings; workflow integration tests 5/5; total suite 134/136 with two pre-existing unrelated failures. Coordinator live verification submitted two runs in one API process; both returned 202 and completed without the prior ownership exception.

Deferred follow-ups: harden `FileSystemJsonCheckpointStore` for concurrent writes as a reliability improvement; investigate best-effort worktree cleanup warnings where LibGit2Sharp cannot delete a branch that is current HEAD of a linked repository.


### 2026-06-09T19:04:00-07:00: Root-cause debugging directive (copilot-directive-2026-06-09T19-04)

**By:** Ahmed Sabbour (via Copilot)

Never fix symptoms only. Debugging and bug fixes must identify the root cause and fix the issue at the source; surface-only patches are not acceptable.

### 2026-06-09: Issue 3 — Sandbox root `.` and Foundry `list_directory` (morpheus-issue3-sandbox)

**Owner:** Morpheus  
**Status:** Implemented

Fixed sandbox handling for paths resolving exactly to the sandbox root (`.` / `./`) by using OR-equality containment checks in `SandboxPathValidator.ValidateAndResolve` and `VerifyOpenedHandle`. Added Foundry `list_directory` support, including prompt/tool registration, non-recursive sandboxed directory listing, directory-read hinting for `read_file`, reparse-root rejection, structured `SandboxEntryKind` / `SandboxDirectoryEntry` results, and documentation updates in `docs/architecture/sandbox.md`.

**Review follow-ups merged:** Added XML remarks documenting the residual `ListDirectoryAsync` TOCTOU model and wired `CancellationToken` support before and during enumeration, including a cancellation regression test.

**Verification:** Full no-incremental rebuild reported 0 warnings / 0 errors. Sandbox tests passed 91/91 after follow-ups. Existing unrelated `ModelSourceValidationTests` failures remained unchanged.

### 2026-06-09: Issue 6 — Worktree branch-delete HEAD warning fix (tank-issue6-worktree)

**Owner:** Tank  
**Status:** Implemented

Fixed `WorktreeManager.RemoveWorktree` so branch deletion no longer warns that the branch is the current HEAD of a linked repository. Root cause was stale linked-worktree state from using one `Repository` handle for pruning and branch deletion. The teardown now deletes the physical worktree directory first, prunes the stale admin entry in its own repository scope, then removes the branch using a fresh repository handle. Added `ILogger<WorktreeManager>` constructor injection.

**Verification:** Added HM-12 `RemoveWorktree_DeletesBranchWithoutHeadWarning` covering create/merge/remove. Release no-incremental build was green, and HybridMerge tests passed 12/12. Existing unrelated `ModelSourceValidationTests` failures remained unchanged.


### 2026-06-09: Batch 2 run bug fixes — suppress internal events, merge lifecycle, blocked retry (batch2-run-bug-fixes)

Batch 2 implemented four scaffolders run bug fixes on branch `001-single-agent-run`.

- Morpheus fixed Issues 1 & 2: `GitHubCopilotAgentRunner` now suppresses SDK-internal `report_intent` and `glob` tool lifecycle events through a static allowlist with per-run suppressed-call tracking and a run-end suppressed counter; runner-level `run.completed` emission was removed in favor of `agent.turn.end` (`turnId: "0"`) while the watch loop remains the sole `run.completed` emitter. Foundry runner-level `run.completed` was also deleted. Verification: FoundryStreamingTests 12/12; post-implementation rubber-duck SOUND and Seraph GREEN.
- Tank fixed Issue 5 backend: added `merge.started`, `EventTypes.MergeStarted`, and LIVE/DIRECT API emission before merge execution with payload `{ tree_hash }` only. Paths and branch names are intentionally excluded per Seraph guidance.
- Tank fixed Issue 4: `WorktreeManager` now compares checked-out HEAD with the originating branch using `OrdinalIgnoreCase` on Windows and `Ordinal` elsewhere; blocked merges now map to `blocked`, loop back through the MAF HITL review gate, and do not trigger registry/checkpoint teardown if leaked to terminal-output handling. Tank-3 addressed post-review findings by proving HM-13 fail-without/pass-with for `HEAD=Main` vs `origin=main` and making `HandleTerminalOutputAsync` return `bool` so non-terminal blocked output keeps the watch loop alive. Verification: build 0/0; related tests green; full suite 161/163 with two environment-only `ModelSourceValidationTests` failures.
- Trinity fixed Issue 5 frontend: `RunWatcher` derives effective review completion from SSE `merge.completed` / `merge.failed`, `merge_failed` now shows a red badge with retry UI, `sse.ts` recognizes `merge.started` and adds a `seq=0` singleton dedup guard, and `docs/reference/web.md` documents live merge resolution. Verification: build, lint, and 49 web tests green; post-implementation rubber-duck SOUND and Seraph GREEN.

Source inbox files: morpheus-issues-1-2.md, tank-issues-4-5.md, tank-issue4-postreview.md, trinity-issue5-frontend.md.

### 2026-06-10T06-01-04: Map caller-input errors in AddWorktree to 400 via RunSubmissionValidationException; keep test fixture on the 400 path (no branch fix)
**By:** Tank
**What:** Map caller-input errors in AddWorktree to 400 via RunSubmissionValidationException; keep test fixture on the 400 path (no branch fix)
**References:** apps/Scaffolder.Api/Runs/RunSubmissionValidationException.cs, apps/Scaffolder.Api/Git/WorktreeManager.cs, apps/Scaffolder.Api/Program.cs, tests/Scaffolder.Tests/ModelSourceValidationTests.cs, tests/Scaffolder.Tests/Helpers/ScaffolderWebApplicationFactory.cs
**Why:** Bug: POST /api/runs returned 500 for caller-input errors (bad repo path, non-existent branch). Fix: introduced RunSubmissionValidationException thrown by AddWorktree for RepositoryNotFoundException and branch-not-found; caught in Program.cs to return 400.

Test fixture decision: chose NOT to hardcode the real default branch in ModelSourceValidationTests. Reason: the WebApplicationFactory comment confirms "These tests never execute a run, so the values are never used to make a real model call." There is no stub for the agent runtime workflow — a valid branch would invoke RunWorkflowFactory.StartAsync which attempts real HTTP calls to the configured (fake) provider endpoints. This would make the tests network-dependent and non-deterministic. The core fix alone (400 with a non-model_source error) satisfies the test assertions deterministically on all hosts regardless of git init.defaultBranch.

### 2026-06-09T22:51:30-07:00: Run submission caller-input validation maps to HTTP 400
**By:** Scribe
**What:** POST /api/runs now returns 400 for caller-input failures surfaced while creating the worktree: repository-not-found and branch-not-found are translated by WorktreeManager.AddWorktree into RunSubmissionValidationException, and Program.cs handles that exception before the generic 500 path.
**References:** apps/Scaffolder.Api/Runs/RunSubmissionValidationException.cs, apps/Scaffolder.Api/Git/WorktreeManager.cs, apps/Scaffolder.Api/Program.cs, apps/Scaffolder.Api/API.md, docs/reference/api.md
**Why:** The root cause was infrastructure exceptions from invalid caller inputs escaping as internal failures. The typed exception preserves the boundary: invalid run submissions get deterministic 400 responses without echoing repository paths, while GetDiff and MergeWorktree failures intentionally remain 500 because they represent server-side lifecycle/invariant failures after run acceptance. Seraph's A1 truncation advisory was folded in for branch names; A2 repo-path traversal hardening is logged as a deferred hardening follow-up because it is pre-existing and out of scope for this bug fix.

### 2026-06-09: A2 Repository Path Allowlist Implementation (tank-a2-repo-allowlist)
**By:** Tank
**What:** Closed the A2 vulnerability where `POST /api/runs` flowed unsanitized `repository_path` directly to `new Repository(repositoryPath)`. Added `RepositoryRootValidator`, shared `Scaffolder.SandboxFs.RealPath.Resolve`, DI/handler wiring, CLI `Path.GetFullPath` normalization, `Runs:AllowedRepositoryRoots`, documentation, and 22 validator tests.
**Why:** Run submissions must canonicalize repository paths, reject UNC/device/ADS forms, resolve symlink ancestors for containment, avoid path-existence oracles, and remain permissive when no allowlist is configured.
**Verification:** Initial implementation built SandboxFs/API with 0 warnings/0 errors and passed 185 tests; post-review found the Unix directory-open issue addressed by `tank-a2-resolveunix-fix`.

### 2026-06-09: ResolveUnix realpath(3) directory-safe fix (tank-a2-resolveunix-fix)
**By:** Tank
**What:** Replaced `RealPath.ResolveUnix` directory-opening logic with libc `realpath(3)` P/Invoke, simplified dead-code guards in `RejectUncAndDevicePaths`, and added `RealPathTests` covering directory resolution.
**Why:** The first Unix implementation used `File.Open` on repository directories, which throws on Linux/macOS and would reject every run submission off Windows. `realpath(3)` mirrors the Windows final-path handle approach for directories, files, and symlinks.
**Verification:** Coordinator independently verified full no-incremental rebuilds for SandboxFs and API at 0 warnings/0 errors, plus `dotnet test` with 188/188 passing. Committed as `2802fe6` (`fix(security): validate and canonicalize repository_path on run submission`).


### 2026-06-10: Sandboxed Execution Implementation Plan Architecture (morpheus-sandbox-plan)

**Author**: Morpheus (Runtime/Architecture)
**Status**: Proposed

Authored the implementation plan for mxc-based sandboxed execution (spec 002). Key decisions:

1. Introduce `ISandboxExecutor` in new `Scaffolder.SandboxExec` package to decouple runners from `Sabbour.Mxc.Sdk` v0.1.1. Implementations: Windows mxc processcontainer, WSL2/lxc, and fail-closed passthrough fallback.
2. Preserve defense-in-depth: mxc augments but never replaces in-proc path containment and AGT deny-by-default governance.
3. Require a triple gate for shell: governance allow, real isolation, and configuration enablement.
4. Replace categorical `deny-shell` with sandbox-aware governance.
5. Emit sandbox selection as a run event for observability.
6. Start with a Phase 0 spike before production wiring.

References: `specs/002-sandboxed-execution/spec.md`, `specs/002-sandboxed-execution/plan.md`, `.specify/memory/constitution.md`, exploratory session plan d3f30867.

### 2026-06-10: Security Review of 002 Sandboxed Execution Plan (seraph-sandbox-plan-review)

**Reviewer**: Seraph (Security Reviewer)
**Gate**: T039 — Pre-Implementation Security Review
**Verdict**: YELLOW — proceed after required fixes before implementation.

Seraph found the defense-in-depth approach architecturally sound, but required plan fixes before implementation:

- F1 / C1 (HIGH): approving native Copilot shell would route execution through the SDK host shell; plan must deny native shell and provide an explicit mxc execution path.
- F2 (HIGH): filesystem policy canonicalization must use the full `SandboxPathValidator` reparse-safe chain, not bare `Path.GetFullPath`.
- F3: restrict and audit `MXC_BIN_DIR`; require absolute paths and integrity/trust checks for binaries.
- F4: document that filesystem policy is the runtime shell path barrier and broaden denied paths.
- F5: emit a warning when Windows native execution allows unrestricted network access.
- F6: define HITL behavior for destructive shell commands.
- F7: treat stdout/stderr as sensitive and avoid Info-level raw logging.
- F8: keep WSL command content inside the base64 JSON config blob and validate the blob.

Positive findings: fail-closed passthrough, conjunctive triple gate, immutable platform selection, bounded output, audit events, and spike-first validation. Seraph must re-review after implementation at T040.

### 2026-06-10: Sandboxed Execution Plan Revision (morpheus-sandbox-plan-rev)

**Author**: Morpheus (Runtime/Architecture Lead)
**Artifact**: `specs/002-sandboxed-execution/plan.md`
**Trigger**: Architecture rubber-duck + Seraph security review findings.

Morpheus revised the sandboxed execution implementation plan to resolve the review findings. Coordinator spot-check confirmed C1, C2, F2, and M1 are grounded in real code seams; no extra re-review is required before implementation because Seraph's YELLOW verdict was conditioned on F1 and F2, both resolved in the plan.

Key revisions:

1. Native `PermissionRequestShell` is always denied. Shell execution uses a custom `run_command` AIFunction registered through `SessionConfig.Tools` / `BuildTools`, with `is_override=true`, routing to `ISandboxExecutor.StreamAsync`.
2. `SandboxPolicyBackend` adds `KnownShellTools` with `directory` key extraction; `run_command` injects `directory` for containment validation.
3. Add `LinuxNativeMxcSandboxExecutor`; factory probe order is Windows-native → WSL2 → Linux-native → Passthrough.
4. Defer file-tool routing through mxc; in-proc handle-level TOCTOU verification remains for file operations.
5. Add full `SandboxPathValidator` canonicalization for filesystem policy paths.
6. Add binary integrity checks, command validation, destructive-command HITL gate, output redaction, WSL injection prevention, and network warning events.
7. Plan now has 6 phases and 48 tasks per coordinator manifest.

References: `specs/002-sandboxed-execution/plan.md`, `specs/002-sandboxed-execution/spec.md`, `.specify/memory/constitution.md`, `specs/001-single-agent-run/spike-copilot-sandbox.md`, `packages/Scaffolder.AgentRuntime/GitHubCopilotAgentRunner.cs`, `packages/Scaffolder.SandboxFs/SandboxPolicyBackend.cs`, `packages/Scaffolder.SandboxFs/SandboxedFileTools.cs`.


### 2026-06-10: Built-In Sandboxed Tools for Shell Minimization (morpheus-builtin-tools)

**Author**: Morpheus (Runtime/Architecture Lead)
**Status**: Proposed
**Artifact**: `specs/002-sandboxed-execution/plan.md`, §4.4, §4.7, Phase 4a T042-T054.

Morpheus added M4: reimplement Copilot CLI built-in file/search tools as sandboxed custom AIFunctions in both runners: `read_file`, `grep_search`, `file_search`, `str_replace_editor`, `apply_patch`, `create`, and `edit`. `semantic_search` is scoped out because self-hosted deployments do not have a local embeddings API. This positively supersedes M2: file operations no longer route through shell strings, so in-process handle-level TOCTOU validation is preserved. Native equivalents remain denied in the permission handler as defense-in-depth.

### 2026-06-10: Reusable Tool Library, Native Tool Exclusion, Memory and Planning Tools (morpheus-reusable-tools)

**Author**: Morpheus (Runtime/Architecture Lead)
**Status**: Proposed
**Artifact**: `specs/002-sandboxed-execution/plan.md`, §4.7.8, §4.8, Phase 4b T055-T072.

Morpheus added M5-M7: custom tools move into reusable runner-agnostic `Scaffolder.AgentTools` with `ISandboxTool`, `SandboxToolContext`, and `SandboxToolRegistry`; Copilot runner uses `SessionConfig.AvailableTools` as an explicit allowlist plus `ExcludedTools` and permission-deny as defense-in-depth; `store_memory`, `vote_memory`, `update_todo`, and `report_intent` are custom tools with real stores/events. `exit_plan_mode`, `task`, `notebook`, `web_*`, `sql`, and `semantic_search` remain out of scope.

### 2026-06-10: Security Review of Built-In Tool Layer Expansion (seraph-builtin-tools-review)

**Reviewer**: Seraph (Security Reviewer)
**Verdict**: YELLOW — proceed after plan-level fixes F-BT1 and F-BT2 are incorporated before Phase 4a implementation.
**Artifact**: `specs/002-sandboxed-execution/plan.md`, §4.7, §4.7.8, §4.8, Phase 4a/4b.

Seraph confirmed the allowlist is the authoritative server-side boundary, native exclusion is complete through safe-by-default allowlisting, scoped-out tools are excluded rather than stubbed, and prior findings F1-F8 remain honored. Required fixes: validate every `apply_patch` hunk path, including `Move to`, before any write; redact tool return values before they reach the model. Advisory follow-ups: memory recall redaction, `is_override` wording, and `semantic_search` wording cleanup.

### 2026-06-10: RAI Audit Summary for Built-In Tool Layer Expansion (seraph-builtin-tools-review-audit)

**Reviewer**: Seraph
**Verdict**: YELLOW, conditioned on F-BT1 and F-BT2.

Seraph recorded a redacted audit summary for the tool-layer expansion: no harmful content paths were introduced; privacy obligations remain maintained by extending the redaction pipeline to tool return values; prior findings F1-F8 have no regression. The full security review was merged as `seraph-builtin-tools-review`.

### 2026-06-10: Tool-Layer Design Review Resolution (morpheus-tools-rev2)

**Author**: Morpheus (Runtime/Architecture Lead)
**Artifact**: `specs/002-sandboxed-execution/plan.md`
**Trigger**: Architecture rubber-duck + Seraph security review findings.

Morpheus revised the plan to resolve all tool-layer findings. RBD1 uses delegate injection for `EvaluateToolCall`, keeping `AgentTools -> {SandboxFs, SandboxExec, Domain}` and `AgentRuntime -> AgentTools` acyclic. RBD2/F-BT4 sets `RunCommandTool.IsOverride=false`. RBD3/F-BT1 requires two-phase `apply_patch` validation of all target and `Move to` paths with zero writes on any failure. F-BT2/F-BT3 require every tool return value, including memory recall, to be redacted before reaching the LLM. RBD4 adds canonical-name tests; RBD5 adds T064a for CLI/Web `agent.intent`; RBD6 expands T049 across file, search, and internal backend branches; F-BT5 clarifies `semantic_search` is not registered.

### 2026-06-10: Squad Model Selection Policy (copilot-directive-2026-06-10)

**By:** Ahmed Sabbour (@sabbour)
**Directive:** Squad model-selection policy going forward:
- Development (implementation/coding): claude-sonnet-4.6
- When stuck or planning: claude-opus-4.8
- Security reviews, architecture reviews, rubber-duck: gpt-5.5

Supersedes prior "use opus-4.6 not 4.8" guidance.

### 2026-06-10: Defer Memory Tools to Future Increment (morpheus-defer-memory) [OVERRIDDEN]

**Author:** Morpheus (Runtime/Architecture Lead)
**Status:** OVERRIDDEN — see morpheus-remove-meta-tools and morpheus-restore-report-intent below.

Memory tools (`store_memory`, `vote_memory`, `SandboxMemoryStore`) deferred. Planning tools (`update_todo`, `report_intent`) retained. AvailableTools shrunk from 12 to 10. Tasks T058, T059, T065, T066 moved to Phase 6 (deferred). In-scope: 76 tasks, Deferred: 4.

### 2026-06-10: Remove Memory AND Planning/Todo Tools from Scope (morpheus-remove-meta-tools)

**Author:** Morpheus (Runtime/Architecture Lead)
**Requested by:** Ahmed Sabbour (@sabbour)

Memory tools (`store_memory`, `vote_memory`) AND planning/todo tools (`update_todo`, `report_intent`) removed entirely. Agent ships with shell + file + search only. AvailableTools: 8 tools (`run_command`, `read_file`, `grep_search`, `file_search`, `str_replace_editor`, `apply_patch`, `create`, `edit`). Phase 6 (Deferred) deleted. Tasks removed: T058, T059, T060, T064, T064a, T065, T066, T067. In-scope: 72 tasks. `KnownInternalTools` branch removed; three branches remain.

### 2026-06-10: Restore report_intent as UI Observability Tool (morpheus-restore-report-intent)

**Author:** Morpheus (Runtime/Architecture Lead)
**Requested by:** Ahmed Sabbour (@sabbour)

`report_intent` RESTORED to scope as a UI-only observability tool — emits `agent.intent` run event for run view. No filesystem or shell action; no governance branch needed. Return value still passes through `SandboxOutputRedactor`. NOT a memory or todo tool. AvailableTools restored to 9 tools. Tasks re-added: T064 (`ReportIntentTool` + unit test), T064a (render `agent.intent` in run UI). In-scope total: 74 tasks. `store_memory`, `vote_memory`, `update_todo` remain OUT OF SCOPE and excluded.


### 2026-06-10T08:42:53-07:00: User directive — Sandbox config must be dynamic and project-scoped
**By:** Ahmed Sabbour (@sabbour) (via Copilot)
**What:** Sandbox configuration (ShellEnabled, AllowedRepositoryRoots, DestructiveCommandPatterns, etc.) must NOT be stored in appsettings.json. It must be dynamic and project-scoped — each project has its own sandbox settings, stored in the database alongside project data, not in a static config file.
**Why:** User requirement — appsettings.json is deployment-global and static; sandbox policy is per-project and operator-configurable at runtime.

### 2026-06-10T09:36:27-07:00: GitOps sandbox policy — YAML file in repo
**By:** Ahmed Sabbour (@sabbour) (via Copilot)
**What:** Sandbox policy moves from SQLite (per repository_path row) to a GitOps YAML file: `.scaffolder/sandbox.yml` at the repository root. ISandboxPolicyStore interface is unchanged; SqliteSandboxPolicyStore replaced by YamlSandboxPolicyStore. SetPolicyAsync writes the file; operator commits it. Policy is version-controlled, auditable, and travels with the repo.
**Why:** Projects are Git repositories; config-as-code is the natural fit. Policy changes are reviewable via PR, git log shows history, clone gives you policy.

### 2026-06-10T09:52:19-07:00: .scaffolder/settings.yml — scoped multi-group config
**By:** Ahmed Sabbour (@sabbour) (via Copilot)
**What:** The GitOps config file is .scaffolder/settings.yml (not sandbox.yml). Settings are organized by group/section. Sandbox config lives under a top-level "sandbox:" key, leaving room for future groups (review, agents, etc.).
**Why:** Extensibility — a single file with scoped sections avoids proliferating per-feature YAML files in the .scaffolder directory.

### 2026-06-10: Security Review — Phase 6 Sandbox Policy Enrichment (seraph-phase6-review)
**Reviewer**: Seraph  
**Date**: 2026-06-10  
**Requested by**: Ahmed Sabbour  

**Summary**  
No CRITICAL or HIGH findings. Two MEDIUM items identified.

**MEDIUM: %TEMP% shared read-write (Finding #2)**  
- `GetTemporaryFilesPolicy()` adds the shared platform temp dir as read-write to every sandbox
- Enables cross-sandbox contamination and temp-file race attacks
- **Recommendation**: Use per-sandbox temp subdirectory

**MEDIUM: NetworkEnabled in repo-committed YAML (Finding #5)**  
- Any contributor with push access can enable network in `.scaffolder/settings.yml`
- No human-in-the-loop gate specifically for network changes
- **Recommendation**: Warn operators visibly; consider admin-only control

**LOW findings (no action required)**  
- PATH injection: existing mxc SDK filters sufficient
- LocalAppData read-only exec: by design for tool access
- Dangling /sbin symlink: harmless ENOENT
- ClearPolicyOnExit crash: ephemeral AppContainer SID makes leak benign
- Missing /usr/share: functional degradation only, not exploitable


### 2026-06-11: Round 9 Artifact-Browser Integration (commit 35b4148)

**Round**: 9 (artifact-browser feature, branch 001-single-agent-run/artifact-viewer)
**Commit**: 35b4148
**Requested by**: Ahmed Sabbour (@sabbour)
**Agents**: Trinity (Frontend), Tank (Backend), Seraph (Security), Morpheus (Runtime), Link (Platform)
**Verification**: 277 .NET tests + 56 web tests pass, 0 warnings

#### Spawned Work Batch

**Trinity (Frontend, background)**
- F1: Modal auto-close fix for artifact browser
- F2: Syntax-highlighted diff with added/removed line treatment
- F4: Typography coherence pass across UI
- F5: Files-tab colored filenames by status
- Outcome: done, 56 web tests pass

**Tank (Backend, background)**
- B1: SSE recovery — synthetic review.requested for checkpoint-less awaiting_review runs
- B2: report_intent registered as single Copilot custom tool, surfaced as agent.intent event
- Outcome: implemented; B1 later found to have blockers

**rubber-duck + Seraph (pre-impl design review)**
- Approved B1/B2 design
- Recommended B3 (request-changes) ship as fresh-workflow-on-same-worktree MVP

**Seraph + rubber-duck (post-impl review)**
- B2: clean, approved for merge
- B1: REJECTED with blockers (synthetic review emitted for un-approvable runs; tree-hash fail-open; SSE Last-Event-ID reconnect hang)

**Morpheus (Runtime, background)**
- Fixed B1 blockers under reviewer-rejection lockout (Tank locked out)
- Added strict pre-emit validation, fail-closed tree-hash, RunStreamEntry.HasEventType + SSE break fix, B2 permission-handler tests
- 276 tests passing

**Seraph (re-review)**
- Confirmed Morpheus fixes for B1
- Found 1 remaining MAJOR: direct-review merge-time tree-hash fail-open in Program.cs

**Link (Platform, background)**
- Fixed the direct-review fail-open + regression test
- Result: 277 tests passing

#### Accepted Design Decisions

**D1: B3 (request-changes) MVP Scope**
- Fresh-workflow-on-same-worktree is the MVP for B3 request-changes handling
- NOT a MAF (Merge Approval Flow) cycle surgery; defer fuller state machine to future increment
- **Rationale**: Unblocks artifact-browser completion without architecture churn; allows shipping B3 as soon as B1/B2 stabilize

**D2: Reviewer Comment Sanitization**
- Reviewer feedback must be sanitized and wrapped in a delimited untrusted "reviewer feedback (subordinate to system rules)" section
- Never raw-concatenated into system context
- **Rationale**: Prevents prompt injection; preserves determinism; allows recovery from malicious or accidental reviewer sabotage

**D3: Tree-Hash Validation Fail-Closed**
- Tree-hash validation must be fail-closed everywhere
- Null current hash = immediate failure (never pass-through)
- Applies to: B1 SSE recovery, B3 request-changes reconciliation, all worktree state transitions
- **Rationale**: Fail-open tree-hash led to 35b4148 blockers; fail-closed is the only safe default

**D4: API Capability Documentation Parity (Principle IV)**
- Every new API capability must update docs/reference/* (API reference, CLI reference, Web UI reference)
- CLI + Web parity is non-negotiable
- Scoped to 001-single-agent-run; applies to B2 (report_intent), B3 (request-changes), and all future API extensions
- **Rationale**: Prevents shipped-but-undocumented features; ensures users can discover capability across all clients

### 2026-06-11: Round 10 Request-Changes & Stream Eviction Completion

#### Spawned Work Batch

**Tank (Backend, B3 Initial)**
- B3 request-changes endpoint, StartRevisionAsync, audit table, CLI
- Outcome: Implemented; rejected by Seraph + rubber-duck (2 blockers + 2 majors); locked out

**Trinity (Frontend, F3 Initial)**
- Three-button review bar, client hook, event handling
- Outcome: Implemented; design review approved

**Smith (Frontend, MAJOR Fix)**
- deriveRunStatusFromEvents latest-event-wins, SSE reconnect() dedup, bar gating
- Outcome: 3 critical fixes; approved

**Morpheus (Backend Fixes, Tank Lockout Authority)**
- CAS exclusivity (Interlocked.CompareExchange guard), generation guard (increment on completion), RunWorkflowRegistry.Abandon, nonce prompt fence, audit fail-closed, shell-approval authz, 404 non-enum, append-only triggers
- Outcome: Resolved all Tank B3 blockers; partially blocked by rubber-duck on stream eviction race

**Link (Stream Eviction Tranche 1)**
- LastActiveAt before cap check, TryMarkEvicted TOCTOU fix, SendResponseAsync recovery finally
- Outcome: Implemented; blocked by rubber-duck (new revision/eviction race discovered)

**Tank (RunStreamStore Fixes)**
- LastActiveAt moved before cap, TryMarkEvicted atomic, recovery finally
- Outcome: Implemented; blocked by rubber-duck (new revision/eviction race)

**Link (Stream Eviction Tranche 2, Final)**
- ClearAwaitingReview refreshes _lastActiveAt+returns bool, TryBumpGeneration checks _evicted, StartRevisionAsync recreates on evicted entry
- Outcome: Implemented; approved by rubber-duck

**Trinity (7 UI Refinements)**
- Filename truncation, button layout+both-tabs, modal single dark header, light oneLight theme, modified files amber not red, review.changes_requested comment blockquote, SSE reconnect after commitRun
- Outcome: All approved

**Trinity (AbortController Fix)**
- Per-effect AbortController, stopRef removed
- Outcome: Approved

**Link (B3 Minor Fixes)**
- Case-insensitive delimiter, stream entry MarkLive, SendResponseAsync strand
- Outcome: All approved by Seraph + rubber-duck

**Seraph + rubber-duck (Review Cycles)**
- 2 Seraph rounds (initial RED, cycle 2 GREEN), 4 rubber-duck re-reviews (initial + 3 concurrent fix reviews)
- Final: Seraph 🟢, rubber-duck APPROVED

#### Accepted Design Decisions

**D5: Atomic Worktree Merge on /commit (Round 10)**
- The "Commit Changes" button becomes "Commit and merge"
- /commit endpoint must merge the worktree branch back into OriginatingBranch atomically (no intermediate states)
- Merge conflicts surfaced in UI with file list for reviewer intervention
- **Rationale**: Prevents committed runs from being in an orphaned-worktree state; atomic merge ensures consistency across API restarts; aligns with D3 (fail-closed tree-hash validation)

**D6: Stream Event Persistence TODO (Round 10)**
- Run event persistence via SQLite table (run_events) + replay is a planned TODO for Round 11+
- Not blocking shipment of Round 10 (a4b3f98)
- Addresses "history missing on restart" issue identified in Round 9 artifact-browser work
- **Rationale**: Current in-memory broadcaster (RunStreamStore) loses all events across API restarts; SQLite append-only table + event replay will persist run history durably

#### Test Coverage

- **.NET Tests**: 306 (up from 277 in Round 9)
- **Web Tests**: 71 (up from 56 in Round 9)
- **Build Warnings**: 0
- **Build Errors**: 0
- **Regressions**: 0

#### Key Findings & Resolutions

**CAS Exclusivity Race**: Non-exclusive CurrentHashAlgorithm CAS allowed race between revision startup and concurrent StartRevisionAsync (Morpheus fix: Interlocked guard + generation checks)

**Generation Guard Missing**: RunWorkflowEntry.Generation not incremented on revision completion (Morpheus fix: generation increment on workflow completion + guard all transitions)

**Audit Fail-Open**: Audit log INSERT failure silently ignored (Morpheus fix: fail-closed exception)

**Shell Approval Authz Missing**: Shell approval endpoint didn't validate shell-caller identity (Morpheus fix: authz check for shell-caller identity)

**Stream Eviction Race (Tranche 1)**: LastActiveAt read after cap check; TryMarkEvicted non-atomic; SendResponseAsync exception recovery incomplete (Link fix: move LastActiveAt before check, atomic TryMarkEvicted, recovery finally)

**Stream Eviction Race (Tranche 2)**: ClearAwaitingReview doesn't return evicted status; TryBumpGeneration allows stale bumps on evicted entries; StartRevisionAsync creates orphaned revisions on evicted entries (Link fix: return bool+refresh, check _evicted, recreate on evicted)

**Frontend Event Logic**: deriveRunStatusFromEvents read first event (not latest), SSE reconnect() sent duplicate Last-Event-ID, bar gating missing (Smith fix: latest-event-wins, dedup, conditional render)

#### Commit & Verification

**Commit**: a4b3f98  
**Date**: 2026-06-11  
**Verification**: 306 .NET tests + 71 web tests pass, 0 warnings, 0 errors, 0 regressions

---


## 2026-06-11T15:46:22-07:00: 003-projects implementation plan — 4-round spec-to-plan journey COMPLETE

### Round 1 — Tank (Coordinator) authors plan.md after dual specification rounds

**By:** Tank (Backend engineer), coordinating specs/003-projects/plan.md authoring after spec 003-projects clarified and locked (commit pending).

**What:** Tank authored a comprehensive 12-section implementation plan grounding the design in real codebase seams. Project identity in new SQLite `projects` table via `SqliteProjectStore` (not yml). GitHub Copilot authorized through GitHub OAuth token store (no separate API key). Per-run `model_id` threaded end-to-end. Race-safe delete via atomic `TryCreateProjectRunAsync` + `TryBeginDeleteAsync` serialization. Multi-tenant token keying by `GitHubTokenScope`. Sign-out fail-closed (config fallback only in NeverSignedIn scope). Full specification of `IProjectWorkspaceProvider` seam for hosted-cloud storage. 

**Outcome:** Plan passed skeleton review and locked 15/16 checklist items; only FR-025 (cloud storage model) marked [NEEDS CLARIFICATION]. Routed to dual architecture/logic review (Seraph + rubber-duck). **Seraph REJECT (5 blocking)**; **Rubber-duck REJECT (5 blocking)**. Morpheus locked in for revision round.

---

### Round 2 — Morpheus (Runtime) revises plan addressing all 9 blocking issues

**Merged from inbox:** morpheus-003-plan-revision.md (2026-06-11T15:00:00Z)

**By:** Morpheus (Runtime engineer), revising the plan Tank authored after dual REJECT. Grounded in real codebase, not assumptions.

**Decisions applied (A-I):**
- **A. Per-run model_id** — added end-to-end plumbing (Run.ModelId, runs.model_id, AgentTurnInput.ModelId, IAgentRunner.ExecuteAsync param); model resolution order: explicit per-run → project default → provider runtime default; unavailable-model recovery via 409 with available list.
- **B. Blank-project startup** — initial empty commit on configured default branch at creation (Repository.Init + commit on `Workspace:DefaultBranch`).
- **C. Race-safe delete** — project `state` CAS gate + authoritative force-terminal; non-terminal runs marked Failed/cancelled; bounded requery.
- **D. Relink validation** — separate rule from creation (accepts non-empty, requires valid git repo + matching origin where determinable).
- **E. Creation rollback** — compensation tracks app-created vs pre-existing artifacts; deletes only app creations on failure.
- **F. IProjectWorkspaceProvider** — fully specified seam (BackendName, ResolveWorkingDirectory, EnsureWorkspace, IsAvailable, Release); PersistentVolumeWorkspaceProvider owns mount-path mapping and validation.
- **G. Token store tenancy** — keyed by GitHubTokenScope (fixed installation key locally; per-tenant in cloud from CallerContext.User / AgentTurnInput owner).
- **H. Sign-out fail-closed** — tri-state per scope (SignedIn / SignedOut / NeverSignedIn); config fallback suppressed after sign-out, active only for NeverSignedIn installs.
- **I. Copilot OAuth dual-grant** — early Phase 2 spike; fallback: same OAuth token via CopilotClientOptions.GitHubToken; if single token insufficient, OAuth for clone + SDK device-auth for Copilot.

**Outcome:** Seraph **APPROVE WITH CHANGES**; Rubber-duck **REJECT on C (residual TOCTOU)** — race between project delete and concurrent run-create identified. Tank locked out; Smith assigned to round 3.

---

### Round 3 — Smith (QA) closes delete/run-create race via atomic reservation

**Merged from inbox:** smith-003-plan-race-fix.md (2026-06-11T15:55:00Z)

**By:** Smith (QA engineer), surgical race fix. Tank and Morpheus locked out under reviewer-rejection protocol.

**Decision (issue C):** Atomic run-create reservation — `SqliteRunStore.TryCreateProjectRunAsync` inserts `Pending` run row guarded by project.Active in single SQLite transaction, BEFORE any side effect. Delete ordering unchanged: `TryBeginDeleteAsync` serialization point → enumerate non-terminal runs (now includes any reservation that won the race) → cancel + mark Failed → verify requery → release + record delete.

**Polish items folded in:** (1) NeverSignedIn config-token fallback LOCAL-only (non-interactive installs; cloud always requires interactive sign-in). (2) Background runner token keyed by resolved GitHubTokenScope, not raw owner. (3) Phase 1 dependency: from-GitHub clone codes against IGitHubTokenStore interface (defined Phase 0) with test fake; concretely implemented Phase 2.

**Outcome:** Delete/run-create TOCTOU closed. Rubber-duck **REJECT on NEW blocker** — reserved Pending leaks if post-reservation side effect fails; no compensation. Morpheus locked out; Link assigned to round 4.

---

### Round 4 — Link (Platform) applies owner-accepted compensation without re-review

**Merged from inbox:** link-003-plan-reservation-compensation.md (2026-06-11T16:20:00Z)

**By:** Link (Platform engineer), owner-accepted review-resolution. NO further review cycle per project owner decision.

**Decision (post-reservation compensation):** Wrap post-reservation side-effect sequence in compensation so reserved row is self-healing on failure. On ANY failure after reservation commits and before workflow registry observable: (1) terminalize reserved row via conditional+idempotent `TrySetTerminalStatusAsync(runId, Failed, "run_start_failed")`. (2) Emit existing terminal event (run.failed, not new event). (3) Clean up created artifacts (worktree, registry entry, stream entry). Mirrors CreationScope rollback idiom from issue E.

**New section:** Added section 13 "Review Resolutions" documenting all four rounds (Tank reject; Morpheus approve-with-changes; Smith race-fix; owner-accepted compensation).

**Outcome:** Reserved Pending self-heals on start failure. Coordinator committed plan as b9061aa on branch 003-projects.

---

### Summary: 4-Round Plan Completion

- **Round 1 (Tank):** Authored plan, dual-reviewer REJECT (9 blocking issues).
- **Round 2 (Morpheus):** Fixed issues A-I, Seraph APPROVE-WITH-CHANGES, Rubber-duck REJECT on race (C).
- **Round 3 (Smith):** Closed C race via atomic reservation, Rubber-duck REJECT on new compensation gap.
- **Round 4 (Link):** Applied compensation, owner-accepted, NO re-review.
- **Commit:** b9061aa on branch 003-projects ([Spec Kit] Add 003-projects implementation plan; resolve FR-025).
- **Outcome:** Plan locked. Next phase: generate tasks / begin implementation (Phase 0 domain + persistence).

---

### 2026-06-11T15:46:22-07:00: FR-025 resolved — hosted-cloud project storage model

**By:** Squad on behalf of Ahmed Sabbour.

**Decision:** Hosted-cloud project storage = a managed per-project persistent volume mounted at the project's working-directory path, behind the `IProjectWorkspaceProvider` abstraction. Local-directory model unchanged (Principle VI deployment parity). The `PersistentVolumeWorkspaceProvider` owns mount-path mapping, validation, and availability detection; actual cloud volume allocation/attachment is environment/operator-supplied, surfaced to the app as a mount at `Workspace:PersistentVolume:MountRoot`.

**Why:** Enables Principle VI deployment parity without coupling the app to cloud infrastructure provisioning. Cloud deployment is operator-responsible; scaffolders-app is operator-agnostic.

**Plan reference:** Section 3.2 (IProjectWorkspaceProvider contract) + Section 4.2 (workspace provisioning).

---

### 2026-06-11T15:46:22-07:00: 003-projects implementation plan authored manually and committed

**By:** Squad on behalf of Ahmed Sabbour.

**Decision:** The 003-projects implementation plan was authored MANUALLY by the team (NOT via speckit.plan tool, per explicit user constraint) across 4 collaborative review rounds and committed as b9061aa on branch 003-projects.

**Why:** User directive to avoid speckit.plan automation; maintain team visibility and decision ownership across architecture consensus.

**Plan reference:** specs/003-projects/plan.md (12 sections + Review Resolutions); commit b9061aa.

---

### 2026-06-11T15:46:22-07:00: 003-projects architecture decisions settled

**By:** Squad on behalf of Ahmed Sabbour.

**Decisions:**
1. **Project identity** — new SQLite `projects` table + `SqliteProjectStore` (not .scaffolder/settings.yml).
2. **Copilot authorization** — GitHub OAuth token store (no stored Copilot API key).
3. **Per-run model plumbing** — model_id threaded end-to-end (explicit per-run → project default → provider runtime default).
4. **Race-safe delete** — atomic `TryCreateProjectRunAsync` reservation + `TryBeginDeleteAsync` serialization + Pending-in-sweep, with reserved-run compensation to Failed on post-reservation start failure.
5. **Token-store tenancy** — keyed by `GitHubTokenScope` (per-caller/tenant in cloud).
6. **Sign-out semantics** — fail-closed (config-token fallback only in NeverSignedIn/local scope).
7. **Run cancellation** — run.cancelled event reused for delete-cancellation (no new RunStatus).
8. **Workspace provisioning** — `IProjectWorkspaceProvider` seam fully specified; cloud provisioning operator-supplied.

**Why:** Lock design decisions after dual-reviewer consensus and owner acceptance to enable Phase 0 domain model + persistence layer implementation.

**Plan reference:** specs/003-projects/plan.md (sections 3-4, 13 Review Resolutions).

---

### 2026-06-11T15:46:22-07:00: Dual-reviewer gatekeeping across 4 rounds established precedent

**By:** Squad on behalf of Ahmed Sabbour.

**Decision:** Implementation plans are gated by two independent reviewers (architecture/security + logic/design) across multiple review rounds until consensus. Blocking findings lock the originating author; subsequent rounds may be owned by other eligible team members. Final narrow gaps (e.g., reserved-Pending compensation) may be accepted by the project owner as documented review-resolutions without re-review, provided the resolution follows the reviewer's prescribed remediation.

**Why:** Dual-reviewer gatekeeping prevents single-reviewer blind spots and ensures architecture quality before Phase 0 implementation. Owner acceptance of prescribed resolutions unblocks delivery without process overhead.

**Precedent:** 003-projects plan: Tank authored, Seraph + Rubber-duck rejected (Tank locked), Morpheus revised (locked after Rubber-duck found race), Smith fixed race (locked after Rubber-duck found compensation gap), Link applied compensation (owner-accepted, no re-review). Locked design decisions now ground Phase 0 + Phase 1 tasks.

---

## 2026-06-11T16:55:00-07:00: 003-projects Merged Inbox Decisions

*Merged by Scribe from .squad/decisions/inbox/ as part of 003-projects close-out.*

### Artifact Browser Design Decisions (squad-artifact-browser-design.md — 2026-06-10)

**Decision:** Artifact browsing architecture resolves 5 blocking design issues identified in pre-implementation review.

1. **Historical State** — Parsed from stored `run.unifiedDiff` (immutable, authoritative, no worktree reconstruction).
2. **Live State** — Served from live worktree filesystem; frontend polls for updates.
3. **Pending State** — Returns empty array (no worktree yet; simplifies frontend state machine).
4. **All-Filter Deduplication** — Frontend merges historical + live; deduplicates on path (one entry per unique path).
5. **CLI Parity** — CLI artifact browser mirrors React ArtifactBrowser UX (same filter/navigation logic).

**Backend:** `GET /api/runs/{id}/files`, `GET /api/runs/{id}/files/{**path}`, WorktreeManager path validation.
**Status:** APPROVED — all blocking issues resolved. Ready for implementation (Tank/Trinity/Smith).

---

### Seraph Pre-Implementation Security Review: Artifact Browser (seraph-pre-artifact-browser.md — 2026-06-10)

**Verdict:** BLOCK (resolved with resolution path, now cleared).

4 high-severity findings and resolutions:
1. **Secrets in Content Exposure** — Raw-content endpoint deferred; diff-only access inherently limits exposure to reviewed changed lines.
2. **Arbitrary Path Access** — Path whitelist enforcement; only paths within run artifact bounds served; validated against stored run metadata.
3. **Path Validation Gaps (symlink traversal, double-encoding, null bytes)** — Pre-open canonical normalization + out-of-bounds detection; post-open file-stat verification.
4. **Markdown XSS via Content Rendering** — Diff-only scope; structured Git diff format with safe syntax highlighting; no eval.

**Status:** RESOLVED — Clear to implement diff-only artifact browser slice.

---

### Seraph Final Verdict: Artifact Browser (seraph-final-artifact-browser.md — 2026-06-10)

**Verdict:** ADVISORY (block cleared).

All 4 prior security blocks resolved:
1. **ANSI Escape Injection** — Morpheus implemented character-by-character scanner in `ArtifactCommands.cs` (no regex). Cleared.
2. **%2F Path Traversal** — Tank implemented strict decode-then-validate on backend. Cleared.
3. **Run State Authorization** — Tank enforced owner identity verification before artifact path operations. Cleared.
4. **C1/ADS File Rejection** — Tank added reserved filename filtering. Cleared.

**Remaining Advisory (non-blocking):** `ArtifactBrowser.tsx` stale UI flash on `runId` change — cosmetic flicker, no data exposure, no authz bypass.
**Status:** ✅ Clear to commit.

---

### Morpheus OAuth Scope Assumption (morpheus-oauth-scope-assumption.md — 2026-06-11)

**Decision:** Proceeding with Spike Outcome A — one device-flow token with scopes `repo` + `read:user` satisfies both private-repo clone (LibGit2Sharp UsernamePasswordCredentials) and GitHub Copilot SDK (CopilotClientOptions.GitHubToken). Spike verification occurs during Phase 7 integration tests (T024, gated on `GITHUB_INTEGRATION_TESTS`). Fallback: Spike Outcome B — separate clone token + SDK `UseLoggedInUser` — is fully specified via the `IGitHubTokenStore` seam.

**Why:** Allows Phase 2 implementation to proceed without blocking on a live spike.

---

### Morpheus ANSI Escape Sanitizer (morpheus-ansi-fix.md — 2026-06-10)

**Decision:** Rewrote ANSI terminal-escape stripping in `ArtifactCommands.cs` with comprehensive character-by-character scanner. No regex; handles incomplete/malformed sequences safely.

**Lockout Notice:** Per Reviewer Rejection Protocol, Trinity is locked from further changes to `ArtifactCommands.cs`. Morpheus retains ownership of terminal-layer security.
**Status:** ✅ Build clean, all tests passing, security block resolved.


### 2026-06-12T09:39:18-07:00: User directive — single provider

**By:** Ahmed Sabbour (@sabbour) (via Copilot)
**What:** Provider is now GitHub Copilot ONLY. Microsoft Foundry is dropped as a model source. Constitution Principle II must be amended, and feature 005 (and any other provider-selection surfaces) updated to remove the two-provider choice.
**Why:** User decision: "For the per provider thing, we need to update it and the constitution. I settled on GitHub Copilot only."


### 2026-06-12T09:39:18-07:00: User directive — per-role model with runtime override

**By:** Ahmed Sabbour (@sabbour) (via Copilot)
**What:** Each agent ROLE carries a DEFAULT model in its definition/charter, and that default can be OVERRIDDEN at runtime. Provider remains fixed to GitHub Copilot (prior directive); only the model varies. Casting must assign a default model per role; runtime (casting proposal runs and later agent execution) must allow overriding the model per run.
**Why:** User decision: "For the model per agent role, the role should have a default model but allow for overriding at runtime."


# Decision: Feature 005 plan revision (rubber-duck rejection fixes)

Author: Morpheus (Runtime Engineer)
Date: 2026-06-12
Scope: specs/005-agent-team-casting/plan.md (revised in place; Tank locked out per Reviewer Rejection Protocol)

## Key decisions

1. Read-only proposal-generation run mode (section 10.1). Model-assisted casting (US2/US3) runs on MAF via IAgentRunner but with a no-write/no-shell/no-arbitrary-read tool allowlist, NO AgentTurnExecutor worktree-commit step, and persisted+streamed events. Only ProjectSignalScanner output and the read-only catalog feed the model. All .squad/ writes happen only in confirm. Write-safety is structural (FR-005, FR-007, FR-008).

2. Per-run provider/model selection. free_text and analysis proposals accept provider (validated against github-copilot | microsoft-foundry) and model_id, resolved request -> project default -> system default (003 FR-015), at CLI/Web parity. Scenario casting takes none (FR-008, FR-009).

3. Sync stays inside .squad/ + reviewed-change token. Merge attributes moved to .squad/.gitattributes with paths relative to .squad/ and included in the reviewed change set. POST /sync verifies expected_change_set_hash from GET /sync and rejects on mismatch (FR-024, FR-027).

4. Merge-safe append-only state. Adopted JSONL event-log sidecars (registry.events.jsonl, history.events.jsonl) that union-merge cleanly line-by-line; canonical registry.json/history.json are regenerated from them. Tests prove two-branch merges are parseable and lossless (FR-027, FR-023).

5. Canonical vs legacy layout (decided, not open). .squad/casting/ is canonical/authoritative; root-level casting-*.json is legacy. Read: only-legacy -> read + flag migration; both-divergent -> detect + report layout_conflict and offer migration (never silently pick); writes always to .squad/casting/ (FR-011, FR-019, FR-023).

6. Recast semantics (decided, not open). Augment adds members, retires none. Recast re-derives roster, retains overlap, adds new, retires dropped members: registry status=retired, charter moved to .squad/agents/_alumni/{name}/charter.md, name reserved forever, recast snapshot recorded. Never overwrites/renames (FR-021, FR-022).

Non-blocking: CastProposalStore lifecycle (in-memory, one active proposal/project, 30-min TTL, lost on restart, owner = 003 project owner); analysis scanner excludes .git/build outputs/dependency folders/binaries/secrets and sends summary-only (section 10, 12).

Sections changed: 1, 2, 3 (constitution + complexity), 4.1, 4.2, 4.3, 5.1, 5.2 (+ new precedence subsection), 6.1, 6.2, 6.3, 6.4, new 6.5, 7 (T004/T012/T014/T015/T021/T024/T026), 10 (+ new 10.1), 11, 12, 13, 14.


# Decision: Reconcile 005 plan to single fixed provider (GitHub Copilot) + per-role default model

Author: Morpheus (Runtime Engineer)
Date: 2026-06-12T09:50:00-07:00
Scope: specs/005-agent-team-casting/plan.md only
References: .specify/memory/constitution.md v1.4.0 (Principle II), spec.md FR-032/FR-033/FR-034, prior Issue 2 fix (now superseded)

## Decision
The earlier Issue-2 fix introduced a two-provider selection (provider validated against
github-copilot / microsoft-foundry plus model_id). Constitution v1.4.0 and the amended spec
make GitHub Copilot the SINGLE, FIXED provider that is never selectable per run/role/project.
The plan is reconciled accordingly:

1. Provider selection removed everywhere. No `provider` field, no two-value validation, no
   provider resolution order, no provider UI/CLI/API control, no Microsoft Foundry. The MAF
   dependency list now names only GitHubCopilotAgentRunner.

2. Per-role DEFAULT model + runtime override. Each role carries a default GitHub Copilot model,
   recorded in the role definition, charter ("## Default Model" section), and casting registry
   (default_model). At runtime an OPTIONAL `model_id` overrides it for that run.
   Resolution order: request model_id override -> role/agent default model -> system default model.
   The model used is observable in the run steps (Principle V). Model override is exposed at CLI
   (`--model`) and Web (wizard model picker) parity; provider is not exposed (nothing to choose).

3. Section 3 Constitution Check updated to v1.4.0; Principle II row now reads
   "GitHub Copilot only, fixed provider".

## Constraints honored
All 6 rubber-duck blocker fixes left intact (read-only proposal run mode, .squad/.gitattributes +
change-set hash, JSONL event-log merge, canonical layout precedence, augment-vs-recast, scanner
bounds, CastProposalStore lifecycle). Plan still covers FR-001..FR-034, SC-001..SC-007, 5 user
stories. Zero emojis. No mocks/placeholders.


### 2026-06-12T09:21:35-07:00: Casting lives in a new `packages/Scaffolder.Squad` package, orchestrated by `apps/Scaffolder.Api/Casting/CastingService`

Casting is a self-contained bounded context (catalog, universe allocation, charter compilation, registry/history bookkeeping, git sync). Provider-agnostic primitives (catalog reader, `.squad` reader/writer, universe allocator, charter compiler, git scribe) go in `packages/Scaffolder.Squad` so they are host-free and unit-testable; the host-bound orchestration (`CastingService`) lives in `apps/Scaffolder.Api/Casting/`, mirroring how `ProjectService` sits in `apps/Scaffolder.Api/Projects/`. Casting domain records do NOT bloat `Scaffolder.Domain`, and casting does NOT create a parallel agent runtime — model-assisted modes reuse `IAgentRunner`/`AgentTurnExecutor` from `Scaffolder.AgentRuntime` (Principle I).

### 2026-06-12T09:21:35-07:00: Writer emits the spec `.squad/casting/` layout; reader tolerates the repo's root-level `casting-*.json` for round-trip

Spec FR-012 mandates `.squad/casting/policy.json|registry.json|history.json`, but this repo's own `.squad/` (and bradygaster/squad) uses root-level `casting-policy.json`, `casting-registry.json`, `casting-history.json` with fields `casting_policy_version` / `allowlist_universes` / `universe_capacity`. Decision: the writer always emits the FR-012 subfolder layout; the reader round-trips BOTH layouts so externally-created teams (FR-019) and existing `.squad/` folders (FR-011) load cleanly. Flagged as an open question for a possible spec/convention alignment.

### 2026-06-12T09:21:35-07:00: Git sync uses LibGit2Sharp with per-file staging, not shelling to git

FR-024 requires staging changed `.squad/` files individually (never bulk `git add`) and committing only `.squad/`. We reuse LibGit2Sharp (already a dependency via `ProjectGitInitializer`) and stage each changed path with `Commands.Stage(repo, path)`, adapting squadboard's scribe per-file-staging pattern to .NET. This avoids a hard dependency on a `git` binary on PATH (deployment parity, Principle VI) and gives precise index control. FR-027 union merge is configured by writing `.gitattributes` entries (`*.json merge=union`) for the append-only `casting/` state.

## Archived on 2026-06-27 (entries older than 7 days as of 2026-06-27T03:15:00-07:00)

### 2026-06-19: Coordinator Page Header Dedup — trinity-coordinator-headers
**Source:** .squad\decisions\inbox\trinity-coordinator-headers.md


**Agent:** Trinity (frontend)  
**Date:** 2026-06-19  
**Branch:** 009-backlog-kanban-board  

#### Problem

The CoordinatorRunPage had two redundant heading stacks:

1. **Run ID shown twice**: breadcrumb showed "Orchestration {shortId}" AND the page `<Title2>` row also showed `{shortId}` beside "Orchestration".
2. **"Outcome spec" title shown twice**: the left-column section header said `<Title3>Outcome spec</Title3>`, and the `OutcomeSpecPanel` component rendered its own `<Title3>Outcome spec</Title3>` directly below it.

#### Solution

##### CoordinatorRunPage.tsx
- **Removed** `<span className={styles.runIdLabel}>{shortId}</span>` from the page header row. The breadcrumb (`Orchestration {shortId}`) is the single canonical id reference for navigation; the h2 page title now reads just "Orchestration".
- **Removed** the `<div className={styles.outcomeHeaderRow}>` wrapper that held `<Title3>Outcome spec</Title3>` + the collapse button. Instead, passed `onCollapse={() => setOutcomeCollapsed(true)}` directly to `<OutcomeSpecPanel>`.

##### OutcomeSpecPanel.tsx
- **Added** `onCollapse?: () => void` optional prop.
- **Added** `ChevronLeftRegular` icon import.
- **Added** collapse button inline in the panel's own header row (rendered only when `onCollapse` is provided), so the panel header now shows: `"Outcome spec" [Drafting badge] [spacer] [Spinner?] [Collapse button?]`

#### Before / After Heading Structure

**Before:**
```
breadcrumb:  Projects / Project / Orchestration 05790459   ← shortId #1
h2 header:   Orchestration   05790459                       ← shortId #2 (REDUNDANT)
h3 section:  Outcome spec    [collapse ▸]                  ← title #1
h3 panel:    Outcome spec    [Drafting]                    ← title #2 (REDUNDANT)
```

**After:**
```
breadcrumb:  Projects / Project / Orchestration 05790459   ← shortId (single)
h2 header:   Orchestration                                 ← clean, no duplicate id
h3 panel:    Outcome spec    [Drafting]   [collapse ▸]    ← single title + badge + button
```

#### Files Changed

| File | Change |
|------|--------|
| `apps/web/src/pages/CoordinatorRunPage.tsx` | Removed `shortId` span from header row; removed outer `outcomeHeaderRow` div; added `onCollapse` prop to `OutcomeSpecPanel` call |
| `apps/web/src/components/OutcomeSpecPanel.tsx` | Added `ChevronLeftRegular` import; added `onCollapse?: () => void` prop; added collapse button in header row |

#### Tests Not Affected

No tests asserted on the removed shortId span or the outer "Outcome spec" section heading text — the coordUx test checking `toContain('Orchestration')` continues to match the h2 and breadcrumb. The OutcomeSpecPanel tests use role-based queries, unaffected by the structural change.

#### Verification

- `npx tsc --noEmit` → 0 errors ✓
- `npm run build` → tsc clean + vite built in 1.11s ✓
- `npm test` → **209/209 tests passed** (28 files) ✓
---
