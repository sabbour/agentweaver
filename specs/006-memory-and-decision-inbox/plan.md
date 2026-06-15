# Implementation Plan: Memory and Decision Inbox

**Branch**: `006-memory-and-decision-inbox` | **Date**: 2026-06-15 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/006-memory-and-decision-inbox/spec.md`

## Summary

Introduce a structured, queryable, audit-friendly memory and decision system for agent teams. The
feature adds four new entities (Decision, DecisionInboxEntry, AgentMemory, SessionContext) backed
by EF Core + SQLite, exposes 15 REST endpoints for full CRUD and workflow operations (inbox submit,
merge, reject; decision create/list/update; per-agent memory; cross-agent tag search; session
lifecycle), and seeds baseline records when a project cast is confirmed. Export/import to the Squad
file ledger and agent prompt injection are explicitly deferred to Phase 2. All 15 endpoints are
immediately consumable by the existing CLI and Web UI as required by the API-first principle.

## Technical Context

**Language/Version**: C# / .NET 10 (matches the existing `Scaffolder.Api` project)

**Primary Dependencies**:
- `Microsoft.EntityFrameworkCore` (new — deliberate migration away from raw ADO.NET for new features)
- `Microsoft.EntityFrameworkCore.Sqlite` (new SQLite provider for EF Core)
- `Microsoft.EntityFrameworkCore.Design` (new — EF Core tooling for migrations)
- `Scaffolder.Domain` (existing — `ProjectId`, `IProjectStore`)
- `Scaffolder.Squad` (existing — `SquadWriter`, `SquadPaths`; referenced for Phase 2 export prep)

**Storage**: SQLite — same physical `scaffolder.db` file as the existing `SqliteDb`/ADO.NET layer.
EF Core uses a dedicated `MemoryDbContext` with its own table namespace, managed by EF migrations.
No ADO.NET tables are migrated; both data-access paths coexist in the same file.

**Testing**: `Scaffolder.Tests` project (existing xunit / `WebApplicationFactory` pattern)

**Target Platform**: Local developer machine and hosted cloud (.NET 10, cross-platform — satisfies
Deployment Parity, Principle VI)

**Project Type**: Additive feature to an existing ASP.NET Core minimal API web service

**Performance Goals**: Sub-second round-trip for all 15 endpoints under light developer-machine
load (SC-005). Merge atomicity guaranteed via EF Core `SaveChangesAsync` within an explicit
transaction (SC-002).

**Constraints**:
- No mocks, fakes, or stub implementations at any layer (Principle VII)
- No emojis in code, UI, output, logs, generated docs (Principle VIII)
- API-first: all endpoints are the single write path; no separate client-side logic (Principle III)
- MVP scope: EF Core setup + 4 entities + 15 endpoints + init seeding. File export/import (FR-018
  to FR-021) and agent prompt injection (FR-023) are Phase 2 and out of scope for this plan.

**Scale/Scope**: Single-project developer tool; expected record counts are small (tens to hundreds
of records per project). No sharding, caching layer, or pagination required for MVP.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Applies? | Assessment |
|-----------|----------|------------|
| I — Agent Runtime | No | No agent loop changes; purely infrastructure |
| II — Model Sources | No | No model calls in this feature |
| III — API-First | **Yes** | 15 REST endpoints are the sole write path; no client-side business logic |
| IV — Two Frontends at Parity | **Yes** | Endpoints must be reachable from CLI and Web UI; no client code added in this feature, but the API surface is complete |
| V — Observable Runs | No | No run-step stream changes |
| VI — Deployment Parity | **Yes** | SQLite + EF Core run identically locally and in a hosted container |
| VII — No Mocks/Fakes | **Yes** | All implementations must be functional; no placeholder response stubs |
| VIII — No Emojis | **Yes** | Enforced across all new code, docs, and log messages |
| IX — Responsible AI | No | No AI/model usage in this feature |
| X — Safe Execution | No | No sandbox boundary changes |
| XI — Governance Toolkit | No | No governance layer changes |

**Gate result**: PASS — no violations. The one noteworthy point is Principle III: the MVP does not
add CLI/Web UI screens, but the API is complete and both clients can consume it immediately. The
spec explicitly states "the web UI and CLI are consumers of the same 15 REST endpoints" (Assumption
2); no further client work is in scope here.

**Post-Phase-1 re-check**: The design in `data-model.md` and `contracts/` confirms:
- Atomic merge via EF Core transaction satisfies SC-002 and Principle X (no partial state).
- All endpoints return real data immediately; no stubs (Principle VII).
- Database file path follows `Database:Path` configuration — works locally and in cloud (Principle VI).
- No emojis introduced (Principle VIII confirmed at artifact level).

## Project Structure

### Documentation (this feature)

```text
specs/006-memory-and-decision-inbox/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/
│   ├── decision-inbox.md
│   ├── decisions.md
│   ├── agent-memory.md
│   └── session-context.md
└── tasks.md             # Phase 2 output (/speckit.tasks — NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
apps/Scaffolder.Api/
├── Memory/                            # New feature folder
│   ├── Entities/
│   │   ├── Decision.cs                # EF Core entity
│   │   ├── DecisionInboxEntry.cs      # EF Core entity
│   │   ├── AgentMemory.cs             # EF Core entity
│   │   └── SessionContext.cs          # EF Core entity
│   ├── MemoryDbContext.cs             # EF Core DbContext (SQLite)
│   ├── MemoryService.cs               # Application service: all business logic
│   ├── MemoryEndpoints.cs             # Extension method: maps the 15 minimal API routes
│   └── MemoryDtos.cs                  # Request/response record types
├── Migrations/                        # EF Core auto-generated migration files
│   ├── YYYYMMDD_InitialMemorySchema.cs
│   └── MemoryDbContextModelSnapshot.cs
├── Casting/
│   └── CastingService.cs              # Modified: inject MemorySeeder, call after confirm
├── Program.cs                         # Modified: register MemoryDbContext + MemoryService
└── Scaffolder.Api.csproj              # Modified: add EF Core package references

tests/Scaffolder.Tests/
└── Memory/
    ├── DecisionInboxTests.cs          # FR-001 to FR-006 + SC-002 atomicity
    ├── DecisionTests.cs               # FR-007 to FR-010
    ├── AgentMemoryTests.cs            # FR-011 to FR-013 + SC-007 tag filtering
    └── SessionContextTests.cs         # FR-014 to FR-017
```

**Structure Decision**: Single additive folder `Memory/` inside `Scaffolder.Api` keeps the feature
self-contained while following the existing per-feature folder pattern (`Casting/`, `Projects/`,
`Infrastructure/`). EF Core migrations live in `Migrations/` at the API project root — the standard
EF Core convention that makes `dotnet ef` tooling work without extra configuration. No new assembly
is introduced; the feature does not warrant its own project.

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Two data-access patterns in one project (ADO.NET + EF Core) | Deliberate incremental migration per the spec assumption; existing ADO.NET stores are not in scope to rewrite | Migrating all existing stores to EF Core in this feature would be a much larger change that risks breaking existing functionality and is out of scope |
