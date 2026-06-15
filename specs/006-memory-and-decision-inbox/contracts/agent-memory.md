# Contract: Agent Memory Endpoints

**Base paths**:
- Per-agent: `/api/projects/{projectId}/agents/{agentName}/memory`
- Cross-agent: `/api/projects/{projectId}/memory`

All endpoints require a valid API key (existing `ApiKeyAuthMiddleware`). `{projectId}` must be a
valid project ID; a missing or unknown project returns `404 Not Found`.

---

## POST /api/projects/{projectId}/agents/{agentName}/memory

Record a memory entry for a named agent (FR-011).

### Path parameters

| Parameter | Description |
|-----------|-------------|
| `agentName` | Agent name (URL-encoded if it contains spaces, though agent names are typically single words) |

### Request body

```json
{
  "type": "learning",
  "importance": "high",
  "content": "EF Core with SQLite requires WAL mode for concurrent access.",
  "tags": ["database", "ef-core", "sqlite"],
  "sessionReference": "session-2026-06-15-001"
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `type` | string | Yes | `core_context` \| `learning` \| `update` \| `pattern` |
| `importance` | string | Yes | `low` \| `medium` \| `high` |
| `content` | string | Yes | Non-empty markdown |
| `tags` | string[] | No | Default `[]`; elements must be non-empty; duplicates deduplicated |
| `sessionReference` | string | No | Optional free-text reference to a session ID |

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `201 Created` | `{ "id": "<guid>" }` | Memory entry recorded |
| `400 Bad Request` | `{ "error": "..." }` | Validation failure |
| `404 Not Found` | — | Project not found |

---

## GET /api/projects/{projectId}/agents/{agentName}/memory

List all memory entries for a named agent on the project (FR-012).

### Query parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `type` | string | (all) | Filter by memory type |
| `importance` | string | (all) | Filter by importance level |

### Response `200 OK`

Returns an empty array `[]` if the agent has no memory entries (not an error — spec edge case).

```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "projectId": "proj-abc",
    "agentName": "Ralph",
    "type": "learning",
    "importance": "high",
    "content": "EF Core with SQLite requires WAL mode for concurrent access.",
    "tags": ["database", "ef-core", "sqlite"],
    "sessionReference": "session-2026-06-15-001",
    "createdAt": "2026-06-15T22:00:00Z",
    "updatedAt": "2026-06-15T22:00:00Z"
  }
]
```

---

## GET /api/projects/{projectId}/memory

Search all memory entries across all agents on the project (FR-013). Supports tag-based and
type-based filtering.

### Query parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `tags` | string (comma-separated) | (all) | OR-filter: returns entries matching **any** of the provided tags |
| `type` | string | (all) | Filter by memory type (AND with tags if both provided) |

**Tag OR semantics**: `tags=database,schema` returns entries that have `database` OR `schema` in
their tags list. Both filters (`tags` AND `type`) are applied together when both are provided:
entries must match any tag AND match the type.

**Example**: `GET /api/projects/proj-abc/memory?tags=database,schema&type=learning`
Returns all `learning` memories that contain the tag `database` OR the tag `schema`.

### Response `200 OK`

Same shape as the per-agent memory list above. Includes `agentName` to identify the source.

### No false positives (SC-007)

Tag matching uses `LIKE '%"<tag>"%'` (double-quoted) to prevent substring collisions. A tag `foo`
will not match an entry tagged `foobar`.
