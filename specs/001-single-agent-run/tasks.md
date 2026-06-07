# Tasks: Single-Agent File-Editing Run

**Input**: Design documents from `/specs/001-single-agent-run/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/run-api.yaml, contracts/run-step-event.schema.json

**Organization**: Grouped by logical phase: Setup -> Foundation -> Agent Loop + Event Log (US1) -> Streaming/SSE (US2) -> Model Source Selection (US3) -> Review + Merge (US4) -> Governance + Responsible AI -> CLI Client -> Web UI Client -> Contract Tests + Integration QA

**Coverage**: FR-001 to FR-029, NFR-001 to NFR-003, SC-001 to SC-010, all four user stories, all edge cases from the spec

---

## Phase 1: Setup

**Purpose**: Repository scaffolding, solution structure, and toolchain initialization per plan.md.

- [ ] T001 Create solution file Scaffolder.sln and directory tree: backend/Scaffolder.Api/, clients/cli/, clients/web/, tests/Scaffolder.Api.Tests/, tests/web/
- [ ] T002 Initialize Scaffolder.Api ASP.NET Core .NET 9 Minimal API project in backend/Scaffolder.Api/Scaffolder.Api.csproj with dependencies: Microsoft.Agent.Framework, Microsoft.EntityFrameworkCore.Sqlite, and Microsoft.AspNetCore.OpenApi
- [ ] T003 [P] Initialize Scaffolder.Cli .NET 9 console project with Spectre.Console in clients/cli/Scaffolder.Cli.csproj
- [ ] T004 [P] Initialize Scaffolder.Api.Tests xUnit .NET 9 project in tests/Scaffolder.Api.Tests/Scaffolder.Api.Tests.csproj
- [ ] T005 [P] Initialize React 19 + Vite + TypeScript Web UI project with @fluentui/react-components in clients/web/package.json and clients/web/vite.config.ts
- [ ] T006 Define application settings schema: run-root directory, provider credential bindings, default maxSteps (200), default maxDurationSeconds (1800) in backend/Scaffolder.Api/appsettings.json and backend/Scaffolder.Api/Configuration/ScaffolderOptions.cs

---

## Phase 2: Foundation

**Purpose**: Persistence schema, entity models, repositories, and shared middleware that every user story depends on.

**CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T007 Add EF Core + SQLite packages and create ScaffolderDbContext with entity registrations in backend/Scaffolder.Api/Persistence/ScaffolderDbContext.cs
- [ ] T008 [P] Create Run entity with EF Core fluent configuration (fields: id UUID PK, originatingBranch, modelSource enum, taskPrompt, submittedBy, status enum, createdAt, startedAt, completedAt, maxSteps, maxDurationSeconds, sessionId FK nullable, diffSummary nullable, failureReason nullable) in backend/Scaffolder.Api/Persistence/Entities/RunEntity.cs
- [ ] T009 [P] Create Session entity with EF Core configuration (fields: id UUID PK, runId FK unique, artifactDir absolute path, worktreePath absolute path, originatingCommit SHA, createdAt) in backend/Scaffolder.Api/Persistence/Entities/SessionEntity.cs
- [ ] T010 [P] Create Event entity for the append-only log (fields: id UUID PK, runId FK, sequence int64 monotonic, type enum covering all 14 event types, timestamp informational, callId UUID nullable, payload JSON; unique index on (runId, sequence)) in backend/Scaffolder.Api/Persistence/Entities/EventEntity.cs
- [ ] T011 [P] Create ToolOperation entity (fields: id UUID PK, runId FK, callId UUID, toolName enum read_file|write_file, requestedPath, resolvedPath nullable, result enum success|rejected|error, errorCode enum nullable PATH_ESCAPE|NOT_FOUND|PERMISSION|UNKNOWN, createdAt) in backend/Scaffolder.Api/Persistence/Entities/ToolOperationEntity.cs
- [ ] T012 [P] Create ReviewDecision entity (fields: id UUID PK, runId FK unique, decision enum approve|decline, reviewer string, comment nullable, createdAt, mergeResult enum not_attempted|merged|conflict|failed) in backend/Scaffolder.Api/Persistence/Entities/ReviewDecisionEntity.cs
- [ ] T013 [P] Create OperationalRecord entity for compliance (fields: id UUID PK, runId FK unique, submittedBy, modelSource enum, startedAt, endedAt, stepCount int, outcome enum, policyTrace JSON array, createdAt; distinct from the event log) in backend/Scaffolder.Api/Persistence/Entities/OperationalRecordEntity.cs
- [ ] T014 Generate EF Core initial migration covering all six entities and apply to SQLite dev database; add migration files to backend/Scaffolder.Api/Persistence/Migrations/
- [ ] T015 [P] Implement IRunRepository interface and EF Core RunRepository (create, get by id, update status, update diffSummary, update failureReason) in backend/Scaffolder.Api/Persistence/RunRepository.cs
- [ ] T016 [P] Implement IEventRepository interface and EF Core EventRepository with append-only write (no update/delete) and read-from-sequence query (events where sequence > lastSeenSequence ordered by sequence ascending) in backend/Scaffolder.Api/Persistence/EventRepository.cs
- [ ] T017 [P] Implement IOperationalRecordRepository interface and EF Core OperationalRecordRepository (upsert by runId, read by runId, append policyTrace entry) in backend/Scaffolder.Api/Persistence/OperationalRecordRepository.cs
- [ ] T018 Configure global error handling with ProblemDetails middleware, structured logging (ILogger), and 404/409 mapping in backend/Scaffolder.Api/Program.cs
- [ ] T019 Register all repositories and services in the DI container; configure OpenAPI generation linked to contracts/run-api.yaml; set server base URL http://localhost:3000 in backend/Scaffolder.Api/Program.cs

**Checkpoint**: Persistence schema applied, repositories available, middleware configured. User story implementation can begin.

---

## Phase 3: US1 - Agent Loop + Event Log (Priority: P1) - MVP

**Goal**: Submit a task, run the Microsoft Agent Framework loop inside an isolated git worktree, emit typed events to the durable append-only event log, reach a terminal state, and expose the diff.

**Independent Test**: POST /runs with a known originating branch and taskPrompt; confirm the run transitions queued -> running -> completed; GET /runs/{runId}/diff returns a non-empty unified diff; originating branch is byte-for-byte unmodified (SC-005 for no-merge case).

- [ ] T020 [US1] Implement WorktreeService (create a git worktree from originatingBranch, record originatingCommit SHA in Session, and clean up on policy; uses LibGit2Sharp or git CLI) in backend/Scaffolder.Api/Worktrees/WorktreeService.cs
- [ ] T021 [US1] Implement DiffService (generate unified diff of worktree against originatingCommit via git; return raw text for the diff endpoint) in backend/Scaffolder.Api/Worktrees/DiffService.cs
- [ ] T022 [P] [US1] Implement SandboxPathResolver (resolve requested path relative to artifactDir; reject absolute paths with PATH_ESCAPE; reject .. traversal with PATH_ESCAPE; resolve and verify symlinks stay inside artifactDir with PATH_ESCAPE; return resolvedPath on success) in backend/Scaffolder.Api/Agent/Tools/SandboxPathResolver.cs
- [ ] T023 [P] [US1] Implement ReadFileTool (validate path via SandboxPathResolver; on PATH_ESCAPE return tool.rejected; read text file contents; on missing file return tool.error with NOT_FOUND; on success return file contents) in backend/Scaffolder.Api/Agent/Tools/ReadFileTool.cs
- [ ] T024 [P] [US1] Implement WriteFileTool (validate path via SandboxPathResolver; on PATH_ESCAPE return tool.rejected; write text content to the resolved path inside the worktree; on success return tool.result) in backend/Scaffolder.Api/Agent/Tools/WriteFileTool.cs
- [ ] T025 [US1] Implement EventLogService (assign monotonic per-run sequence, append EventEntity to IEventRepository, enforce append-only by rejecting updates; broadcast to EventBroadcaster when wired in Phase 4) in backend/Scaffolder.Api/Persistence/EventLogService.cs
- [ ] T026 [US1] Implement AgentLoopHost (Microsoft Agent Framework loop: evaluate task, call tools from allowlist, receive results, repeat until done or bounded; emit agent.message, tool.call, tool.result, tool.rejected, tool.error events to EventLogService with correct callId correlation per FR-018 and FR-020; terminate loop on content-safety failure) in backend/Scaffolder.Api/Agent/AgentLoopHost.cs
- [ ] T027 [US1] Implement RunStateMachine (queued -> running on start; running -> completed | failed | bounded on terminal condition; emit corresponding lifecycle events run.started, run.completed, run.failed, run.bounded; validate all transitions, reject illegal ones with 409) in backend/Scaffolder.Api/Runs/RunStateMachine.cs
- [ ] T028 [US1] Implement RunExecutionService (orchestrate: create Session via WorktreeService, start AgentLoopHost, drive RunStateMachine transitions, update Run entity on terminal state, call OperationalRecordWriter on completion) in backend/Scaffolder.Api/Runs/RunExecutionService.cs
- [ ] T029 [US1] Implement POST /runs endpoint: validate CreateRunRequest (originatingBranch non-empty, modelSource enum, taskPrompt non-empty, maxSteps >= 1, maxDurationSeconds >= 1); extract submittedBy from auth context (FR-024); persist Run; enqueue RunExecutionService; return 201 with Run body in backend/Scaffolder.Api/Runs/RunsEndpoints.cs
- [ ] T030 [US1] Implement GET /runs/{runId} endpoint (return Run with current status, timestamps, sessionId; 404 if not found) in backend/Scaffolder.Api/Runs/RunsEndpoints.cs
- [ ] T031 [US1] Implement GET /runs/{runId}/diff endpoint (call DiffService; return text/plain unified diff; return 409 if run is not in a state with an available diff) in backend/Scaffolder.Api/Runs/RunsEndpoints.cs

**Checkpoint**: Full US1 functional. POST a run, agent executes in worktree, diff available. Originating branch untouched. Proves core loop.

---

## Phase 4: US2 - Streaming/SSE (Priority: P2)

**Goal**: Live ordered step stream with resumable cursor (lastSeenSequence / SSE Last-Event-ID) for reconnect and replay across process restarts from the durable event log.

**Independent Test**: Start a run; open GET /runs/{runId}/stream from two concurrent clients simultaneously; verify both receive all events in sequence order with no gaps; disconnect one client mid-run and reconnect with Last-Event-ID; verify it receives only events after the cursor and none are missed (SC-006).

- [ ] T032 [US2] Implement SseWriter (format SSE frames: id = sequence, event = RunEvent.type, data = JSON payload; set Content-Type text/event-stream; disable response buffering) in backend/Scaffolder.Api/Streaming/SseWriter.cs
- [ ] T033 [US2] Implement EventBroadcaster (in-process Channel-based fan-out: maintain per-run subscriber list; push new EventEntity to all active SSE connections for a runId) in backend/Scaffolder.Api/Streaming/EventBroadcaster.cs
- [ ] T034 [US2] Implement EventReplayService (read events from IEventRepository where sequence > lastSeenSequence ordered ascending; handle process-restart replay from durable log for any cursor within the retention window per FR-022) in backend/Scaffolder.Api/Streaming/EventReplayService.cs
- [ ] T035 [US2] Implement GET /runs/{runId}/stream SSE endpoint (parse Last-Event-ID header and lastSeenSequence query param; replay historical events via EventReplayService; then stream live events via EventBroadcaster; at-least-once delivery; stream spans full lifecycle through review/merge per FR-023) in backend/Scaffolder.Api/Streaming/StreamEndpoints.cs
- [ ] T036 [US2] Wire EventBroadcaster into EventLogService so every appended event is immediately pushed to live SSE subscribers; update backend/Scaffolder.Api/Persistence/EventLogService.cs and register EventBroadcaster singleton in backend/Scaffolder.Api/Program.cs

**Checkpoint**: SSE stream live from both clients. Reconnect and replay verified. Review/merge events span full lifecycle.

---

## Phase 5: US3 - Model Source Selection (Priority: P3)

**Goal**: Per-run selection from exactly two providers (copilot_sdk, microsoft_foundry); unsupported model sources rejected at 400; provider selection recorded in Run and OperationalRecord.

**Independent Test**: Create two runs, one per provider; verify each uses the selected provider; verify POST /runs with an unknown modelSource value returns 400.

- [ ] T037 [US3] Define IModelSourceAdapter interface (SubmitPromptAsync, ApplyContentSafetyCheckAsync) and ModelSource strict enum (copilot_sdk | microsoft_foundry only) in backend/Scaffolder.Api/Agent/ModelSources/IModelSourceAdapter.cs
- [ ] T038 [US3] Implement CopilotSdkAdapter (GitHub Copilot SDK .NET integration; configure auth from ScaffolderOptions; wire into AgentLoopHost) in backend/Scaffolder.Api/Agent/ModelSources/CopilotSdkAdapter.cs
- [ ] T039 [P] [US3] Implement MicrosoftFoundryAdapter (Microsoft Foundry SDK integration; configure endpoint and key from ScaffolderOptions; wire into AgentLoopHost) in backend/Scaffolder.Api/Agent/ModelSources/MicrosoftFoundryAdapter.cs
- [ ] T040 [US3] Add ModelSourceGuard to GovernancePolicyEngine (validate modelSource enum at run creation; reject with 400 on unknown value; log policyTrace entry: model-source validation pass or reject with timestamp) in backend/Scaffolder.Api/Agent/Governance/GovernancePolicyEngine.cs

**Checkpoint**: Both providers selectable per run. Unsupported values rejected. policyTrace updated for model-source decisions.

---

## Phase 6: US4 - Review + Merge (Priority: P4)

**Goal**: Human-approval gate: approve triggers merge into originating branch; decline leaves branch byte-for-byte unchanged; merge conflict surfaces as merge_conflict status; all outcomes are first-class event log entries.

**Independent Test**: Complete a run (status completed -> awaiting_review); POST /runs/{runId}/review with approve; verify status becomes merged and originating branch contains exactly the run's changes (SC-004). In a separate run POST decline; verify status declined and originating branch is byte-for-byte unchanged (SC-005). Verify a diverged branch surfaces merge_conflict and the branch is not modified.

- [ ] T041 [US4] Implement MergeService (merge worktree into originating branch via LibGit2Sharp or git CLI; detect conflicts and surface merge_conflict without modifying the branch; return merged | conflict | failed result) in backend/Scaffolder.Api/Worktrees/MergeService.cs
- [ ] T042 [US4] Extend RunStateMachine with review/merge transitions (completed -> awaiting_review -> approved | declined; approved -> merged | merge_conflict | failed; declined is terminal; validate that merge is only attempted on approved; emit lifecycle events for each transition) in backend/Scaffolder.Api/Runs/RunStateMachine.cs
- [ ] T043 [US4] Implement POST /runs/{runId}/review endpoint (validate ReviewDecisionRequest; enforce human-approval gate via GovernancePolicyEngine; on approve call MergeService; on decline leave branch unchanged; return updated Run; 409 on invalid state transition) in backend/Scaffolder.Api/Runs/RunsEndpoints.cs
- [ ] T044 [US4] Emit review/merge first-class events via EventLogService: review.requested when run transitions to awaiting_review; review.approved or review.declined on decision; merge.completed or merge.failed on merge outcome; events share the same monotonic sequence and run through merge (FR-023) in backend/Scaffolder.Api/Runs/RunExecutionService.cs

**Checkpoint**: Full four-story backend API complete. Submit, stream, model-source selection, and human-gated merge all functional.

---

## Phase 7: Governance + Responsible AI

**Purpose**: Centralized policy enforcement (FR-027, Principle X); content-safety intercept before any client delivery (FR-025, SC-008); secrets and personal data scrubbing (FR-026, SC-009); explicit run bounds with run.bounded terminal state (FR-029); operational records for compliance (FR-028, SC-010); submitting user identity (FR-024); deployment parity (NFR-001); no-emoji enforcement (NFR-002).

- [ ] T045 Implement GovernancePolicyEngine as the central enforcer called by AgentLoopHost and RunsEndpoints: tool allowlist check (read_file and write_file only), sandbox boundary enforcement (delegate to SandboxPathResolver), human-approval gate validation, run-limit policy; every decision appends a timestamped policyTrace entry to IOperationalRecordRepository (SC-010) in backend/Scaffolder.Api/Agent/Governance/GovernancePolicyEngine.cs
- [ ] T046 [P] Implement ContentSafetyInterceptor (intercept every model output from IModelSourceAdapter before it is written to EventLogService or relayed to any SSE subscriber; call the active provider's content-safety API; on failure: withhold the content, emit run.failed event with failureReason indicating content-safety failure, transition run to failed terminal state; 100% intercept per SC-008) in backend/Scaffolder.Api/Agent/Governance/ContentSafetyInterceptor.cs
- [ ] T047 [P] Implement SecretsScrubbingFilter (scan event payloads, OperationalRecord fields, and API response bodies for secrets, credentials, and personal data patterns before persistence or serialization; redact matches; enforce that no raw secret or personal data reaches any downstream path per FR-026 and SC-009) in backend/Scaffolder.Api/Agent/Governance/SecretsScrubbingFilter.cs
- [ ] T048 Implement RunBoundsEnforcer (track per-run step count increment on every agent loop iteration; check wall-clock elapsed against maxDurationSeconds using a cancellation token; on hitting maxSteps or maxDurationSeconds emit run.bounded event via EventLogService and transition run to bounded terminal state; enforced by GovernancePolicyEngine and not bypassable by any client or tool per FR-029) in backend/Scaffolder.Api/Agent/Governance/RunBoundsEnforcer.cs
- [ ] T049 Implement OperationalRecordWriter (write one OperationalRecord per run after the run reaches a terminal state: submittedBy echoed from Run, modelSource, startedAt, endedAt, stepCount, outcome; policyTrace JSON array containing an entry for every governance decision made by GovernancePolicyEngine during the run; distinct from the event log per FR-028 and research Decision 12) in backend/Scaffolder.Api/Persistence/OperationalRecordWriter.cs
- [ ] T050 [P] NFR-001 deployment parity: abstract database provider via IDbContextFactory so SQLite is used in local developer mode and a hosted relational provider (SQL Server or PostgreSQL) is used in cloud; selection driven by ScaffolderOptions without a dedicated code path per environment in backend/Scaffolder.Api/Persistence/ScaffolderDbContext.cs and backend/Scaffolder.Api/Configuration/ScaffolderOptions.cs
- [ ] T051 [P] NFR-002 no-emoji enforcement: add an automated test that scans all event log payload serializations, API response fixtures, CLI output helpers, and Web UI string constants for any Unicode emoji codepoint; fail the build on any match in tests/Scaffolder.Api.Tests/Unit/NoEmojiAuditTests.cs

**Checkpoint**: All ten constitutional principles enforced. Content-safety, secrets scrubbing, run bounds, operational records, and deployment parity validated. Clients can now be built against a hardened API.

---

## Phase 8: CLI Client

**Purpose**: Thin .NET TUI over the API achieving full submission-watch-review parity with the Web UI (Principles III and IV).

- [ ] T052 Configure Scaffolder.Cli entry point with Spectre.Console app and top-level command routing; add OpenAPI-generated client package reference in clients/cli/Program.cs
- [ ] T053 [P] Generate typed C# HTTP client from contracts/run-api.yaml into clients/cli/ApiClient/ using NSwag or Kiota targeting http://localhost:3000
- [ ] T054 [US1] Implement `run submit` Spectre.Console command (prompt for originatingBranch, taskPrompt, modelSource selection from exactly the two supported providers, optional maxSteps and maxDurationSeconds; POST /runs; display returned runId and status) in clients/cli/Commands/SubmitRunCommand.cs
- [ ] T055 [P] [US1] Implement `run status` Spectre.Console command (GET /runs/{runId}; render Run status, createdAt, startedAt, completedAt, and submittedBy in a Spectre.Console table; no emojis) in clients/cli/Commands/RunStatusCommand.cs
- [ ] T056 [US2] Implement `run watch` Spectre.Console command (connect to GET /runs/{runId}/stream SSE; render each event type as a labelled row with sequence and type; reconnect on disconnect by sending Last-Event-ID; deduplicate re-delivered events by sequence; display terminal lifecycle event and exit) in clients/cli/Commands/WatchRunCommand.cs
- [ ] T057 [US1] Implement `run diff` Spectre.Console command (GET /runs/{runId}/diff; print unified diff text to terminal; display 409 message if diff not available) in clients/cli/Commands/DiffCommand.cs
- [ ] T058 [US4] Implement `run review` Spectre.Console command (prompt for approve or decline and optional comment; POST /runs/{runId}/review; display resulting Run status and mergeResult) in clients/cli/Commands/ReviewCommand.cs

**Checkpoint**: CLI can execute the full submit-watch-diff-review flow. Parity with Web UI verified against SC-003.

---

## Phase 9: Web UI Client

**Purpose**: Thin React 19 + Fluent 2 Web UI over the API achieving full submission-watch-review parity with the CLI (Principles III and IV).

- [ ] T059 Configure Vite project with React 19, @fluentui/react-components, TypeScript strict mode, and path aliases; set API base URL via environment variable in clients/web/vite.config.ts and clients/web/tsconfig.json
- [ ] T060 [P] Generate typed TypeScript API client from contracts/run-api.yaml into clients/web/src/api/ using openapi-typescript or Kiota
- [ ] T061 [US1] Implement SubmitRunPage (Fluent 2 form with: originatingBranch text input, taskPrompt textarea, modelSource dropdown limited to copilot_sdk | microsoft_foundry with no other options, optional maxSteps and maxDurationSeconds inputs; POST /runs on submit; navigate to watch page with returned runId; no emojis) in clients/web/src/pages/SubmitRunPage.tsx
- [ ] T062 [P] [US1] Implement RunStatusBadge Fluent 2 Badge component mapping every RunStatus enum value to a distinct text label (no emojis) in clients/web/src/components/RunStatusBadge.tsx
- [ ] T063 [US2] Implement RunWatchPage (connect to GET /runs/{runId}/stream via browser EventSource; set lastEventId on reconnect; maintain event list in sequence order; deduplicate by sequence; display terminal lifecycle event and mark stream closed) in clients/web/src/pages/RunWatchPage.tsx
- [ ] T064 [P] [US2] Implement EventList Fluent 2 component (render agent.message, tool.call, tool.result, tool.rejected, tool.error, all lifecycle, and review/merge event types each with distinct text label and monospace content; strictly monotonic sequence order; no emojis) in clients/web/src/components/EventList.tsx
- [ ] T065 [US4] Implement ReviewPage (fetch diff via GET /runs/{runId}/diff; render in DiffViewer; Fluent 2 approve and decline action buttons; POST /runs/{runId}/review on action; display resulting RunStatusBadge and merge outcome) in clients/web/src/pages/ReviewPage.tsx
- [ ] T066 [P] [US4] Implement DiffViewer Fluent 2 component (render unified diff with added lines highlighted in a distinct color and removed lines in another; no emojis; accessible color contrast) in clients/web/src/components/DiffViewer.tsx

**Checkpoint**: Web UI can execute the full submit-watch-diff-review flow. Parity with CLI verified against SC-003.

---

## Phase 10: Contract Tests + Integration QA + Polish

**Purpose**: Validate all FRs, NFRs, and SCs through contract tests, integration scenarios, and pre-release compliance checks per quickstart.md.

- [ ] T067 Write contract tests for POST /runs (valid request returns 201 with Run schema, invalid modelSource returns 400, missing required fields return 400) and GET /runs/{runId} (200 Run schema, 404) against contracts/run-api.yaml in tests/Scaffolder.Api.Tests/Contracts/RunEndpointsContractTests.cs
- [ ] T068 [P] Write contract tests for GET /runs/{runId}/stream validating every emitted SSE event body against the RunEvent JSON schema in contracts/run-step-event.schema.json; assert callId is present on all tool event types and absent on non-tool types in tests/Scaffolder.Api.Tests/Contracts/StreamContractTests.cs
- [ ] T069 [P] Write contract tests for POST /runs/{runId}/review (valid approve returns 200 Run, invalid state returns 409) and GET /runs/{runId}/diff (200 text/plain, 409 when not diffable) against contracts/run-api.yaml in tests/Scaffolder.Api.Tests/Contracts/ReviewDiffContractTests.cs
- [ ] T070 [P] Write unit tests for SandboxPathResolver covering: valid relative path succeeds, absolute path rejected with PATH_ESCAPE, single dot-dot segment rejected with PATH_ESCAPE, multi-hop dot-dot rejected with PATH_ESCAPE, symlink resolving outside artifactDir rejected with PATH_ESCAPE (SC-002, FR-007) in tests/Scaffolder.Api.Tests/Unit/SandboxPathResolverTests.cs
- [ ] T071 [P] Write unit tests for GovernancePolicyEngine: tool allowlist allows read_file and write_file only, model-source guard passes copilot_sdk and microsoft_foundry and rejects anything else, run bounds enforcer emits run.bounded at maxSteps and at maxDuration, human-approval gate blocks merge on decline, policyTrace contains a timestamped entry for each tested decision (SC-010) in tests/Scaffolder.Api.Tests/Unit/GovernancePolicyEngineTests.cs
- [ ] T072 Write integration test for the full run lifecycle (queued -> running -> completed -> awaiting_review -> approved -> merged): assert event log contains run.started, at least one agent.message, at least one tool.call with matching tool.result, run.completed, review.requested, review.approved, merge.completed all in monotonically increasing sequence in tests/Scaffolder.Api.Tests/Integration/RunLifecycleIntegrationTests.cs
- [ ] T073 [P] Write integration test for SSE reconnect/replay: start a run, consume first N events, record sequence N, disconnect, reconnect with Last-Event-ID = N, assert server replays only events with sequence > N in order, assert no event is missing from the full sequence, assert client can deduplicate any at-least-once re-delivery (SC-006, FR-021, FR-022) in tests/Scaffolder.Api.Tests/Integration/SseReconnectIntegrationTests.cs
- [ ] T074 [P] Write integration test for content-safety failure: configure a stubbed IModelSourceAdapter that returns a safety-fail signal; submit a run; assert zero safety-failing content appears in any event log payload; assert run ends in failed terminal state; assert event log contains a run.failed event with non-empty failureReason referencing content-safety (SC-008, FR-025) in tests/Scaffolder.Api.Tests/Integration/ContentSafetyIntegrationTests.cs
- [ ] T075 [P] Write integration test for secrets scrubbing: submit a run with an identifiable token string in taskPrompt (e.g. SECRET_TOKEN_TEST_XYZ); let run complete; assert the token string does not appear verbatim in any event log payload field, any diff output, or any OperationalRecord field (SC-009, FR-026) in tests/Scaffolder.Api.Tests/Integration/SecretsScrubIntegrationTests.cs
- [ ] T076 [P] Write integration test for governance traceability: submit a run that exercises one sandbox pass, one PATH_ESCAPE rejection, one model-source validation, and the human-approval gate; after merge inspect OperationalRecord.policyTrace; assert it contains a timestamped entry for each governance decision in the order they occurred; assert a compliance reviewer can reconstruct all policy outcomes from policyTrace without the event log (SC-010, FR-028) in tests/Scaffolder.Api.Tests/Integration/GovernanceTraceIntegrationTests.cs
- [ ] T077 [P] Write integration test for bounded run: submit a run with maxSteps = 1 against a task the agent will not complete in one step; assert run ends in bounded terminal state; assert event log contains a run.bounded event; assert no run.completed event is present (FR-029, research Decision 8) in tests/Scaffolder.Api.Tests/Integration/BoundedRunIntegrationTests.cs
- [ ] T078 [P] Write Vitest + React Testing Library tests for RunWatchPage (mock EventSource, verify events render in sequence order, verify deduplication, verify reconnect triggers lastEventId) and SubmitRunPage (form validation, modelSource dropdown shows exactly two options, successful submit navigates to watch page) in tests/web/RunWatchPage.test.tsx and tests/web/SubmitRunPage.test.tsx
- [ ] T079 NFR-003 pre-release bias review: document a fairness and bias review of every AI-influencing default (default taskPrompt preamble if any, tool allowlist defaults, model-source defaults, and any system prompt injected into the agent loop); record any identified concern and its mitigation plan; gate release on this document existing in specs/001-single-agent-run/checklists/bias-review.md
- [ ] T080 Run all quickstart.md validation scenarios end-to-end from both CLI and Web UI: Scenario 1 (submit and complete), Scenario 2 (SSE watch and reconnect), Scenario 3 (model source selection), Scenario 4 (review and merge + decline + conflict path), plus all named checks (sandbox, bounded-run, content-safety failure, no-secrets-in-outputs, governance traceability); confirm all pass criteria from quickstart.md (SC-001 through SC-010)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 Setup**: No dependencies. Start immediately.
- **Phase 2 Foundation**: Depends on Phase 1. BLOCKS all user stories and governance phases.
- **Phase 3 US1 Agent Loop + Event Log**: Depends on Phase 2. Core MVP. Blocks Phases 4, 5, 6, 7.
- **Phase 4 US2 Streaming/SSE**: Depends on Phase 3 (EventLogService and AgentLoopHost must exist to broadcast events).
- **Phase 5 US3 Model Source Selection**: Depends on Phase 3 (AgentLoopHost must exist to accept adapter injection). Can run in parallel with Phase 4.
- **Phase 6 US4 Review + Merge**: Depends on Phase 3 (RunStateMachine and EventLogService must exist). Can run in parallel with Phases 4 and 5.
- **Phase 7 Governance + Responsible AI**: Depends on Phase 3 (GovernancePolicyEngine integrates into AgentLoopHost and RunsEndpoints). Should complete before Phases 8 and 9 to avoid rework.
- **Phase 8 CLI Client**: Depends on the API surface from Phases 3 to 7 being stable.
- **Phase 9 Web UI Client**: Depends on the API surface from Phases 3 to 7 being stable. Can run in parallel with Phase 8.
- **Phase 10 Contract Tests + Integration QA**: Depends on all preceding phases. Final validation gate before release.

### User Story Dependencies

- **US1 (P1)**: Can start immediately after Phase 2. No dependency on US2, US3, or US4.
- **US2 (P2)**: Depends on US1 (EventLogService from T025 must exist). Can be implemented in parallel with US3 and US4 after T025 completes.
- **US3 (P3)**: Depends on US1 (AgentLoopHost from T026 must exist to accept an adapter). Can run in parallel with US2.
- **US4 (P4)**: Depends on US1 (RunStateMachine from T027 must exist). Can run in parallel with US2 and US3.

### Within Each Phase

- Tasks marked [P] touch different files and have no intra-phase blocking dependencies; launch them together.
- Entity tasks (T008 to T013) are all [P] and can be generated simultaneously.
- SandboxPathResolver (T022) must complete before ReadFileTool (T023) and WriteFileTool (T024).
- EventLogService (T025) must complete before AgentLoopHost (T026) and EventBroadcaster wiring (T036).
- RunStateMachine (T027) must complete before RunExecutionService (T028) and review extensions (T042).
- GovernancePolicyEngine (T045) must complete before ContentSafetyInterceptor (T046), SecretsScrubbingFilter (T047), and RunBoundsEnforcer (T048).
- All API endpoints (T029 to T031, T035, T043) must complete before CLI commands (T054 to T058) and Web UI pages (T061 to T066).

### Parallel Execution Examples

```text
Phase 2 entity parallel group (launch together):
  T008 RunEntity, T009 SessionEntity, T010 EventEntity,
  T011 ToolOperationEntity, T012 ReviewDecision, T013 OperationalRecord

Phase 3 tools parallel group (after T022 completes):
  T023 ReadFileTool, T024 WriteFileTool

Phase 5 adapter parallel group:
  T038 CopilotSdkAdapter, T039 MicrosoftFoundryAdapter

Phase 7 interceptor parallel group (after T045 GovernancePolicyEngine completes):
  T046 ContentSafetyInterceptor, T047 SecretsScrubbingFilter

Phase 8 and Phase 9 (after Phase 7 API is stable):
  Phase 8 CLI client, Phase 9 Web UI client (run together across stories)

Phase 10 parallel test group (after all implementation phases complete):
  T067, T068, T069 (contract tests)
  T070, T071 (unit tests)
  T073, T074, T075, T076, T077 (integration tests)
  T078 (web UI tests)
```

---

## Implementation Strategy

### MVP First (US1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundation (CRITICAL - blocks all stories)
3. Complete Phase 3: US1 Agent Loop + Event Log
4. **STOP and VALIDATE**: POST a run; confirm worktree created, agent runs, diff available, originating branch unmodified
5. **MVP demonstrated**: backend proves the core agent loop works end-to-end

### Incremental Delivery

1. Phase 1 + Phase 2: Persistence and infrastructure ready
2. Phase 3 (US1): Core agent loop, worktree, diff, basic run endpoints - **deploy and demo**
3. Phase 4 (US2): Add live SSE step stream and reconnect - **deploy and demo**
4. Phase 5 (US3): Add model source selection and adapter switching - **deploy and demo**
5. Phase 6 (US4): Add human review and merge - **deploy and demo**
6. Phase 7: Harden governance, content-safety, secrets, bounds, operational records
7. Phase 8 + Phase 9 (parallel): Build CLI and Web UI clients to full parity
8. Phase 10: Contract tests, integration tests, compliance checks, pre-release validation

### Parallel Team Strategy

With multiple developers (after Phase 2 completes):

- Developer A: Phase 3 (US1 Agent Loop) - owns the critical path
- Developer B: Phase 5 stubs (US3 adapter interfaces) - can stub IModelSourceAdapter early
- Developer C: Phase 9 shell (Web UI with mock API) - can build pages against contract stubs
- All reconvene after Phase 3 to integrate adapters, SSE, review, and governance
- QA / Phase 10 runs as a dedicated gate after all implementation phases

---

## Notes

- Task IDs are sequential in dependency order. [P] = safely parallelizable within the phase (different files, no intra-phase blocking dependency).
- [US1] through [US4] labels map implementation tasks to user stories for traceability and independent validation.
- No [US] label on Setup (Phase 1), Foundation (Phase 2), Governance (Phase 7), and QA (Phase 10) phases; these are cross-cutting.
- No emojis anywhere in code, UI, output, logs, generated documentation, or commit messages (Principle VII, NFR-002).
- Constitution v1.1.0 ten-principle check must pass at every PR gate; deviations require a Complexity Tracking entry in plan.md.
- Provider credentials and run-root directory are configuration, not code; no secrets committed to source.
- SSE delivery is at-least-once; all clients must deduplicate by (runId, sequence) regardless of transport.
- The OperationalRecord (Phase 7) is intentionally distinct from the event log; do not merge them.
- The append-only invariant on the Event log (T010, T016, T025) must never be broken by any future change.
