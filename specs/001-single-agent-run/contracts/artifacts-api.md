# API Contract: Artifact Browser Endpoints

**Branch**: `001-single-agent-run/artifact-viewer` | **Date**: 2026-06-10

These two endpoints are the complete new surface area added for the artifact browser.
Existing endpoints (`POST /api/runs`, `GET /api/runs/{id}`, `GET /api/runs/{id}/stream`,
`POST /api/runs/{id}/review`) are unchanged.

---

## GET /api/runs/{id}/artifacts

Returns the artifact file tree for a run, scoped to the requested filter.

### Authorization

Bearer token. Caller must be the run's submitting user (same owner check as `GET /api/runs/{id}`).
Returns `403 Forbidden` if the caller does not own the run.

### Path Parameters

| Parameter | Type | Description |
|---|---|---|
| `id` | string (ULID) | Run identifier |

### Query Parameters

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filter` | string | `all` | One of `all`, `committed`, `uncommitted`, `last-commit` (case-insensitive) |

### Responses

#### 200 OK

```json
{
  "run_id": "01HZ7M3K8V0GYABP5RQNF7DTXW",
  "filter": "all",
  "is_live": true,
  "is_readonly": false,
  "files": [
    {
      "path": "src/utils/format.ts",
      "status": "modified",
      "is_markdown": false
    },
    {
      "path": "docs/CHANGELOG.md",
      "status": "added",
      "is_markdown": true
    },
    {
      "path": "src/legacy/old-helper.ts",
      "status": "deleted",
      "is_markdown": false
    }
  ]
}
```

Fields:

| Field | Type | Description |
|---|---|---|
| `run_id` | string | Echoed run identifier |
| `filter` | string | Echoed filter used (`all`, `committed`, `uncommitted`, `last-commit`) |
| `is_live` | bool | `true` while run status is `in_progress`; `false` otherwise |
| `is_readonly` | bool | `true` when run status is `merged`, `declined`, or `merge_failed` |
| `files` | array | Zero or more file entries (empty if no writes yet) |
| `files[].path` | string | Relative path from worktree root, forward slashes, no leading slash |
| `files[].status` | string | `"added"`, `"modified"`, or `"deleted"` |
| `files[].is_markdown` | bool | `true` when file extension is `.md` or `.markdown` |

**Empty-tree case**: when no files have been written yet, `files` is `[]`. The endpoint still
returns `200 OK` with an empty array (not `404`). This is the correct behavior for a run that
has just been submitted (FR-038).

#### 400 Bad Request

```json
{ "error": "Invalid run id." }
{ "error": "Invalid filter value 'foo'. Expected one of: all, committed, uncommitted, last-commit." }
```

#### 403 Forbidden

Caller does not own the run.

#### 404 Not Found

Run does not exist.

#### 503 Service Unavailable

Worktree is provisioned but not yet accessible on disk (transient; client should retry).

```json
{ "error": "Worktree not yet available. The run may still be initializing." }
```

---

## GET /api/runs/{id}/artifacts/{**path}

Returns the diff or raw content of a single file in the run's worktree, relative to the
originating branch.

### Authorization

Same bearer + owner check as above.

### Path Parameters

| Parameter | Type | Description |
|---|---|---|
| `id` | string (ULID) | Run identifier |
| `**path` | string | Relative path of the file (e.g., `src/utils/format.ts`). Decoded from URL. |

The server validates that the decoded path, when resolved against the worktree root, does not escape
that root (path traversal protection, FR-007 / research §6). If the resolved path is outside the
worktree, `403 Forbidden` is returned.

### Responses

#### 200 OK — Source File (diff)

```json
{
  "path": "src/utils/format.ts",
  "kind": "diff",
  "diff": "diff --git a/src/utils/format.ts b/src/utils/format.ts\nindex abc1234..def5678 100644\n--- a/src/utils/format.ts\n+++ b/src/utils/format.ts\n@@ -10,7 +10,9 @@\n ...",
  "content": null
}
```

#### 200 OK — Markdown File (content)

```json
{
  "path": "docs/CHANGELOG.md",
  "kind": "markdown",
  "diff": null,
  "content": "# Changelog\n\n## v2.0.0\n\n- Added feature X\n"
}
```

Fields:

| Field | Type | Description |
|---|---|---|
| `path` | string | Echoed relative path |
| `kind` | string | `"diff"` for source files, `"markdown"` for `.md`/`.markdown` files |
| `diff` | string \| null | Unified diff against originating branch; non-null only when `kind == "diff"` |
| `content` | string \| null | Raw file content; non-null only when `kind == "markdown"` |

**Deleted files**: `diff` contains the deletion diff (all lines prefixed `-`). `content` is null.

#### 400 Bad Request

```json
{ "error": "Invalid run id." }
{ "error": "File path must not be empty." }
```

#### 403 Forbidden

Caller does not own the run, or path resolves outside the worktree root.

#### 404 Not Found

Run does not exist, or the specified file is not present in the worktree diff.

---

## CLI Command Contract

```
scaffolder run artifacts <run-id> [--filter <filter>]
```

| Argument / Flag | Type | Default | Description |
|---|---|---|---|
| `<run-id>` | string | required | Run identifier |
| `--filter` | string | `all` | One of `all`, `committed`, `uncommitted`, `last-commit` |

Exit codes:
- `0` — success (file viewed or tree printed)
- `1` — API error or run not found
- `2` — path traversal attempt detected (should not occur in normal use)

---

## Data Flow: Live Update

```
Agent writes a file
    → write_file tool.result emitted into SSE stream
    → client receives tool.result, identifies paired tool.call as write_file
    → client calls GET /api/runs/{id}/artifacts?filter={active}
    → file tree updated in UI
    → (if a file was already selected) client calls GET /api/runs/{id}/artifacts/{path}
    → file panel updated
```

Round-trip time target (SC-013): ≤ 5 seconds from write to visible update.
Typical observed latency: < 1 s (SSE delivery) + < 300 ms (artifact REST call) = well within target.

---

## State Machine: Artifact Browser Modes

```
Run status          → is_live   → is_readonly   → Browser mode
─────────────────────────────────────────────────────────────────
pending             → false     → false         → Empty tree (worktree not provisioned)
in_progress         → true      → false         → Live mode (tree updates on write_file)
completed           → false     → false         → Static mode (review not yet requested)
awaiting_review     → false     → false         → Review mode (approve/decline available)
merging             → false     → false         → Review mode (approval in flight)
merged              → false     → true          → Historical readonly mode
declined            → false     → true          → Historical readonly mode
merge_failed        → false     → true          → Historical readonly mode (worktree preserved)
failed              → false     → false         → Error mode (diff/content unavailable if worktree removed)
```
