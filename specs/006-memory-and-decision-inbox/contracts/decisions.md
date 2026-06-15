# Contract: Decision Endpoints

**Base path**: `/api/projects/{projectId}/decisions`

All endpoints require a valid API key (existing `ApiKeyAuthMiddleware`). `{projectId}` must be a
valid project ID; a missing or unknown project returns `404 Not Found`.

---

## POST /api/projects/{projectId}/decisions

Create a Decision directly, bypassing the inbox (FR-007). Intended for authorized roles such as
Scribe.

### Request body

```json
{
  "agentName": "Scribe",
  "type": "process",
  "title": "Adopt trunk-based development",
  "content": "# Decision\n\nAll work will merge directly to main...",
  "rationale": "Reduces long-lived branch conflicts.",
  "slug": "adopt-trunk-based-development"
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `agentName` | string | Yes | Non-empty |
| `type` | string | Yes | `architectural` \| `process` \| `scope` \| `technical` |
| `title` | string | Yes | Non-empty; max 500 chars |
| `content` | string | Yes | Non-empty markdown |
| `rationale` | string | Yes | Non-empty |
| `slug` | string | Yes | Kebab-case; max 200 chars |

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `201 Created` | `{ "id": "<guid>", "status": "active" }` | Decision created |
| `400 Bad Request` | `{ "error": "..." }` | Validation failure |
| `404 Not Found` | — | Project not found |

---

## GET /api/projects/{projectId}/decisions

List decisions for the project (FR-008).

### Query parameters

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `type` | string | (all) | Filter by decision type |
| `agent` | string | (all) | Filter by agent name (exact match) |
| `status` | string | `active` | Filter by status (`active` \| `superseded` \| `archived` \| `all`) |

### Response `200 OK`

```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "projectId": "proj-abc",
    "agentName": "Scribe",
    "type": "process",
    "status": "active",
    "title": "Adopt trunk-based development",
    "content": "# Decision\n\n...",
    "rationale": "Reduces long-lived branch conflicts.",
    "slug": "adopt-trunk-based-development",
    "supersededById": null,
    "createdAt": "2026-06-15T22:00:00Z",
    "updatedAt": "2026-06-15T22:00:00Z"
  }
]
```

---

## GET /api/projects/{projectId}/decisions/{decisionId}

Retrieve a single Decision by ID.

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `200 OK` | Single decision object (same shape as list item above) | Found |
| `404 Not Found` | — | Project or decision not found |

---

## PATCH /api/projects/{projectId}/decisions/{decisionId}

Update a Decision's status, optionally linking a superseding Decision (FR-009).

### Request body

All fields are optional; only provided fields are updated.

```json
{
  "status": "superseded",
  "supersededById": "9c7b4e21-3e8a-4d01-bb44-7b2f9d1e3a0c"
}
```

| Field | Type | Required | Constraints |
|-------|------|----------|-------------|
| `status` | string | No | `active` \| `superseded` \| `archived` |
| `supersededById` | Guid | No | Required when `status = superseded`; must be a Decision in the same project |

### Responses

| Status | Body | Condition |
|--------|------|-----------|
| `200 OK` | Updated decision object | Success |
| `400 Bad Request` | `{ "error": "..." }` | Invalid status transition or missing `supersededById` |
| `404 Not Found` | — | Project, decision, or superseding decision not found |

### Status transition rules

| From | To | Permitted? |
|------|----|-----------|
| `active` | `superseded` | Yes — `supersededById` required |
| `active` | `archived` | Yes |
| `superseded` | `archived` | Yes |
| `archived` | any | No — `400 Bad Request` |
| any | `active` | No — `400 Bad Request` |
