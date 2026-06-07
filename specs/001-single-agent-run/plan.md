# Implementation Plan: Single-Agent File-Editing Run

**Branch**: `001-single-agent-run` | **Date**: 2026-06-07 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-single-agent-run/spec.md`

## Summary

Deliver the first product slice: a single AI agent that runs an agent loop to
complete a natural-language file-editing task. A run is created from a
user-chosen originating branch and executes inside its own session whose
artifact directory is a dedicated git worktree. The agent has exactly two
sandboxed tools (read-file, write-file) confined to that directory. Each run
streams its steps (agent messages, tool calls, tool results) live to any client.
On completion the worktree diff against the originating branch is the output; a
human reviews and, on approval, the worktree merges back.

Technical approach (from research): one Microsoft Agent Framework loop on .NET
hosts the agent and provider adapters (GitHub Copilot SDK or Microsoft Foundry,
selectable per run). The authoritative ASP.NET Core API exposes run creation,
status, an SSE step stream, diff retrieval, and a human review endpoint. Two
thin clients consume the OpenAPI contract: a .NET CLI/TUI and a React 19 +
Fluent 2 Web UI. File-tool sandboxing is enforced with canonical-path
resolution that denies absolute paths, `..` traversal, and symlink escapes.

## Technical Context

**Language/Version**: C# / .NET 9 (backend, agent runtime, CLI); TypeScript 5.x
+ React 19 (Web UI)

**Primary Dependencies**: Microsoft Agent Framework (.NET) for the agent loop;
GitHub Copilot SDK (.NET) and Microsoft Foundry SDK as the two model-source
adapters; ASP.NET Core (Minimal API) for the authoritative API and SSE; LibGit2Sharp
or `git` CLI for worktree/diff/merge lifecycle; Spectre.Console for the CLI/TUI;
React 19 + Fluent 2 (`@fluentui/react-components`) + Vite for the Web UI

**Storage**: Relational store for Run/Session/Event/ToolOperation/ReviewDecision/OperationalRecord
(SQLite for local-developer parity, provider-swappable to a hosted SQL engine for
cloud); the per-run Event log is append-only and serves reconnect/replay for clients
(FR-022); the OperationalRecord is a separate store for debugging, compliance, and
capacity analysis, distinct from the event log (FR-028); git worktrees on the
filesystem under a configured run-root for artifact directories and diffs

**Testing**: xUnit for .NET unit/integration/contract tests; Vitest +
React Testing Library for the Web UI; contract tests validate the API against
`contracts/run-api.yaml` and step payloads against
`contracts/run-step-event.schema.json`

**Target Platform**: Cross-platform .NET host runnable on a developer machine and
as a hosted cloud service (Principle VI); modern browsers for the Web UI

**Project Type**: Web application (authoritative backend API + two clients)

**Performance Goals**: First streamed step visible in under 10 seconds after
submission under normal conditions (SC-001); ordered, no-gap step delivery including
after disconnect-and-reconnect (SC-006); 100% of content-safety-failing model outputs
withheld from clients and recorded in the event log (SC-008); zero secrets,
credentials, or personal data in any event log payload, client output, or operational
record (SC-009); every governance policy decision traceable in the operational record
for any run within the retention window (SC-010)

**Constraints**: 100% rejection of file operations escaping the artifact directory
with zero out-of-sandbox I/O (SC-002, FR-006/FR-007); every run bounded by explicit,
policy-enforced max step count and max wall-clock duration ending in a visible terminal
state (FR-029); CLI and Web UI at full parity over the API (FR-012, Principle IV);
no emojis in any product output, code, logs, or generated docs (Principle VII,
NFR-002); content-safety checks applied before relaying any model output to clients,
with failures withheld, logged, and the run terminated in a visible failure state
(FR-025, SC-008); secrets and personal data excluded from event log payloads, client
outputs, and operational records (FR-026, SC-009); all governance policies (tool
allowlist, model-source enum, sandbox boundary, human-approval gate, run limits)
enforced uniformly by the Agent Framework governance layer regardless of client
(FR-027, SC-010); submitting user identity recorded as the named human accountable
for every run (FR-024); identical behavior in local and cloud deployments using the
same build (NFR-001); all AI-influencing defaults reviewed for fairness and bias
concerns before release (NFR-003)

**Scale/Scope**: First testing slice - one agent, two tools, concurrent runs each
in an isolated worktree; breadth (multiple agents, boards, more tools) intentionally
deferred

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Status | How this plan satisfies it |
|---|-----------|--------|----------------------------|
| I | Agent Runtime (Microsoft Agent Framework, .NET) | PASS | Single Agent Framework loop on .NET hosts the agent; no alternative runtime or ad hoc control flow (research Decision 1; FR-001/FR-010) |
| II | Model Sources (Copilot SDK or Microsoft Foundry, per run) | PASS | `modelSource` strict enum `copilot_sdk \| microsoft_foundry` behind one adapter interface; selectable per run; all else rejected (research Decision 6; FR-009) |
| III | API-First (backend is single source of truth) | PASS | ASP.NET Core API owns the agent loop, tasks, and step stream; clients are thin and generated from the OpenAPI contract (FR-017; `contracts/run-api.yaml`) |
| IV | Two Front-Ends at Parity (CLI/TUI + React 19/Fluent 2 Web UI) | PASS | .NET CLI/TUI and React 19 + Fluent 2 Web UI both consume the full API surface; submit-watch-review available in each (FR-012; SC-003) |
| V | Observable Runs (streamed steps) | PASS | SSE endpoint streams ordered agent messages, tool calls, tool results with monotonic `sequence` and reconnect support (research Decision 5; FR-011) |
| VI | Deployment Parity (Local and Cloud) | PASS | Single .NET build runs on a developer machine and as a hosted service; storage abstracted (SQLite local / hosted SQL); run-root configurable |
| VII | No Emojis | PASS | No emojis in product code, UI, output, logs, or generated docs; enforced in review |
| VIII | Responsible AI | PASS | Submitting user identity recorded as the named human accountable for every run (FR-024). Content-safety checks gate every model output before client delivery; failures withheld, logged as a `run.failed` event, and the run terminated in a visible failure state (FR-025, SC-008). Secrets and personal data excluded from event log payloads, client outputs, and operational records (FR-026, SC-009). All AI actions transparent through the typed, append-only event log (FR-011, FR-022). No harmful or copyright-infringing content. All AI-influencing defaults reviewed for fairness and bias before release (NFR-003). Human approval required before any irreversible action (FR-015/FR-016, Principle IX). |
| IX | Safe Execution | PASS | File ops confined to the per-run worktree sandbox with 100% rejection of out-of-sandbox I/O (SC-002, FR-006/FR-007). Every run subject to explicit, policy-enforced max step count and max wall-clock duration; reaching either emits `run.bounded` ending in a visible terminal state (FR-029). Destructive actions (merge) require explicit human approval (FR-015/FR-016). Full audit trail in the durable append-only event log: typed events, callId correlation, resumable cursor (lastSeenSequence / SSE Last-Event-ID), and retention window spanning through merge (FR-022, FR-023). |
| X | Agent Governance Toolkit (.NET) | PASS | All governance policies (tool allowlist, model-source enum, sandbox boundary, human-approval gate, run limits) enforced by the Agent Framework governance layer and applied uniformly regardless of which client initiates the run (FR-027). Structured telemetry and the append-only event log make every run observable and auditable. A separate OperationalRecord (distinct from the event log) captures submitting user, model source, step count, and outcome for compliance and capacity analysis; every policy decision produces a traceable entry (FR-028, SC-010; research Decisions 1, 5, 6, 11, 12). |

**Initial gate result**: PASS - no violations; Complexity Tracking not required.

**Post-Design re-check (after Phase 1)**: PASS - data-model, contracts, and updated
plan artifacts (constitution v1.1.0, ten-principle check) keep the API authoritative,
the model-source enum strict, sandboxing explicit, and streaming ordered. Run entity
carries submitting user identity (FR-024); OperationalRecord entity is distinct from
the event log and captures compliance-grade data (FR-028); Event entity is durable,
append-only, and typed with callId correlation and resumable cursor (FR-022, FR-023).
Agent/Governance/ centralizes all policy enforcement (FR-027, SC-010). All ten
principles: PASS. Complexity Tracking remains empty.

## Project Structure

### Documentation (this feature)

```text
specs/001-single-agent-run/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── run-api.yaml                 # OpenAPI 3.1 API contract
│   └── run-step-event.schema.json   # SSE step payload JSON Schema
├── checklists/
│   └── requirements.md  # Spec quality checklist
└── tasks.md             # Phase 2 output (/speckit.tasks - NOT created here)
```

### Source Code (repository root)

```text
backend/
└── Scaffolder.Api/                 # ASP.NET Core authoritative API
    ├── Runs/                       # run create/get/diff/review endpoints
    ├── Streaming/                  # SSE step stream + reconnect/replay (FR-021, FR-022)
    ├── Agent/                      # Microsoft Agent Framework loop host
    │   ├── Tools/                  # read_file / write_file sandboxed tools
    │   ├── ModelSources/           # copilot_sdk + microsoft_foundry adapters
    │   └── Governance/             # policy enforcement: tool allowlist, model-source guard, run limits, content-safety (FR-027, FR-029)
    ├── Worktrees/                  # git worktree lifecycle + diff + merge
    ├── Persistence/                # Run/Session/Event/ToolOperation/ReviewDecision/OperationalRecord
    └── Program.cs

clients/
├── cli/                            # Scaffolder.Cli (.NET, Spectre.Console TUI)
│   └── ApiClient/                  # OpenAPI-generated client
└── web/                            # React 19 + Fluent 2 (Vite) Web UI
    └── src/
        ├── components/
        ├── pages/
        └── api/                    # OpenAPI-generated client

tests/
├── Scaffolder.Api.Tests/           # xUnit unit + integration + contract tests
└── web/                            # Vitest + React Testing Library
```

**Structure Decision**: Web-application layout. A single authoritative .NET
backend (`backend/Scaffolder.Api`) hosts the Agent Framework loop, the two model
adapters, the worktree lifecycle, persistence, and both REST and SSE endpoints.
Two thin clients live under `clients/` - a .NET CLI/TUI and a React 19 + Fluent 2
Web UI - each consuming the same OpenAPI contract so neither holds business logic.
This satisfies Principles III, IV, and VI directly.

## Complexity Tracking

> No constitution violations. No entries required.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| (none)    | -          | -                                   |
