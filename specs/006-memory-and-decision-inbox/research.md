# Research: Memory and Decision Inbox

**Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-06-15

All unknowns from the Technical Context are resolved below. No NEEDS CLARIFICATION items remain.

---

## EF Core + SQLite in an existing ADO.NET project

**Decision**: Introduce `Microsoft.EntityFrameworkCore 9.x` (the latest stable release compatible
with .NET 10 RC) alongside the existing `Microsoft.Data.Sqlite` ADO.NET layer. Both access the
same physical database file; EF Core manages its own tables exclusively via code-first migrations.

**Rationale**: EF Core with the SQLite provider is the standard, well-supported approach for
relational data access in .NET. The existing ADO.NET stores are battle-tested and deliberately not
migrated as part of this feature (spec assumption). The two patterns coexist without conflict
because they use different tables and EF Core's `DbContextOptionsBuilder` can be pointed at the
same connection string without interfering with ADO.NET connections.

**Alternatives considered**:
- Pure ADO.NET for all four new entities — rejected because the spec explicitly requires EF Core as
  the new pattern for new features. ADO.NET would not provide a migration path.
- Dapper — rejected for the same reason; the spec names EF Core specifically.
- Separate SQLite file for EF Core tables — rejected; adds operational complexity (two files to
  back up, restore, etc.) with no benefit at this scale.

**Package versions** (EF Core targets .NET 10 compatible releases):
- `Microsoft.EntityFrameworkCore` 9.0.x
- `Microsoft.EntityFrameworkCore.Sqlite` 9.0.x
- `Microsoft.EntityFrameworkCore.Design` 9.0.x (development dependency, not shipped)

---

## EF Core migration strategy: `MigrateAsync` vs `EnsureCreated`

**Decision**: Use EF Core code-first migrations (`dotnet ef migrations add` / `database.MigrateAsync()`).

**Rationale**: Migrations are the production-grade approach. They produce versioned, reversible
schema changes that can be inspected and tracked in source control. `EnsureCreated` would skip the
migrations history table and conflict with future schema changes. The design surface (`MemoryDbContext`)
is clean enough that the initial migration is straightforward.

**Migration execution**: Called in `Program.cs` startup alongside the existing
`SqliteDb.EnsureCreatedAsync()` call:

```csharp
await app.Services.GetRequiredService<MemoryDbContext>().Database.MigrateAsync();
```

This is idempotent — subsequent starts apply no-op if the schema is current.

---

## Tags storage and OR-filtering in SQLite with EF Core

**Decision**: Store tags as a JSON array string in a single `TEXT` column (e.g., `'["database","schema"]'`).
For cross-agent tag search (FR-013), apply a `LIKE '%"tag"%'` filter per requested tag combined
with `OR` semantics.

**Rationale**: SQLite supports `json_each()` for proper JSON array expansion, but EF Core's SQLite
provider does not yet translate `json_each()` into LINQ automatically. Options evaluated:

| Approach | Pro | Con |
|----------|-----|-----|
| `LIKE '%"tag"%'` string match | Simple, no raw SQL needed | Could false-positive match substrings (mitigated by quoting) |
| `EF.Functions.Like` per tag, `OR`-combined | Stays in LINQ | Same substring risk |
| Raw SQL with `json_each()` | Correct JSON semantics | Leaks raw SQL into service layer |
| Load all + filter in memory | Always correct | Not scalable; acceptable at this scale |

**Chosen**: `EF.Functions.Like` with `"%\"tag\"%"` patterns per tag, `OR`-combined in the LINQ
`Where` clause. The `"` quoting means a tag `foo` matches the JSON string `"foo"` and not a
substring `foobar`. At the expected record scale (tens to hundreds per project) this is correct
and fast enough.

**Tag OR semantics** (confirmed by spec and user notes): an entry is returned if it matches *any*
of the requested tags. Each tag produces one `LIKE` predicate; they are `OR`ed together.

---

## Atomic inbox merge

**Decision**: Wrap the merge operation (create Decision + update inbox entry status + set
`MergedDecisionId`) in a single EF Core `SaveChangesAsync` call within an explicit
`IDbContextTransaction`.

**Rationale**: SC-002 requires that partial state (entry merged but no Decision, or Decision created
but entry still pending) never be observable. SQLite WAL mode supports concurrent readers and a
single writer; the transaction guarantees atomicity. EF Core's `Database.BeginTransactionAsync` /
`CommitAsync` / `RollbackAsync` pattern is the standard approach.

**Concurrency guard**: Before creating the Decision, re-read the inbox entry inside the transaction
and verify its status is still `pending`. If it has changed (race condition — another concurrent
merge or reject), throw a `ConflictException` and roll back. This satisfies FR-004.

---

## Inbox idempotency (slug uniqueness per project + agent)

**Decision**: On `POST /api/projects/{id}/decisions/inbox`, query for an existing entry with the
same `ProjectId + AgentName + Slug` before inserting. If found and status is `pending`, update the
mutable fields (title, content, rationale, type) and return `200 OK` with the existing entry's ID.
If found and status is `merged` or `rejected`, return `409 Conflict`.

**Rationale**: FR-006 requires idempotent re-submission. A unique index on `(ProjectId, AgentName,
Slug)` in the EF Core model configuration enforces the constraint at the database level and provides
a fast lookup path.

---

## Session unique constraint per project

**Decision**: A unique index on `(ProjectId, SessionId)` in `SessionContext`. Attempting to insert a
duplicate raises a `DbUpdateException` which the service layer catches and converts to `409 Conflict`
(FR-015).

---

## MemoryDbContext registration and connection string

**Decision**: Register `MemoryDbContext` as a scoped service (EF Core default). The connection
string is derived from `IConfiguration["Database:Path"]` — the same setting used by `SqliteDb` —
via a custom `IDbContextOptionsConfigurator` or inline in `Program.cs`:

```csharp
builder.Services.AddDbContext<MemoryDbContext>(options =>
{
    var path = builder.Configuration["Database:Path"]
        ?? Path.Combine(AppPaths.DataDirectory, "scaffolder.db");
    options.UseSqlite($"Data Source={Path.GetFullPath(path)}");
});
```

**Rationale**: Reusing the same configuration key means the database path is a single point of
configuration for both access patterns, consistent with Principle VI (deployment parity).

---

## Init seeding hook into CastingService

**Decision**: Introduce an `IMemorySeeder` interface with a single method
`SeedCastConfirmationAsync(string projectId, IReadOnlyList<string> agentNames, CancellationToken ct)`.
Inject it into `CastingService`; call it at the end of `ConfirmProposalAsync` after the Squad files
are written. The concrete `MemorySeeder` (implemented in `Memory/MemoryService.cs`) uses
`MemoryDbContext` to insert the three baseline record types (FR-022).

**Rationale**: Using an interface keeps `CastingService` decoupled from the EF Core layer. The call
site is `ConfirmProposalAsync` because that is the single code path that fires after a cast is
committed (the endpoint `POST /api/projects/{id}/casting/proposals/{proposalId}/confirm` calls it).
Seeding is idempotent: if records already exist for the project (re-confirm scenario), the seeder
skips rather than duplicating.

---

## Supersession chain

**Decision**: `Decision.SupersededById` is a nullable `Guid` self-referential FK. When
`PATCH /api/projects/{id}/decisions/{decisionId}` is called with status `superseded` and a
`replacedById` field, the service:
1. Sets `Status = superseded` on the original Decision.
2. Sets `SupersededById = replacedById` on the original Decision.
3. Verifies the replacement Decision exists and belongs to the same project.

Both records remain queryable (FR-010). The `GET /api/projects/{id}/decisions` endpoint returns all
non-archived decisions by default; callers can filter by status.

---

## Phase 2 scope boundary

The following are explicitly **out of scope** for this plan and deferred to a future feature:

- FR-018 to FR-021: Squad file ledger export/import. The `SquadWriter` and `SquadPaths` classes in
  `Scaffolder.Squad` already provide the write primitives; a `LedgerExporter` service consuming
  `MemoryDbContext` and writing via `SquadWriter` is the expected Phase 2 design.
- FR-023: Agent prompt injection snippet. Content is defined in the spec; the injection mechanism
  is owned by the existing agent spawning infrastructure.
