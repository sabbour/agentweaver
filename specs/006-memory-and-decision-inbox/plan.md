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
| DbContext | `MemoryDbContext` — separate from `SqliteDb` | Avoids touching the existing raw ADO.NET layer; same DB file via shared connection string |
| Export trigger | After-write inline (not background) | SC-003: file ledger updated immediately after every write — no async queue |
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
Slug              string    NOT NULL  -- UNIQUE(project_id, agent_name, slug)
Title             string    NOT NULL
Content           string    NOT NULL
Rationale         string?
DecisionType      string    NOT NULL
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
Tags              string?   -- JSON array
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
| `GET` | `/api/projects/{id}/decisions/inbox` | List inbox entries (`?agent=&type=`) |
| `POST` | `/api/projects/{id}/decisions/inbox/{entryId}/merge` | Atomically merge → Decision |
| `DELETE` | `/api/projects/{id}/decisions/inbox/{entryId}` | Reject entry |

### Decisions (3)
| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/projects/{id}/decisions` | Create decision directly |
| `GET` | `/api/projects/{id}/decisions` | List active decisions (`?type=&agent=`) |
| `PATCH` | `/api/projects/{id}/decisions/{decisionId}` | Update status / set superseded |

### Agent memory (3)
| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/projects/{id}/agents/{name}/memory` | Record memory entry |
| `GET` | `/api/projects/{id}/agents/{name}/memory` | Get agent memory (`?type=&importance=`) |
| `GET` | `/api/projects/{id}/memory` | Cross-agent search (`?tags=&type=`) |

### Session context (3)
| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/projects/{id}/sessions` | Start session |
| `GET` | `/api/projects/{id}/sessions/current` | Get most recent active session |
| `PATCH` | `/api/projects/{id}/sessions/{sessionId}` | Update session / mark ended |

### File interop (2)
| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/projects/{id}/memory/export` | Regenerate `.squad/` files from DB |
| `POST` | `/api/projects/{id}/memory/import` | Import `.squad/decisions/inbox/` files into DB |

---

## Implementation Phases

### Phase 1 — EF Core data layer

1. Add `Microsoft.EntityFrameworkCore.Sqlite` to `Scaffolder.Api.csproj`
2. Create `apps/Scaffolder.Api/Memory/` folder
3. Create 4 entity classes: `Decision.cs`, `DecisionInboxEntry.cs`, `AgentMemory.cs`, `SessionContext.cs`
4. Create `MemoryDbContext.cs`:
   - Reads connection string from `SqliteDb` (same DB file — inject `IConfiguration`)
   - Configures all 4 DbSets with table names matching spec DDL
   - Unique index on `(project_id, agent_name, slug)` for inbox entries
   - Unique index on `(project_id, session_id)` for sessions
5. Register `MemoryDbContext` in `Program.cs` via `AddDbContext<MemoryDbContext>`
6. Call `db.Database.EnsureCreatedAsync()` at startup (alongside existing `SqliteDb.EnsureCreatedAsync()`)
7. `dotnet build` — verify clean before proceeding

### Phase 2 — Decision inbox endpoints

Create `apps/Scaffolder.Api/Memory/DecisionInboxEndpoints.cs` (or register in `Program.cs`):

- **POST inbox**: Validate request, upsert by `(project_id, agent_name, slug)` (FR-006 idempotency), return `201 Created` with entry ID
- **GET inbox**: Query with optional `agent` and `type` filters, return list
- **POST inbox/{id}/merge**: 
  - Verify entry is `pending` — return 409 if already merged/rejected (FR-004)
  - Begin EF transaction
  - Create `Decision` record
  - Update `DecisionInboxEntry`: `Status=merged`, `MergedAt=now`, `DecisionId=new.Id`
  - Commit
  - Call `SquadMemoryExporter.ExportAsync` (FR-018)
  - Return `201 Created` with decision ID
- **DELETE inbox/{id}**: Mark `rejected`, return `204 No Content`

### Phase 3 — Decision endpoints

- **POST decisions**: Create `Decision` directly; call exporter; return `201`
- **GET decisions**: Query with optional `type` and `agent` filters; return list
- **PATCH decisions/{id}**: Update `Status` and/or set `SupersededById`; validate referenced decision exists if setting superseded; call exporter

### Phase 4 — Agent memory endpoints

- **POST agents/{name}/memory**: Create `AgentMemory`; serialize `tags` array as JSON; call exporter; return `201`
- **GET agents/{name}/memory**: Query by project + agent name; optional `?type=` and `?importance=` filters
- **GET memory**: Cross-agent search; optional `?type=` filter; `?tags=` uses OR semantics (any tag match)

For tags: store as JSON string in DB; for tag filtering use `LIKE` on the JSON column (sufficient for SQLite without extensions).

### Phase 5 — Session context endpoints

- **POST sessions**: Create `SessionContext`; enforce unique `(project_id, session_id)` — return 409 on conflict (FR-015); return `201`
- **GET sessions/current**: Query `WHERE ended_at IS NULL ORDER BY started_at DESC LIMIT 1`; return 404 if none
- **PATCH sessions/{sessionId}**: Update `FocusArea`, `ActiveIssues`, `Summary`, `SerializedState`, `EndedAt`; call exporter (for `identity/now.md` update)

### Phase 6 — Squad file exporter + importer + context artifacts + endpoints

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

### Phase 7 — MemoryContextCompiler (FR-024/025/027/030)

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

### Phase 8 — RunOrchestrator integration (FR-024/026)

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

### Phase 9 — Init seeding (FR-022)

In `apps/Scaffolder.Api/Casting/CastingService.cs`, at end of `ConfirmCastAsync` — unchanged from prior plan.

### Phase 10 — MCP memory tools (FR-028)

Add `apps/Scaffolder.Mcp/Tools/MemoryTools.cs` — 12 new MCP tools:

`decision_inbox_submit`, `decision_inbox_list`, `decision_inbox_merge`, `decision_inbox_reject`, `decision_create`, `decision_list`, `decision_update`, `memory_record`, `memory_get`, `memory_search`, `session_start`, `session_current`, `session_update`

Each is a thin proxy to the corresponding REST endpoint following the same `ScaffolderApiClient` pattern as existing tools.

---

## File Layout

```
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

---

## Out of Scope

- Migrating existing raw ADO.NET stores to EF Core
- Web UI pages for memory/decisions (separate feature)
- Role-based access enforcement for "Scribe-only" direct decision creation (future)
- Pagination on list endpoints (future — returns all records for now)
