---
description: "Task list for Single-Agent File-Editing Run implementation"
---

# Tasks: Single-Agent File-Editing Run

**Input**: Design documents from `/specs/001-single-agent-run/`

**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/

**Tests**: Test tasks ARE included. plan.md explicitly specifies Vitest (unit/integration), Playwright (Web UI flows), and contract tests against OpenAPI; quickstart.md defines validation scenarios that map to integration/e2e tests.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)
- Include exact file paths in descriptions

## Agent Assignments

Task annotations use `<!-- squad:agent=<name> tier=<tier> -->`. Names are the
cast members from `.squad/team.md` (universe: The Matrix). Mapping to the
`squad.config.ts` role IDs:

| Cast Name | Role ID (squad.config.ts) | Role |
|-----------|---------------------------|------|
| tank | backend-engineer | Backend Engineer |
| morpheus | runtime-engineer | Runtime Engineer |
| trinity | frontend-engineer | Frontend Engineer |
| smith | qa-engineer | QA Engineer |
| link | platform-engineer | Platform Engineer |

## Path Conventions

Monorepo structure (per plan.md):

- Backend API: `apps/api/`
- CLI client: `apps/cli/`
- Web client: `apps/web/`
- Shared packages: `packages/agent-runtime/`, `packages/sandbox-fs/`, `packages/run-domain/`, `packages/api-contracts/`
- Tests: `tests/contract/`, `tests/integration/`, `tests/e2e/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Monorepo initialization and shared tooling

- [ ] T001 Create monorepo workspace structure (apps/api, apps/cli, apps/web, packages/agent-runtime, packages/sandbox-fs, packages/run-domain, packages/api-contracts, tests/contract, tests/integration, tests/e2e) with root package.json workspaces config per plan.md
  <!-- squad:agent=link tier=lightweight -->
- [ ] T002 Initialize TypeScript 5.x + Node 22 LTS toolchain: root tsconfig.json base config, per-workspace tsconfig.json, and shared build scripts
  <!-- squad:agent=link tier=standard -->
- [ ] T003 [P] Install and pin core dependencies (Microsoft Agent Framework, Fastify, simple-git, React 19 + Fluent 2, Ink, Zod, better-sqlite3) across the appropriate workspaces
  <!-- squad:agent=link tier=lightweight -->
- [ ] T004 [P] Configure linting and formatting (ESLint + Prettier) with a no-emoji lint rule for shipped surfaces per Constitution VII in repo root config
  <!-- squad:agent=link tier=lightweight -->
- [ ] T005 [P] Configure Vitest, Playwright, and contract-test runners with root scripts (test, test:contract, test:integration, test:e2e)
  <!-- squad:agent=smith tier=standard -->

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core domain, contracts, persistence, and sandbox primitives that ALL user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [ ] T006 [P] Generate api-contracts types and Zod validators from contracts/run-api.yaml and contracts/run-step-event.schema.json into packages/api-contracts/src/
  <!-- squad:agent=tank tier=standard -->
- [ ] T007 [P] Implement shared domain models (Run, Session, Step, ToolOperation, ReviewDecision) with Zod schemas in packages/run-domain/src/models/ per data-model.md
  <!-- squad:agent=morpheus tier=standard -->
- [ ] T008 [US-shared] Implement Run state machine (queued → running → completed/failed/bounded → awaiting_review → approved/declined → merged/merge_conflict) with transition guards in packages/run-domain/src/state-machine.ts
  <!-- squad:agent=morpheus tier=full -->
- [ ] T009 [P] Implement sandbox path guard (realpath + symlink resolution + absolute/`..` escape rejection, errorCode mapping PATH_ESCAPE/NOT_FOUND/PERMISSION/UNKNOWN) in packages/sandbox-fs/src/path-guard.ts per research Decision 3
  <!-- squad:agent=morpheus tier=full -->
- [ ] T010 Setup SQLite persistence layer and schema/migrations for runs, sessions, steps, tool_operations, review_decisions in apps/api/src/db/ (depends on T007)
  <!-- squad:agent=tank tier=standard -->
- [ ] T011 [P] Configure Fastify app bootstrap, routing skeleton, error handling, structured logging, and environment/config management (run-root path, provider credentials) in apps/api/src/server.ts and apps/api/src/config.ts
  <!-- squad:agent=tank tier=standard -->
- [ ] T012 [P] Implement step repository with monotonic per-run `sequence` allocation and ordered persistence in apps/api/src/db/step-repository.ts (depends on T010)
  <!-- squad:agent=tank tier=standard -->
- [ ] T013 [P] Define provider adapter interface (single runtime boundary for copilot_sdk and microsoft_foundry) in packages/agent-runtime/src/providers/provider.ts per research Decision 6
  <!-- squad:agent=morpheus tier=full -->

**Checkpoint**: Foundation ready — user story implementation can now begin

---

## Phase 3: User Story 1 - Submit a task and get a file-editing result (Priority: P1) 🎯 MVP

**Goal**: A user submits a natural-language task against an originating branch; an isolated worktree-backed run executes the agent loop with sandboxed read/write tools and produces file changes contained entirely within the run's working area.

**Independent Test**: Submit a task against a known branch; confirm a run completes and produces file changes contained entirely within the run's own working area, with no modification to the originating branch.

### Tests for User Story 1 ⚠️

- [ ] T014 [P] [US1] Contract test for POST /runs (CreateRunRequest validation, 201 Run response, 400 invalid) in tests/contract/runs-create.contract.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T015 [P] [US1] Contract test for GET /runs/{runId} (200 Run, 404) in tests/contract/runs-get.contract.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T016 [P] [US1] Integration test for full run lifecycle producing isolated diff with no originating-branch mutation in tests/integration/run-lifecycle.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T017 [P] [US1] Integration test for sandbox path rejection (absolute, `..`, symlink escape) returning rejected tool result without external file access in tests/integration/sandbox-rejection.test.ts
  <!-- squad:agent=smith tier=standard -->

### Implementation for User Story 1

- [ ] T018 [P] [US1] Implement git worktree lifecycle manager (create worktree from originating branch/commit, resolve artifactDir under run-root, retain on failure) in apps/api/src/git/worktree-manager.ts per research Decision 4
  <!-- squad:agent=tank tier=full -->
- [ ] T019 [P] [US1] Implement read_file and write_file sandboxed tools using packages/sandbox-fs path guard in packages/sandbox-fs/src/tools.ts (depends on T009)
  <!-- squad:agent=morpheus tier=standard -->
- [ ] T020 [US1] Implement agent loop runtime (plan → tool call → tool result → continue/finish) wiring tools and provider adapter in packages/agent-runtime/src/agent-loop.ts (depends on T013, T019)
  <!-- squad:agent=morpheus tier=full -->
- [ ] T021 [US1] Implement run orchestrator service (create Run + Session, provision worktree, start agent loop, persist steps and tool operations, drive state machine to terminal) in apps/api/src/services/run-orchestrator.ts (depends on T008, T010, T012, T018, T020)
  <!-- squad:agent=tank tier=full -->
- [ ] T022 [US1] Implement POST /runs endpoint (validate CreateRunRequest, create run, return 201) in apps/api/src/routes/runs.ts (depends on T021)
  <!-- squad:agent=tank tier=standard -->
- [ ] T023 [US1] Implement GET /runs/{runId} endpoint (return run status, 404 when missing) in apps/api/src/routes/runs.ts
  <!-- squad:agent=tank tier=standard -->
- [ ] T024 [US1] Implement GET /runs/{runId}/diff endpoint (worktree diff vs originating branch, 409 when no diff available) in apps/api/src/routes/runs.ts (depends on T018, T021)
  <!-- squad:agent=tank tier=standard -->
- [ ] T025 [US1] Add validation, error handling, and failure-reason capture for run creation and tool operations in apps/api/src/services/run-orchestrator.ts
  <!-- squad:agent=tank tier=standard -->

**Checkpoint**: User Story 1 fully functional — a submitted task runs end-to-end and yields an isolated diff. MVP deliverable.

---

## Phase 4: User Story 2 - Watch a run's steps live (Priority: P2)

**Goal**: Users watch an ordered live stream of agent messages, tool calls, and tool results from CLI and Web clients, ending when the run completes, with reconnect support.

**Independent Test**: Start a run and confirm that, from either client, the user sees an ordered live stream of agent messages, tool calls, and tool results as they occur, ending when the run completes.

### Tests for User Story 2 ⚠️

- [ ] T026 [P] [US2] Contract test for GET /runs/{runId}/stream SSE payloads validating RunStepEvent schema in tests/contract/runs-stream.contract.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T027 [P] [US2] Integration test for ordered monotonic streaming with no missing steps and reconnect via Last-Event-ID in tests/integration/stream-ordering.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T028 [P] [US2] E2E test for cross-client parity (CLI + Web observe identical ordered stream and terminal status) in tests/e2e/cross-client-parity.e2e.test.ts
  <!-- squad:agent=smith tier=standard -->

### Implementation for User Story 2

- [ ] T029 [P] [US2] Implement in-process step event bus / pub-sub for live fan-out to multiple watchers in apps/api/src/services/step-stream.ts
  <!-- squad:agent=tank tier=standard -->
- [ ] T030 [US2] Implement GET /runs/{runId}/stream SSE endpoint with monotonic sequence and Last-Event-ID reconnect/backfill in apps/api/src/routes/stream.ts (depends on T012, T029) per research Decision 5
  <!-- squad:agent=tank tier=full -->
- [ ] T031 [US2] Emit step events to the stream bus from the run orchestrator as agent messages, tool calls, tool results, and lifecycle steps are persisted in apps/api/src/services/run-orchestrator.ts (depends on T021, T029)
  <!-- squad:agent=tank tier=standard -->
- [ ] T032 [P] [US2] Implement CLI submit-and-watch experience (Ink TUI: submit task, render ordered live steps, show terminal state) in apps/cli/src/watch.tsx (depends on T030)
  <!-- squad:agent=trinity tier=standard -->
- [ ] T033 [P] [US2] Implement Web submit-and-watch view (React 19 + Fluent 2: submit task, render ordered live steps via SSE, show terminal state) in apps/web/src/views/RunWatch.tsx (depends on T030)
  <!-- squad:agent=trinity tier=standard -->

**Checkpoint**: User Stories 1 AND 2 work independently — runs are observable live from both clients.

---

## Phase 5: User Story 3 - Choose the model source for a run (Priority: P3)

**Goal**: At submission the user selects exactly one of two supported providers (copilot_sdk, microsoft_foundry); the run records and uses it, and unsupported sources are rejected.

**Independent Test**: Submit two runs choosing a different provider for each; confirm each run records and uses the provider the user selected; confirm an unsupported source is rejected.

### Tests for User Story 3 ⚠️

- [ ] T034 [P] [US3] Contract test asserting modelSource accepts only copilot_sdk/microsoft_foundry and rejects other values at POST /runs in tests/contract/model-source.contract.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T035 [P] [US3] Integration test verifying selected provider is recorded and the matching adapter is invoked per run in tests/integration/model-source-selection.test.ts
  <!-- squad:agent=smith tier=standard -->

### Implementation for User Story 3

- [ ] T036 [P] [US3] Implement GitHub Copilot SDK provider adapter behind the provider interface in packages/agent-runtime/src/providers/copilot-sdk.ts (depends on T013)
  <!-- squad:agent=morpheus tier=standard -->
- [ ] T037 [P] [US3] Implement Microsoft Foundry provider adapter behind the provider interface in packages/agent-runtime/src/providers/microsoft-foundry.ts (depends on T013)
  <!-- squad:agent=morpheus tier=standard -->
- [ ] T038 [US3] Implement provider selection/factory resolving modelSource enum to adapter and rejecting unsupported sources in packages/agent-runtime/src/providers/factory.ts (depends on T036, T037)
  <!-- squad:agent=morpheus tier=standard -->
- [ ] T039 [US3] Wire provider selection into run orchestrator and persist recorded provider on the run in apps/api/src/services/run-orchestrator.ts (depends on T038)
  <!-- squad:agent=tank tier=standard -->
- [ ] T040 [P] [US3] Add model-source selector to CLI submission flow in apps/cli/src/watch.tsx
  <!-- squad:agent=trinity tier=standard -->
- [ ] T041 [P] [US3] Add model-source selector to Web submission flow in apps/web/src/views/RunWatch.tsx
  <!-- squad:agent=trinity tier=standard -->

**Checkpoint**: All three core stories independently functional with per-run provider selection.

---

## Phase 6: User Story 4 - Review and approve the merge back (Priority: P4)

**Goal**: A human reviews a completed run's diff and approves or declines; approval merges the worktree back into the originating branch (surfacing conflicts without mutating the branch), decline leaves the originating branch unchanged.

**Independent Test**: Complete a run, review its changes, approve, and confirm the originating branch contains exactly those changes; in a separate run, decline and confirm the originating branch is unchanged.

### Tests for User Story 4 ⚠️

- [ ] T042 [P] [US4] Contract test for POST /runs/{runId}/review (approve/decline, 200 Run, 409 invalid state) in tests/contract/runs-review.contract.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T043 [P] [US4] Integration test for approve → merge produces exact reviewed changes on originating branch in tests/integration/review-approve-merge.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T044 [P] [US4] Integration test for decline leaves originating branch byte-for-byte unchanged and retains worktree in tests/integration/review-decline.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T045 [P] [US4] Integration test for divergent-branch merge conflict surfaced without branch mutation in tests/integration/review-merge-conflict.test.ts
  <!-- squad:agent=smith tier=standard -->

### Implementation for User Story 4

- [ ] T046 [US4] Implement merge-back service (merge worktree to originating branch on approve, detect conflicts, set mergeResult and run status without mutating branch on conflict) in apps/api/src/git/merge-service.ts (depends on T018) per research Decision 7
  <!-- squad:agent=tank tier=full -->
- [ ] T047 [US4] Implement review decision service persisting ReviewDecision, enforcing one decision per run, and driving awaiting_review → approved/declined → merged/merge_conflict in apps/api/src/services/review-service.ts (depends on T008, T046)
  <!-- squad:agent=tank tier=standard -->
- [ ] T048 [US4] Implement POST /runs/{runId}/review endpoint (approve/decline, 200 Run, 409 invalid transition) in apps/api/src/routes/review.ts (depends on T047)
  <!-- squad:agent=tank tier=standard -->
- [ ] T049 [P] [US4] Add diff review and approve/decline actions to CLI in apps/cli/src/review.tsx (depends on T048)
  <!-- squad:agent=trinity tier=standard -->
- [ ] T050 [P] [US4] Add diff review and approve/decline actions to Web in apps/web/src/views/RunReview.tsx (depends on T048)
  <!-- squad:agent=trinity tier=standard -->

**Checkpoint**: All four user stories independently functional — full submit → watch → select provider → review → merge flow complete from CLI and Web.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Hardening, bounds enforcement, and validation across all stories

- [ ] T051 [P] Implement run bounds enforcement (maxSteps + maxDurationSeconds → bounded terminal state) in apps/api/src/services/run-orchestrator.ts per research Decision 8
  <!-- squad:agent=tank tier=standard -->
- [ ] T052 [P] Handle provider-failure-mid-run by ending run in visible failed terminal state in packages/agent-runtime/src/agent-loop.ts
  <!-- squad:agent=morpheus tier=standard -->
- [ ] T053 [P] Add unit tests for state machine transitions and path guard edge cases in tests/unit/
  <!-- squad:agent=smith tier=standard -->
- [ ] T054 [P] Verify SC-001 (first streamed step < 10s) with a performance smoke test in tests/integration/first-step-latency.test.ts
  <!-- squad:agent=smith tier=standard -->
- [ ] T055 [P] Documentation updates (README, run-root config, provider setup) in docs/
  <!-- squad:agent=link tier=lightweight -->
  <!-- squad:note preferred owner 'scribe' is not active in squad.config.ts; routed to nearest active agent (developer-experience). -->
- [ ] T056 Execute quickstart.md validation scenarios end-to-end and record results
  <!-- squad:agent=smith tier=standard -->

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately
- **Foundational (Phase 2)**: Depends on Setup — BLOCKS all user stories
- **User Stories (Phase 3-6)**: All depend on Foundational completion
  - US1 (P1) has no dependency on other stories
  - US2 (P2) consumes the step persistence and orchestrator from US1
  - US3 (P3) layers provider selection onto the orchestrator (US1)
  - US4 (P4) depends on a completed run with diff output (US1)
- **Polish (Phase 7)**: Depends on the targeted user stories being complete

### User Story Dependencies

- **US1 (P1)**: Foundational only — independently testable MVP
- **US2 (P2)**: Foundational + US1 orchestrator/step persistence; independently testable via live stream
- **US3 (P3)**: Foundational + US1 orchestrator; independently testable via provider recording
- **US4 (P4)**: Foundational + US1 completed-run diff; independently testable via approve/decline

### Within Each User Story

- Tests written and failing before implementation
- Models/guards before services; services before endpoints; endpoints before clients
- Core implementation before integration

### Parallel Opportunities

- Setup: T003, T004, T005 in parallel
- Foundational: T006, T007, T009, T011, T013 in parallel; T012 parallel after T010
- US1 tests T014-T017 in parallel; T018 and T019 in parallel before T020
- US2 tests T026-T028 in parallel; clients T032 and T033 in parallel
- US3 adapters T036 and T037 in parallel; clients T040 and T041 in parallel
- US4 tests T042-T045 in parallel; clients T049 and T050 in parallel
- Once Foundational completes, US1-US4 can be staffed in parallel by different developers

---

## Parallel Example: User Story 1

```bash
# Launch all US1 tests together:
Task: "Contract test for POST /runs in tests/contract/runs-create.contract.test.ts"
Task: "Contract test for GET /runs/{runId} in tests/contract/runs-get.contract.test.ts"
Task: "Integration test for run lifecycle in tests/integration/run-lifecycle.test.ts"
Task: "Integration test for sandbox rejection in tests/integration/sandbox-rejection.test.ts"

# Launch independent US1 implementation pieces together:
Task: "Worktree lifecycle manager in apps/api/src/git/worktree-manager.ts"
Task: "Sandboxed read/write tools in packages/sandbox-fs/src/tools.ts"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks all stories)
3. Complete Phase 3: User Story 1
4. STOP and VALIDATE: submit a task, confirm isolated diff with no branch mutation
5. Deploy/demo MVP

### Incremental Delivery

1. Setup + Foundational → foundation ready
2. US1 → isolated file-editing run (MVP)
3. US2 → live observability from both clients
4. US3 → per-run provider selection
5. US4 → human review + merge-back

### Parallel Team Strategy

After Foundational completes: Developer A on US1, B on US2, C on US3, D on US4, integrating independently.

---

## Notes

- [P] = different files, no dependencies
- [Story] label maps each task to its user story for traceability
- Verify tests fail before implementing
- No emojis in shipped product surfaces (Constitution VII)
- Backend API remains authoritative; CLI and Web stay thin (Constitution III/IV)
