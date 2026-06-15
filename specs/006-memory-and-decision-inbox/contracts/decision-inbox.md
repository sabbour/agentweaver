# Contract: Decision Inbox Endpoints

**Base path**: `/api/projects/{projectId}/decisions/inbox`

All endpoints require a valid API key (existing `ApiKeyAuthMiddleware`). `{projectId}` must be a
valid project ID; a missing or unknown project returns `404 Not Found`.

---

## POST /api/projects/{projectId}/decisions/inbox

Submit a draft decision to the inbox. Idempotent by `(projectId, agentName, slug)`.

### Request body

```json
{
  "agentName": "Ralph",
  "slug": "use-ef-core-for-new-stores",
  "title": "Use EF Core for all new data stores",
  "content": "# Decision\n\nAll new stores will use EF Core...",
  "rationale": "Reduces boilerplate and enables migrations.",
  "decisionType": "architectural"
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `agentName` | string | Yes | Non-empty |
| `slug` | string | Yes | Kebab-case (`^[a-z0-9]+(-[a-z0-9]+)*$`); max 200 chars |
| `title` | string | Yes | Non-empty; max 500 chars |
| `content` | string | Yes | Non-empty markdown |
| `rationale` | string | Yes | Non-empty |
| `decisionType` | string | Yes | `architectural` \| `process` \| `scope` \| `technical` |

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `201 Created` | `{ "id": "<guid>", "status": "pending" }` | New entry created |
| `200 OK` | `{ "id": "<guid>", "status": "pending" }` | Existing pending entry updated (idempotent re-submit) |
| `409 Conflict` | `{ "error": "..." }` | Entry with this slug already merged or rejected |
| `400 Bad Request` | `{ "error": "..." }` | Validation failure |
| `404 Not Found` | — | Project not found |

---

## GET /api/projects/{projectId}/decisions/inbox

List inbox entries for the project.

### Query parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `agent` | string | (all) | Filter by agent name (exact match) |
| `type` | string | (all) | Filter by decision type |
| `status` | string | `pending` | Filter by status (`pending` \| `merged` \| `rejected` \| `all`) |

### Response `200 OK`

```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "projectId": "proj-abc",
    "agentName": "Ralph",
    "slug": "use-ef-core-for-new-stores",
    "title": "Use EF Core for all new data stores",
    "content": "# Decision\n\n...",
    "rationale": "Reduces boilerplate...",
    "decisionType": "architectural",
    "status": "pending",
    "mergedAt": null,
    "mergedDecisionId": null,
    "createdAt": "2026-06-15T22:00:00Z",
    "updatedAt": "2026-06-15T22:00:00Z"
  }
]
```

---

## POST /api/projects/{projectId}/decisions/inbox/{entryId}/merge

Atomically promote a pending inbox entry to a finalized Decision. The inbox entry transitions to
`merged` and the new Decision ID is returned.

### Request body

No body required. The Decision is created from the inbox entry's fields. Optional override fields
may be supplied:

```json
{
  "titleOverride": null,
  "contentOverride": null
}
```

Both fields are optional and null by default (entry's original fields are used).

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `200 OK` | `{ "decisionId": "<guid>", "mergedAt": "<iso8601>" }` | Merge succeeded |
| `409 Conflict` | `{ "error": "Inbox entry is not in pending status." }` | Entry already merged or rejected |
| `404 Not Found` | — | Project or entry not found |

---

## DELETE /api/projects/{projectId}/decisions/inbox/{entryId}

Reject (soft-delete) a pending inbox entry. Sets status to `rejected`. No Decision record is
created.

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `204 No Content` | — | Entry rejected |
| `409 Conflict` | `{ "error": "Inbox entry is not in pending status." }` | Already merged or rejected |
| `404 Not Found` | — | Project or entry not found |
