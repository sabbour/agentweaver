# Implementation Plan: Single-Agent File-Editing Run

**Branch**: `001-single-agent-run` | **Date**: 2026-06-07 | **Spec**: `specs/001-single-agent-run/spec.md`

**Input**: Feature specification from `/specs/001-single-agent-run/spec.md`

## Summary

Deliver the first vertical slice of Scaffolder: a single Microsoft Agent Framework loop that runs against an isolated git worktree per run, exposes only sandboxed read/write file tools, streams all run steps live to CLI and Web clients through a single backend API, and supports human review/approval before merge back to the originating branch.

## Technical Context

**Language/Version**: TypeScript 5.x on Node.js 22 LTS

**Primary Dependencies**:
- Microsoft Agent Framework (agent loop runtime)
- Fastify (backend API + SSE)
- simple-git + native git CLI (worktree lifecycle and merge)
- React 19 + Fluent 2 (Web UI)
- Ink (CLI/TUI)
- Zod (runtime validation and contract guards)

**Storage**: SQLite for run/session metadata + filesystem/git worktrees for artifacts

**Testing**: Vitest (unit/integration), Playwright (Web UI flows), contract tests against OpenAPI

**Target Platform**: Local developer machines (Windows/macOS/Linux) and Linux cloud containers

**Project Type**: Full-stack monorepo (backend API + CLI client + Web client + shared packages)

**Performance Goals**:
- First streamed step visible in under 10 seconds (SC-001)
- Ordered step streaming with no missing events to active watchers (SC-006)

**Constraints**:
- File access must never escape artifact directory (FR-006/FR-007, SC-002)
- Exactly two model sources only (FR-009, Constitution II)
- Backend API is authoritative; clients remain thin (FR-017, Constitution III/IV)
- No emojis in shipped product surfaces (Constitution VII)

**Scale/Scope**:
- First feature slice, single-agent runtime
- Concurrent isolated runs supported via per-run session/worktree
- Intended for team-scale internal usage during initial rollout

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- **I. Agent Runtime**: PASS — design is explicitly Microsoft Agent Framework single-loop orchestration.
- **II. Model Sources**: PASS — contract constrains `modelSource` to `copilot_sdk | microsoft_foundry`.
- **III. API-First**: PASS — run lifecycle, steps, and review actions are API-owned.
- **IV. Two Front-Ends at Parity**: PASS — CLI and Web are API clients with no business rules.
- **V. Observable Runs**: PASS — ordered step stream defined as SSE contract.
- **VI. Deployment Parity**: PASS — Node-based services designed for local and containerized cloud deployment.
- **VII. No Emojis**: PASS — output/contracts/docs avoid emoji content.

**Post-Design Re-check (after Phase 1)**: PASS — data model, contracts, and quickstart preserve all seven principles with no justified exceptions required.

## Project Structure

### Documentation (this feature)

```text
specs/001-single-agent-run/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── run-api.yaml
│   └── run-step-event.schema.json
└── tasks.md
```

### Source Code (repository root)

```text
apps/
├── api/                  # Backend API, run orchestrator, stream delivery
├── cli/                  # Terminal submission/watch experience
└── web/                  # React 19 + Fluent 2 thin client

packages/
├── agent-runtime/        # MAF loop + provider adapters
├── sandbox-fs/           # Path-safe read/write tool implementation
├── run-domain/           # Shared models/state machine/validation
└── api-contracts/        # Generated contract types and validators

tests/
├── contract/
├── integration/
└── e2e/
```

**Structure Decision**: Use a monorepo with three apps and shared domain/runtime packages so CLI and Web stay thin while backend remains the single implementation of run logic.

## Complexity Tracking

No constitution violations identified; section intentionally empty.
