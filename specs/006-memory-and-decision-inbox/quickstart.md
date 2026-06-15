# Quickstart: Validate Memory and Decision Inbox

**Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Date**: 2026-06-15

This guide covers the runnable validation scenarios that prove the feature works end-to-end.
It references contracts and data-model details rather than repeating them. All scenarios use the
existing API runner (`start-dev.ps1`) and curl or any HTTP client.

---

## Prerequisites

1. **.NET 10 SDK** installed (matches `global.json`).
2. EF Core CLI tools installed globally:
   ```powershell
   dotnet tool install --global dotnet-ef
   ```
3. The project has been built and the migration applied:
   ```powershell
   cd apps/Scaffolder.Api
   dotnet ef database update
   ```
4. The API is running locally:
   ```powershell
   .\start-dev.ps1
   # or
   cd apps/Scaffolder.Api && dotnet run
   ```
5. A project exists. Replace `{PROJECT_ID}` throughout with the ID returned by
   `POST /api/projects`.
6. A valid API key is set in the `X-Api-Key` header (see `appsettings.Development.json`).

---

## Scenario 1: Agent Records a Decision (User Story 1)

Validates FR-001, FR-003, FR-005, and SC-002 (atomic merge).

### Step 1 — Submit a draft to the inbox

```bash
curl -s -X POST http://localhost:5000/api/projects/{PROJECT_ID}/decisions/inbox \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{
    "agentName": "Ralph",
    "slug": "use-ef-core-for-new-stores",
    "title": "Use EF Core for all new stores",
    "content": "All new data stores will use EF Core with the SQLite provider.",
    "rationale": "Reduces ADO.NET boilerplate and enables code-first migrations.",
    "decisionType": "architectural"
  }'
```

**Expected**: `201 Created`, body `{ "id": "<entryId>", "status": "pending" }`. Note `<entryId>`.

### Step 2 — Merge the inbox entry to a Decision

```bash
curl -s -X POST http://localhost:5000/api/projects/{PROJECT_ID}/decisions/inbox/{entryId}/merge \
  -H "X-Api-Key: dev-key"
```

**Expected**: `200 OK`, body `{ "decisionId": "<decisionId>", "mergedAt": "<iso8601>" }`. Note `<decisionId>`.

### Step 3 — Verify the Decision exists

```bash
curl -s http://localhost:5000/api/projects/{PROJECT_ID}/decisions \
  -H "X-Api-Key: dev-key"
```

**Expected**: Array containing the finalized decision with `status: "active"`, correct type and agent attribution.

### Step 4 — Verify the inbox entry is marked merged

```bash
curl -s "http://localhost:5000/api/projects/{PROJECT_ID}/decisions/inbox?status=merged" \
  -H "X-Api-Key: dev-key"
```

**Expected**: Entry with `status: "merged"` and `mergedDecisionId` matching `<decisionId>`.

### Step 5 — Verify double-merge returns 409

```bash
curl -s -o /dev/null -w "%{http_code}" \
  -X POST http://localhost:5000/api/projects/{PROJECT_ID}/decisions/inbox/{entryId}/merge \
  -H "X-Api-Key: dev-key"
```

**Expected**: `409`.

### Step 6 — Reject a different pending entry

```bash
# First submit another entry
curl -s -X POST http://localhost:5000/api/projects/{PROJECT_ID}/decisions/inbox \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{
    "agentName": "Ralph",
    "slug": "use-dapper-instead",
    "title": "Use Dapper instead",
    "content": "...",
    "rationale": "...",
    "decisionType": "architectural"
  }'

# Then reject it — use the returned id
curl -s -X DELETE http://localhost:5000/api/projects/{PROJECT_ID}/decisions/inbox/{entry2Id} \
  -H "X-Api-Key: dev-key"
```

**Expected**: `204 No Content`.

---

## Scenario 2: Agent Persists and Recalls Memory (User Story 2)

Validates FR-011, FR-012, FR-013, SC-007 (no false positives).

### Step 1 — Record a memory entry

```bash
curl -s -X POST http://localhost:5000/api/projects/{PROJECT_ID}/agents/Ralph/memory \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{
    "type": "learning",
    "importance": "high",
    "content": "EF Core with SQLite performs best with WAL journal mode enabled.",
    "tags": ["database", "ef-core", "sqlite"]
  }'
```

**Expected**: `201 Created`, body `{ "id": "<memoryId>" }`.

### Step 2 — Retrieve it by agent with type + importance filter

```bash
curl -s "http://localhost:5000/api/projects/{PROJECT_ID}/agents/Ralph/memory?type=learning&importance=high" \
  -H "X-Api-Key: dev-key"
```

**Expected**: Array with exactly one entry matching the posted values.

### Step 3 — Verify an agent with no records returns an empty array (not 404)

```bash
curl -s -o /dev/null -w "%{http_code}" \
  http://localhost:5000/api/projects/{PROJECT_ID}/agents/NonExistentAgent/memory \
  -H "X-Api-Key: dev-key"
```

**Expected**: `200` with body `[]`.

### Step 4 — Cross-agent tag search

```bash
# Add a second memory entry for a different agent
curl -s -X POST http://localhost:5000/api/projects/{PROJECT_ID}/agents/Scribe/memory \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{
    "type": "pattern",
    "importance": "medium",
    "content": "Use index on (ProjectId, AgentName) for all memory queries.",
    "tags": ["database", "performance"]
  }'

# Cross-agent search for tag "sqlite" OR "performance"
curl -s "http://localhost:5000/api/projects/{PROJECT_ID}/memory?tags=sqlite,performance" \
  -H "X-Api-Key: dev-key"
```

**Expected**: Two entries returned — Ralph's (`sqlite` tag match) and Scribe's (`performance` tag match). No entries for unrelated tags are included (SC-007).

---

## Scenario 3: Session Context Tracking (User Story 3)

Validates FR-014, FR-015, FR-016, FR-017.

### Step 1 — Start a session

```bash
curl -s -X POST http://localhost:5000/api/projects/{PROJECT_ID}/sessions \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{
    "sessionId": "session-2026-06-15-001",
    "focusArea": "Implement MemoryDbContext",
    "activeIssues": ["#42", "#43"]
  }'
```

**Expected**: `201 Created`, body includes `startedAt`, no `endedAt`.

### Step 2 — Retrieve current session

```bash
curl -s http://localhost:5000/api/projects/{PROJECT_ID}/sessions/current \
  -H "X-Api-Key: dev-key"
```

**Expected**: `200 OK`, session with `sessionId: "session-2026-06-15-001"` and `endedAt: null`.

### Step 3 — Verify duplicate session ID returns 409

```bash
curl -s -o /dev/null -w "%{http_code}" \
  -X POST http://localhost:5000/api/projects/{PROJECT_ID}/sessions \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{
    "sessionId": "session-2026-06-15-001",
    "focusArea": "Duplicate attempt",
    "activeIssues": []
  }'
```

**Expected**: `409`.

### Step 4 — End the session

```bash
curl -s -X PATCH http://localhost:5000/api/projects/{PROJECT_ID}/sessions/session-2026-06-15-001 \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{ "endedAt": "2026-06-15T23:00:00Z" }'
```

**Expected**: `200 OK`, body includes `endedAt: "2026-06-15T23:00:00Z"`.

### Step 5 — Verify current session is now 404

```bash
curl -s -o /dev/null -w "%{http_code}" \
  http://localhost:5000/api/projects/{PROJECT_ID}/sessions/current \
  -H "X-Api-Key: dev-key"
```

**Expected**: `404`.

---

## Scenario 4: Project Initialization Seeds Baseline Memory (User Story 5)

Validates FR-022, SC-006. This scenario requires creating a project and confirming a cast.

### Step 1 — Confirm a cast proposal

Follow the existing cast workflow (see `apps/Scaffolder.Api/API.md`) to propose and confirm a cast
for a new project. The `POST /api/projects/{id}/casting/proposals/{proposalId}/confirm` call will
trigger `MemorySeeder.SeedCastConfirmationAsync`.

### Step 2 — Verify baseline records exist

```bash
# One SessionContext with sessionId "initial"
curl -s "http://localhost:5000/api/projects/{PROJECT_ID}/sessions?active=true" \
  -H "X-Api-Key: dev-key"
# Expected: array with one session, sessionId = "initial", endedAt = null

# One AgentMemory of type core_context per agent (N agents = N records)
curl -s "http://localhost:5000/api/projects/{PROJECT_ID}/memory?type=core_context" \
  -H "X-Api-Key: dev-key"
# Expected: N entries, one per agent in the cast, all importance = "high"

# One genesis Decision of type "process"
curl -s "http://localhost:5000/api/projects/{PROJECT_ID}/decisions?type=process" \
  -H "X-Api-Key: dev-key"
# Expected: one decision with slug starting with "team-formation-", agentName = "system"
```

### Step 3 — Verify re-confirm does not duplicate

Confirm the same proposal again (or a new proposal on the same project). Verify the record counts
do not increase.

---

## Scenario 5: Supersession Chain (Edge Case)

Validates FR-009, FR-010.

```bash
# Create a decision
curl -s -X POST http://localhost:5000/api/projects/{PROJECT_ID}/decisions \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{ "agentName": "Scribe", "type": "architectural", "title": "Use PostgreSQL", "content": "...", "rationale": "...", "slug": "use-postgresql" }'
# Note originalId

# Create the replacement
curl -s -X POST http://localhost:5000/api/projects/{PROJECT_ID}/decisions \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{ "agentName": "Scribe", "type": "architectural", "title": "Use SQLite instead", "content": "...", "rationale": "...", "slug": "use-sqlite-instead" }'
# Note replacementId

# Supersede the original
curl -s -X PATCH http://localhost:5000/api/projects/{PROJECT_ID}/decisions/{originalId} \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-key" \
  -d '{ "status": "superseded", "supersededById": "{replacementId}" }'
```

**Expected**: `200 OK`. Querying `GET .../decisions?status=all` shows both records — original with
`status: "superseded"` and `supersededById` set, replacement with `status: "active"`.
