# Plan: Memory and Decision Inbox (Spec 006)

**Branch**: `006-memory-and-decision-inbox`
**Spec**: `specs/006-memory-and-decision-inbox/spec.md`
**Date**: 2026-06-15

## Already on this branch

- `apps/Scaffolder.Api/Infrastructure/ScaffolderAgentRuntime.cs` — session serialization helper (RunAsync, RunWithSerializationAsync, ResumeAsync)

---

## Architecture Decisions

| Decision | Choice | Rationale |
|---|---|---|
| ORM | EF Core with `Microsoft.EntityFrameworkCore.Sqlite` | Spec explicitly approves EF Core for new features (existing ADO.NET stores not migrated) |
| Location | `apps/Scaffolder.Api/Memory/` folder | Keeps parity with existing `Runs/`, `Casting/`, `Projects/` folders; no new package needed |
| DbContext | `MemoryDbContext` — **separate `memory.db` file** alongside `scaffolder.db` | Avoids touching the existing raw ADO.NET layer; separate file prevents schema conflicts and allows independent EnsureCreated |
| Tags storage | Comma-separated string (e.g. `"cross-team,database"`) | Simpler than JSON for SQLite LIKE queries; `cross-team` tag is the only system-significant value |
| Inbox rejection verb | `POST .../reject` (not DELETE) | State transition, not resource deletion — inbox entry is retained as audit evidence with `status=rejected` |
| Export trigger | After-write inline on every mutation (Phases 2–5) | SC-003: file ledger updated immediately after every write — no async queue |
| Scribe-pass export | Also exports at end of run (Phase 11) | Additive to inline export; Phase 11 export ensures the post-auto-merge state is reflected even if inline export ran before auto-merge |
| Post-run Scribe pass | Fire-and-forget via `IServiceScopeFactory` after terminal state | FR-031: non-blocking; run status not affected by pass failure; auto-merges only learning/pattern/update types |
| Import trigger | On `ConfirmCastAsync` + explicit POST endpoint | FR-021 |
| MCP tools | New tool group `MemoryTools` in `Scaffolder.Mcp` | Constitution Principle IV: all API capabilities reachable via MCP |

---

## Data Model

### Entities (`apps/Scaffolder.Api/Memory/`)

**`Decision`**
```
Id                int       PK, AUTOINCREMENT
ProjectId         string    NOT NULL
AgentName         string    NOT NULL
Type              string    NOT NULL  -- architectural | process | scope | technical
Status            string    NOT NULL  -- active | superseded | archived
Title             string    NOT NULL
Content           string    NOT NULL  -- markdown
Rationale         string?
SupersededById    int?      FK → Decision.Id
CreatedAt         DateTimeOffset
UpdatedAt         DateTimeOffset
```

**`DecisionInboxEntry`**
```
Id                int       PK, AUTOINCREMENT
ProjectId         string    NOT NULL
AgentName         string    NOT NULL
Slug              string    NOT NULL  -- UNIQUE(project_id, slug)
Title             string    NOT NULL
Content           string    NOT NULL
Rationale         string?
Type              string    NOT NULL  -- architectural | scope | process | pattern | learning | update
Status            string    NOT NULL  -- pending | merged | rejected
MergedAt          DateTimeOffset?
DecisionId        int?      FK → Decision.Id (set on merge)
CreatedAt         DateTimeOffset
UpdatedAt         DateTimeOffset
```

**`AgentMemory`**
```
Id                int       PK, AUTOINCREMENT
ProjectId         string    NOT NULL
AgentName         string    NOT NULL
Type              string    NOT NULL  -- core_context | learning | update | pattern
Importance        string    NOT NULL  -- low | medium | high
Content           string    NOT NULL  -- markdown
Tags              string?   -- comma-separated (e.g. "cross-team,database"); "cross-team" enables cross-agent injection
SessionId         string?
CreatedAt         DateTimeOffset
UpdatedAt         DateTimeOffset
```

**`SessionContext`** (schema from spec DDL)
```
Id                int       PK, AUTOINCREMENT
ProjectId         string    NOT NULL
SessionId         string    NOT NULL  -- UNIQUE(project_id, session_id)
FocusArea         string    NOT NULL
ActiveIssues      string?   -- JSON array
Summary           string?
SerializedState   string?   -- JSON from SerializeSessionAsync
StartedAt         DateTimeOffset
EndedAt           DateTimeOffset?
```

---

## 15 REST Endpoints

### Decision inbox (4)
| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/projects/{id}/decisions/inbox` | Submit draft decision |
| `GET` | `/api/projects/{id}/decisions/inbox` | List inbox entries (`?agent=&type=&status=`) |
| `POST` | `/api/projects/{id}/decisions/inbox/{entryId}/merge` | Atomically merge → Decision |
| `POST` | `/api/projects/{id}/decisions/inbox/{entryId}/reject` | Reject entry (audit-safe; entry retained with `status=rejected`) |

### Decisions (3)
| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/projects/{id}/decisions` | Create decision directly |
| `GET` | `/api/projects/{id}/decisions` | List active decisions (`?type=&agent=`) |
| `PUT` | `/api/projects/{id}/decisions/{decisionId}` | Update status / set superseded |

### Agent memory (4)
| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/projects/{id}/agents/{name}/memory` | Record memory entry |
| `GET` | `/api/projects/{id}/agents/{name}/memory` | Get agent memory (`?type=&importance=`) |
| `GET` | `/api/projects/{id}/agents/{name}/memory/{memId}` | Get single memory record |
| `GET` | `/api/projects/{id}/memory` | Cross-agent search (`?tags=&type=`) |

### Session context (3)
| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/projects/{id}/sessions` | Start session (ends any current open session first) |
| `GET` | `/api/projects/{id}/sessions/current` | Get most recent active session |
| `PUT` | `/api/projects/{id}/sessions/current` | Update current session / mark ended |

### File interop (2)
| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/projects/{id}/memory/export` | Regenerate `.squad/` files from DB |
| `POST` | `/api/projects/{id}/memory/import` | Import `.squad/decisions/inbox/` files into DB |

---

## Implementation Phases

### Phase 1 — EF Core data layer

1. Add `Microsoft.EntityFrameworkCore.Sqlite` (v9.x) to `Scaffolder.Api.csproj`
2. Create `apps/Scaffolder.Api/Memory/` folder
3. Create 4 entity classes: `Decision.cs`, `DecisionInboxEntry.cs`, `AgentMemory.cs`, `SessionContext.cs`
4. Create `MemoryDbContext.cs`:
   - Connection string: derives `memory.db` path from `Database:Path` config (same directory as `scaffolder.db`)
   - Configures all 4 DbSets with table names matching spec DDL
   - Unique index on `(project_id, slug)` for inbox entries
   - Unique index on `(project_id, session_id)` for sessions
5. Register `AddDbContext<MemoryDbContext>` as **scoped** in `Program.cs`
6. Call `memoryDb.Database.EnsureCreatedAsync()` at startup (in a temporary scope, alongside `SqliteDb.EnsureCreatedAsync()`)
7. `dotnet build` — verify clean before proceeding

### Phase 2 — Domain: AgentName + AgentCharter on Run + runner interface

This phase touches the domain model and the agent runner abstraction — required before any run integration can compile.

1. **`packages/Scaffolder.Domain/Run.cs`** — add two nullable fields:
   ```csharp
   public string? AgentName { get; init; }
   public string? AgentCharter { get; init; }
   ```
2. **`apps/Scaffolder.Api/Infrastructure/SqliteDb.cs`** — add two `TryAlterAsync` migration calls:
   ```csharp
   await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN agent_name TEXT;", ct);
   await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN agent_charter TEXT;", ct);
   ```
3. **`apps/Scaffolder.Api/Infrastructure/SqliteRunStore.cs`** — update `InsertAsync`, `TryCreateProjectRunAsync` (both INSERT statements), `SelectSql` (add columns 18/19), and `Map` (read positions 18/19)
4. **`apps/Scaffolder.Api/Contracts/Dtos.cs`** — add `agent_name` to `CreateProjectRunRequest`
5. **`packages/Scaffolder.Domain/IAgentRunner.cs`** — add optional `systemPromptContext` parameter at end of `ExecuteAsync` signature:
   ```csharp
   Task<string> ExecuteAsync(..., CancellationToken ct, string? systemPromptContext = null);
   ```
6. **`packages/Scaffolder.AgentRuntime/Workflow/WorkflowMessages.cs`** — add `string? SystemPromptContext = null` to `AgentTurnInput` record
7. **`packages/Scaffolder.AgentRuntime/Workflow/AgentTurnExecutor.cs`** — pass `input.SystemPromptContext` to `_agentRunner.ExecuteAsync`
8. **`packages/Scaffolder.AgentRuntime/GitHubCopilotAgentRunner.cs`** — add `systemPromptContext` param; append to `SystemMessage.Content` when non-null
9. **`packages/Scaffolder.AgentRuntime/FoundryAgentRunner.cs`** — same; append to existing system prompt const
10. **`packages/Scaffolder.AgentRuntime/AgentRunnerDispatcher.cs`** — add param, pass through to both runners
11. `dotnet build` — verify clean

### Phase 3 — Decision inbox endpoints

All endpoints registered inline in `Program.cs` following the existing pattern.

- **POST inbox**: Validate required fields (`agent_name`, `slug`, `type`, `title`, `content`); upsert by `(project_id, slug)` — idempotent by slug (FR-006); return `201 Created` with entry ID
- **GET inbox**: Optional `?agent=`, `?type=`, `?status=` filters; default returns `pending` entries
- **POST inbox/{id}/merge**:
  - Verify entry is `pending` — return `409` if already merged/rejected
  - EF transaction: create `Decision`, set `entry.Status=merged`, `entry.MergedAt=now`, `entry.DecisionId=new.Id`
  - Inline `SquadMemoryExporter.ExportAsync` (SC-003)
  - Return `201` with decision ID
- **POST inbox/{id}/reject**: Set `Status=rejected`, retain entry; return `200`

### Phase 4 — Decision endpoints

- **POST decisions**: Create `Decision` directly (coordinator path); inline export; return `201`
- **GET decisions**: Optional `?type=`, `?agent=`; default returns `status=active`
- **PUT decisions/{id}**: Update `Status`, `Content`, `Rationale`; if `supersededById` provided, validate target exists; inline export

### Phase 5 — Agent memory endpoints

- **POST agents/{name}/memory**: Create `AgentMemory`; `tags` stored as comma-separated string; inline export; return `201`
- **GET agents/{name}/memory**: Optional `?type=`, `?importance=`
- **GET agents/{name}/memory/{memId}**: Single record
- **GET memory**: Cross-agent search; `?tags=` OR semantics per tag using `LIKE '%{tag}%'`; `?type=` filter

### Phase 6 — Session context endpoints

- **POST sessions**: Auto-end any open session first; create new `SessionContext`; enforce unique `(project_id, session_id)` — return `409` on conflict; inline export (updates `identity/now.md`); return `201`
- **GET sessions/current**: `WHERE ended_at IS NULL ORDER BY started_at DESC LIMIT 1`; return `404` if none
- **PUT sessions/current**: Update `FocusArea`, `ActiveIssues`, `Summary`, `SerializedState`; set `EndedAt` when `end=true`; inline export

### Phase 7 — Squad file exporter + importer + context artifacts

**`SquadMemoryExporter`** in `packages/Scaffolder.Squad/Memory/SquadMemoryExporter.cs`:

```
ExportAsync(projectRoot, decisions, inboxEntries, agentMemories, currentSession):
  1. Regenerate .squad/decisions.md — append all active decisions as markdown sections
  2. Regenerate .squad/decisions/inbox/{slug}.md — one file per pending inbox entry
  3. For each agent with memory: append to .squad/agents/{name}/history.md (learning/update entries only)
  4. Write .squad/identity/now.md from currentSession.FocusArea + ActiveIssues
  5. Write .agentweaver/context/boundaries.md — compiled from active architectural + scope decisions:
     ```markdown
     # Project Boundaries
     > Compiled from team decisions. Authoritative for all agent runs.
     
     ## Architectural Decisions
     ### {title}
     **Type:** architectural | **By:** {agent} | **Date:** {date}
     {content}
     ---
     
     ## Scope Decisions
     ...
     ```
  6. Write .agentweaver/context/patterns.md — compiled from pattern-type AgentMemory across all agents:
     ```markdown
     # Team Patterns
     > Accumulated patterns from agent work sessions.
     
     ## {agent} — {memory title}
     {content}
     ---
     ```
```

**`SquadMemoryImporter`** — unchanged from prior plan.

**Endpoints** — unchanged from prior plan.

### Phase 8 — MemoryContextCompiler (FR-024/025/027/030)

Create `apps/Scaffolder.Api/Memory/MemoryContextCompiler.cs`:

```csharp
public sealed class MemoryContextCompiler(MemoryDbContext db)
{
    public async Task<string?> CompileAsync(
        string projectId, string agentName, CancellationToken ct)
    {
        // Layer 1: active architectural + scope decisions (team-wide boundaries)
        var decisions = await db.Decisions
            .Where(d => d.ProjectId == projectId 
                     && d.Status == "active"
                     && (d.Type == "architectural" || d.Type == "scope"))
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);

        // Layer 2: agent core_context memories
        var coreMemories = await db.AgentMemory
            .Where(m => m.ProjectId == projectId 
                     && m.AgentName == agentName 
                     && m.Type == "core_context")
            .ToListAsync(ct);

        // Layer 3: top-5 high-importance learnings/patterns for this agent
        //          + cross-team tagged memories from other agents
        var learnings = await db.AgentMemory
            .Where(m => m.ProjectId == projectId
                     && m.Importance == "high"
                     && (m.AgentName == agentName || (m.Tags != null && m.Tags.Contains("cross-team")))
                     && (m.Type == "learning" || m.Type == "pattern"))
            .OrderByDescending(m => m.CreatedAt)
            .Take(5)
            .ToListAsync(ct);

        // Layer 4: current session
        var session = await db.SessionContexts
            .Where(s => s.ProjectId == projectId && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (!decisions.Any() && !coreMemories.Any() && !learnings.Any() && session is null)
            return null;

        var sb = new StringBuilder();
        
        if (decisions.Any())
        {
            sb.AppendLine("## Boundaries and Decisions");
            sb.AppendLine("> These are non-negotiable team boundaries. They take precedence over all other context.");
            foreach (var d in decisions)
            {
                sb.AppendLine($"\n### {d.Title}");
                sb.AppendLine($"**Type:** {d.Type} | **Decided by:** {d.AgentName}");
                sb.AppendLine(d.Content);
                if (!string.IsNullOrEmpty(d.Rationale))
                    sb.AppendLine($"> **Rationale:** {d.Rationale}");
            }
        }

        if (coreMemories.Any() || learnings.Any())
        {
            sb.AppendLine("\n## Memory");
            foreach (var m in coreMemories)
                sb.AppendLine($"- [core] {m.Content}");
            foreach (var m in learnings)
                sb.AppendLine($"- [{m.Type}] {m.Content}");
        }

        if (session is not null)
        {
            sb.AppendLine("\n## Current Session");
            sb.AppendLine($"**Focus:** {session.FocusArea}");
            if (!string.IsNullOrEmpty(session.ActiveIssues))
                sb.AppendLine($"**Active issues:** {session.ActiveIssues}");
            if (!string.IsNullOrEmpty(session.Summary))
                sb.AppendLine($"**Summary:** {session.Summary}");
        }

        return sb.ToString();
    }
}
```

Register `MemoryContextCompiler` as a scoped service in `Program.cs`.

### Phase 9 — RunOrchestrator integration (FR-024/026)

In `RunOrchestrator`, inject `MemoryContextCompiler`. Modify `StartRunAsync` and `StartReservedProjectRunAsync`:

**Pre-run (before `_workflowFactory.StartAsync`):**
```csharp
string? memoryContext = null;
if (!string.IsNullOrEmpty(run.AgentName))
{
    try
    {
        memoryContext = await _memoryCompiler.CompileAsync(
            run.ProjectId?.ToString() ?? "", run.AgentName, ct);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Memory context compilation failed for run {RunId} — proceeding without", run.Id);
    }
}

// Prepend to system prompt (after charter, before task)
var systemPrompt = run.AgentCharter ?? "";
if (!string.IsNullOrEmpty(memoryContext))
    systemPrompt = $"{systemPrompt}\n\n{memoryContext}";
```

**Harvest prompt** — injected into `AgentTurnInput.Task` (appended after the user's task):
```csharp
var harvestPrompt = $"""

---
**Session Harvest** (complete after your main task)
Project ID: {run.ProjectId} | Agent: {run.AgentName} | API: {_apiBaseUrl}

After completing the task above, please:
1. Submit any architectural or scope decisions made to: POST {_apiBaseUrl}/api/projects/{run.ProjectId}/decisions/inbox
2. Record new learnings or patterns at: POST {_apiBaseUrl}/api/projects/{run.ProjectId}/agents/{run.AgentName}/memory
3. If you encountered any boundary conflicts (constraints that prevented the task from being completed as specified), flag them as a `process` inbox entry titled "Boundary conflict: {short description}".
""";
```

### Phase 10 — Init seeding (FR-022)

In `apps/Scaffolder.Api/Casting/CastingService.cs`, at end of `ConfirmCastAsync` — unchanged from prior plan.

### Phase 11 — MCP memory tools (FR-028)

Add `apps/Scaffolder.Mcp/Tools/MemoryTools.cs` — 13 new MCP tools:

`decision_inbox_submit`, `decision_inbox_list`, `decision_inbox_merge`, `decision_inbox_reject`, `decision_create`, `decision_list`, `decision_update`, `memory_record`, `memory_get`, `memory_search`, `session_start`, `session_current`, `session_update`, `memory_export`, `memory_import`

Each is a thin proxy to the corresponding REST endpoint following the same `ScaffolderApiClient` pattern as existing tools.

### Phase 12 — Post-run Scribe pass (FR-031/032/033)

**The loop-close phase.** Closes the memory flywheel so each run deposits knowledge into the DB, which feeds the next run's context compilation.

**`apps/Scaffolder.Api/Runs/PostRunScribeService.cs`** — new scoped service:

```csharp
public sealed class PostRunScribeService(
    MemoryDbContext memoryDb,
    ILogger<PostRunScribeService> logger)
{
    /// <summary>
    /// Runs the post-run Scribe pass for a completed project run.
    /// Non-blocking: all errors are logged, none are re-thrown.
    /// </summary>
    public async Task RunAsync(Run run, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(run.AgentName) || !run.ProjectId.HasValue) return;
        
        var projectId = run.ProjectId.Value.ToString();
        var agentName = run.AgentName;
        var runStarted = run.StartedAt;

        try
        {
            // Step 1: Auto-merge low-risk inbox entries
            var autoMergeTypes = new[] { "learning", "pattern", "update" };
            var toMerge = await memoryDb.DecisionInbox
                .Where(e => e.ProjectId == projectId
                         && e.AgentName == agentName
                         && e.Status == "pending"
                         && autoMergeTypes.Contains(e.Type)
                         && e.CreatedAt >= runStarted)
                .ToListAsync(ct);

            var now = DateTimeOffset.UtcNow;
            foreach (var entry in toMerge)
            {
                var decision = new Decision
                {
                    ProjectId = projectId, AgentName = entry.AgentName,
                    Type = entry.Type, Status = "active",
                    Title = entry.Title, Content = entry.Content,
                    Rationale = entry.Rationale,
                    CreatedAt = now, UpdatedAt = now,
                };
                memoryDb.Decisions.Add(decision);
                entry.Status = "merged";
                entry.UpdatedAt = now;
            }

            // Step 2: Flag architectural/scope entries as needing review
            var reviewNeeded = await memoryDb.DecisionInbox
                .Where(e => e.ProjectId == projectId
                         && e.AgentName == agentName
                         && e.Status == "pending"
                         && (e.Type == "architectural" || e.Type == "scope")
                         && e.CreatedAt >= runStarted)
                .CountAsync(ct);

            await memoryDb.SaveChangesAsync(ct);
            logger.LogInformation(
                "PostRunScribe: run {RunId} — auto-merged {Merged} entries, {Review} pending coordinator review",
                run.Id, toMerge.Count, reviewNeeded);

            // Step 3: Export to .squad/ + .agentweaver/context/ (inline — SC-003)
            var allDecisions = await memoryDb.Decisions
                .Where(d => d.ProjectId == projectId && d.Status == "active").ToListAsync(ct);
            var allInbox = await memoryDb.DecisionInbox
                .Where(e => e.ProjectId == projectId && e.Status == "pending").ToListAsync(ct);
            var allMemory = await memoryDb.AgentMemory
                .Where(m => m.ProjectId == projectId).ToListAsync(ct);
            var session = await memoryDb.SessionContexts
                .Where(s => s.ProjectId == projectId && s.EndedAt == null)
                .OrderByDescending(s => s.StartedAt)
                .FirstOrDefaultAsync(ct);

            // Step 4: Update session with run outcome
            if (session is not null)
            {
                var outcome = $"Run {run.Id} by {agentName} completed.";
                session.Summary = string.IsNullOrEmpty(session.Summary)
                    ? outcome
                    : session.Summary + "\n" + outcome;
                await memoryDb.SaveChangesAsync(ct);
            }


            // Step 3: Call SquadMemoryExporter.ExportAsync(project.WorkingDirectory, decisionDtos, ...)
            // PostRunScribeService injects IProjectStore to resolve project.WorkingDirectory.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PostRunScribe pass failed for run {RunId} — memory context unchanged", run.Id);
        }
    }
}
```

**Constructor signature** (required injections):
```csharp
public sealed class PostRunScribeService(
    MemoryDbContext memoryDb,
    IProjectStore projectStore,   // resolves project.WorkingDirectory for exporter
    ILogger<PostRunScribeService> logger)
```

**`RunWatchLoopService` trigger** — when a project run with `AgentName` reaches a terminal state (`Completed`, `Merged`, `NoChanges`), call `PostRunScribeService.RunAsync` in a background fire-and-forget scope (using `IServiceScopeFactory`). The trigger fires after the run event is emitted, not before.
Register `PostRunScribeService` as scoped in `Program.cs`.

---

## File Layout

```
apps/Scaffolder.Api/Runs/
  PostRunScribeService.cs   ← NEW: post-run loop-close (auto-merge + export + session update)

apps/Scaffolder.Api/Memory/
  Decision.cs
  DecisionInboxEntry.cs
  AgentMemory.cs
  SessionContext.cs
  MemoryDbContext.cs
  MemoryContextCompiler.cs  ← NEW: deterministic context assembler (hierarchy-aware)
  MemoryEndpoints.cs

packages/Scaffolder.Squad/Memory/
  SquadMemoryExporter.cs    ← also writes .agentweaver/context/boundaries.md + patterns.md
  SquadMemoryImporter.cs

apps/Scaffolder.Mcp/Tools/
  MemoryTools.cs            ← 13 new MCP tools
```

**Context artifact files written to managed repos (by exporter):**
```
.agentweaver/context/
  boundaries.md    ← compiled from active architectural + scope Decisions
  patterns.md      ← compiled from pattern-type AgentMemory entries
```

---

## Success Verification

- `dotnet build` — 0 errors after each phase
- Phase 1: `memory_db` tables exist in `scaffolder.db` after startup
- Phase 2–5: All 15 endpoints return correct shapes; merge is atomic (no partial state)
- Phase 6: After a merge, `.squad/decisions.md` AND `.agentweaver/context/boundaries.md` are updated on disk immediately
- Phase 7: `MemoryContextCompiler` returns a structured block with three named sections (Boundaries and Decisions, Memory, Current Session) given a seeded project
- Phase 8: A run with `agent_name` stores a `SystemPrompt` that contains the compiled context; a completed run's task prompt ends with the harvest section
- Phase 9: After `ConfirmCastAsync`, DB has 1 genesis decision + N memory records + 1 session
- Phase 10: MCP tools discoverable; `decision_inbox_submit` roundtrip works against a live API
- Phase 11: After a project run with `AgentName` completes, `pending` learning/pattern/update inbox entries are auto-merged; `.agentweaver/context/boundaries.md` is regenerated; the next `CompileAsync` call for that agent returns richer output than before the run

---

## Out of Scope

- Migrating existing raw ADO.NET stores to EF Core
- Web UI pages for memory/decisions (separate feature)
- Role-based access enforcement for "Scribe-only" direct decision creation (future)
- Pagination on list endpoints (future — returns all records for now)
