# Data Model: Memory and Decision Inbox

**Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-06-15

All four entities are EF Core code-first models stored in the existing `scaffolder.db` SQLite file
via `MemoryDbContext`. Tags and active-issues lists are stored as JSON-serialized `TEXT` columns
with EF Core value converters.

---

## Entity: Decision

A finalized, audit-visible team decision. Content is markdown. Superseded decisions are never
deleted — only their status changes.

| Column | CLR Type | Nullable | Constraints | Notes |
|--------|----------|----------|-------------|-------|
| `Id` | `Guid` | No | PK | `Guid.NewGuid()` on create |
| `ProjectId` | `string` | No | Index | FK reference to `projects.project_id` (string) |
| `AgentName` | `string` | No | Index | Name of the agent that authored the decision |
| `Type` | `string` | No | | `architectural` \| `process` \| `scope` \| `technical` |
| `Status` | `string` | No | | `active` \| `superseded` \| `archived` |
| `Title` | `string` | No | | Human-readable title |
| `Content` | `string` | No | | Markdown body |
| `Rationale` | `string` | No | | Why this decision was made |
| `Slug` | `string` | No | | Kebab-case identifier (used for file naming in Phase 2) |
| `SupersededById` | `Guid?` | Yes | FK → Decision | Set when status = `superseded` |
| `CreatedAt` | `DateTimeOffset` | No | | UTC timestamp |
| `UpdatedAt` | `DateTimeOffset` | No | | UTC; updated on every write |

**EF Core index**: `(ProjectId)`, `(ProjectId, AgentName)`, `(ProjectId, Status)`

**Navigation**: `SupersededByDecision` → `Decision?` (self-referential, optional)

**Validation rules**:
- `Type` must be one of the four allowed values; validated in `MemoryService` before insert.
- `Status` transitions: `active → superseded`, `active → archived`. Reverse transitions are not
  permitted. `MemoryService` enforces this.
- When transitioning to `superseded`, `SupersededById` must refer to an existing Decision in the
  same project.

---

## Entity: DecisionInboxEntry

A draft decision submitted by an agent, awaiting promotion to a finalized Decision. Slugs are
unique per `(ProjectId, AgentName)` pair.

| Column | CLR Type | Nullable | Constraints | Notes |
|--------|----------|----------|-------------|-------|
| `Id` | `Guid` | No | PK | `Guid.NewGuid()` on create |
| `ProjectId` | `string` | No | Index | FK reference to `projects.project_id` |
| `AgentName` | `string` | No | | Name of the submitting agent |
| `Slug` | `string` | No | Unique(ProjectId, AgentName, Slug) | Kebab-case; idempotency key |
| `Title` | `string` | No | | |
| `Content` | `string` | No | | Markdown body |
| `Rationale` | `string` | No | | |
| `DecisionType` | `string` | No | | Same domain as `Decision.Type` |
| `Status` | `string` | No | | `pending` \| `merged` \| `rejected` |
| `MergedAt` | `DateTimeOffset?` | Yes | | Set atomically when merged |
| `MergedDecisionId` | `Guid?` | Yes | FK → Decision | Set atomically when merged |
| `CreatedAt` | `DateTimeOffset` | No | | |
| `UpdatedAt` | `DateTimeOffset` | No | | |

**EF Core unique index**: `(ProjectId, AgentName, Slug)` — enforces FR-006 at the database level.

**EF Core index**: `(ProjectId)`, `(ProjectId, Status)`

**State transitions**:

```
pending ──merge──> merged
pending ──reject──> rejected
```

Merged and rejected are terminal states. Attempting a transition from a terminal state returns
`409 Conflict` (FR-004).

**Idempotency on submit**: If a `pending` entry with the same `(ProjectId, AgentName, Slug)` already
exists, its mutable fields (Title, Content, Rationale, DecisionType) are updated and `200 OK` is
returned. If the entry is `merged` or `rejected`, `409 Conflict` is returned.

---

## Entity: AgentMemory

An accumulated knowledge record for a specific agent on a project. Tags enable cross-agent search.

| Column | CLR Type | Nullable | Constraints | Notes |
|--------|----------|----------|-------------|-------|
| `Id` | `Guid` | No | PK | |
| `ProjectId` | `string` | No | Index | FK reference to `projects.project_id` |
| `AgentName` | `string` | No | Index | |
| `Type` | `string` | No | | `core_context` \| `learning` \| `update` \| `pattern` |
| `Importance` | `string` | No | | `low` \| `medium` \| `high` |
| `Content` | `string` | No | | Markdown body |
| `Tags` | `string` | No | | JSON array stored as TEXT, e.g., `["database","schema"]`; empty array `[]` when none |
| `SessionReference` | `string?` | Yes | | Optional free-text session reference |
| `CreatedAt` | `DateTimeOffset` | No | | |
| `UpdatedAt` | `DateTimeOffset` | No | | |

**EF Core index**: `(ProjectId, AgentName)`, `(ProjectId, Type)`

**Value converter**: `Tags` is a `List<string>` on the C# side, serialized to/from JSON via an EF
Core value converter (`System.Text.Json`).

**Tag OR filtering** (FR-013, confirmed by spec edge-case notes):
For each requested tag `t`, the query adds a `WHERE Tags LIKE '%"t"%'` predicate. All tag
predicates are combined with `OR`. The quoted matching pattern prevents substring false positives.

**Validation rules**:
- `Type` must be one of the four allowed values.
- `Importance` must be one of the three allowed values.
- `Tags` elements must be non-empty strings; duplicates within an entry are deduplicated before
  storage.
- Agent with no memory entries: returns empty list, not an error (edge case from spec).

---

## Entity: SessionContext

A record of one agent work session. `SessionId` is unique per project (business key).

| Column | CLR Type | Nullable | Constraints | Notes |
|--------|----------|----------|-------------|-------|
| `Id` | `Guid` | No | PK | Surrogate key |
| `ProjectId` | `string` | No | Index | FK reference to `projects.project_id` |
| `SessionId` | `string` | No | Unique(ProjectId, SessionId) | Business key; unique per project (FR-015) |
| `FocusArea` | `string` | No | | What the session is focused on |
| `ActiveIssues` | `string` | No | | JSON array of issue identifiers stored as TEXT |
| `Summary` | `string?` | Yes | | Optional free-text summary |
| `StartedAt` | `DateTimeOffset` | No | | |
| `EndedAt` | `DateTimeOffset?` | Yes | | Null = session is still active |
| `CreatedAt` | `DateTimeOffset` | No | | |
| `UpdatedAt` | `DateTimeOffset` | No | | |

**EF Core unique index**: `(ProjectId, SessionId)` — enforces FR-015.

**EF Core index**: `(ProjectId)`, `(ProjectId, EndedAt)`

**Value converter**: `ActiveIssues` is a `List<string>` on the C# side, serialized to/from JSON.

**Current session query**: `WHERE ProjectId = @projectId AND EndedAt IS NULL ORDER BY StartedAt DESC LIMIT 1`

**Validation rules**:
- Duplicate `SessionId` within the same project: `409 Conflict` (FR-015).
- Ending a session that is already ended: no-op (idempotent).
- Updating an ended session's `FocusArea` or `ActiveIssues`: permitted; only `EndedAt` triggers
  "current" exclusion.

---

## MemoryDbContext Summary

```csharp
public class MemoryDbContext : DbContext
{
    public DbSet<Decision> Decisions { get; set; }
    public DbSet<DecisionInboxEntry> DecisionInboxEntries { get; set; }
    public DbSet<AgentMemory> AgentMemories { get; set; }
    public DbSet<SessionContext> SessionContexts { get; set; }
}
```

**OnModelCreating** configures:
- All unique indexes described above
- Value converters for `Tags` (AgentMemory) and `ActiveIssues` (SessionContext)
- Self-referential FK on `Decision.SupersededById` with `DeleteBehavior.Restrict`
- FK on `DecisionInboxEntry.MergedDecisionId` with `DeleteBehavior.Restrict`

**Connection string**: `Data Source={path}` where `path` comes from `IConfiguration["Database:Path"]`
(same key as the existing `SqliteDb`).

**DI lifetime**: Scoped (EF Core default for web applications).

---

## Init Seeding Records (FR-022)

When `ConfirmProposalAsync` completes, `MemorySeeder.SeedCastConfirmationAsync` inserts:

| Entity | Count | Details |
|--------|-------|---------|
| `SessionContext` | 1 | `SessionId = "initial"`, `FocusArea = "Team formation"`, `StartedAt = now`, no `EndedAt` |
| `AgentMemory` | N (one per agent) | `Type = core_context`, `Importance = high`, `Content` = agent charter summary, `Tags = ["team-formation"]` |
| `Decision` | 1 | `Type = process`, `Status = active`, `AgentName = "system"`, `Title = "Team formed"`, `Content` = team member list, `Slug = "team-formation-{timestamp}"` |

**Idempotency**: Before inserting, the seeder checks whether any `SessionContext` with
`SessionId = "initial"` already exists for the project. If it does, seeding is skipped entirely.
This handles the re-confirm scenario without creating duplicates.
