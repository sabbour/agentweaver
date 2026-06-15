# Contract: Session Context Endpoints

**Base path**: `/api/projects/{projectId}/sessions`

All endpoints require a valid API key (existing `ApiKeyAuthMiddleware`). `{projectId}` must be a
valid project ID; a missing or unknown project returns `404 Not Found`.

---

## POST /api/projects/{projectId}/sessions

Start a new session context for the project (FR-014).

### Request body

```json
{
  "sessionId": "session-2026-06-15-001",
  "focusArea": "Implement MemoryDbContext and initial migration",
  "activeIssues": ["#42", "#43"],
  "summary": null
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `sessionId` | string | Yes | Non-empty; unique per project (FR-015) |
| `focusArea` | string | Yes | Non-empty |
| `activeIssues` | string[] | No | Default `[]` |
| `summary` | string | No | Optional |

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `201 Created` | `{ "id": "<guid>", "sessionId": "<string>", "startedAt": "<iso8601>" }` | Session started |
| `409 Conflict` | `{ "error": "A session with this ID already exists for the project." }` | Duplicate session ID (FR-015) |
| `400 Bad Request` | `{ "error": "..." }` | Validation failure |
| `404 Not Found` | — | Project not found |

---

## GET /api/projects/{projectId}/sessions

List all sessions for the project, ordered by `StartedAt` descending.

### Query parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `active` | bool | (all) | When `true`, return only sessions where `EndedAt` is null |

### Response `200 OK`

```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "projectId": "proj-abc",
    "sessionId": "session-2026-06-15-001",
    "focusArea": "Implement MemoryDbContext and initial migration",
    "activeIssues": ["#42", "#43"],
    "summary": null,
    "startedAt": "2026-06-15T22:00:00Z",
    "endedAt": null,
    "createdAt": "2026-06-15T22:00:00Z",
    "updatedAt": "2026-06-15T22:00:00Z"
  }
]
```

---

## GET /api/projects/{projectId}/sessions/current

Return the most recently started session that has not yet ended (FR-017).

The "current" session is defined as: `WHERE ProjectId = @id AND EndedAt IS NULL ORDER BY StartedAt DESC LIMIT 1`.

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `200 OK` | Single session object (same shape as list item above) | Active session found |
| `404 Not Found` | — | No active session exists for the project |

---

## PATCH /api/projects/{projectId}/sessions/{sessionId}

Update a session's mutable fields and/or mark it as ended (FR-016).

`{sessionId}` here is the **business key** (`SessionContext.SessionId`), not the surrogate `Id`.

### Request body

All fields are optional; only provided fields are updated.

```json
{
  "focusArea": "Complete MemoryService implementation",
  "activeIssues": ["#42", "#44"],
  "summary": "Completed DbContext and migration. Starting service layer.",
  "endedAt": null
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `focusArea` | string | No | Non-empty if provided |
| `activeIssues` | string[] | No | Replaces the current list |
| `summary` | string | No | |
| `endedAt` | DateTimeOffset | No | When provided, marks the session as ended |

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `200 OK` | Updated session object | Success |
| `400 Bad Request` | `{ "error": "..." }` | Validation failure |
| `404 Not Found` | — | Project or session not found |

### Notes

- Setting `endedAt` to a non-null value marks the session as ended. It will no longer be returned
  by `GET .../sessions/current`.
- Setting `endedAt` on an already-ended session is a no-op (idempotent).
- All other updates are permitted regardless of session state (active or ended).
