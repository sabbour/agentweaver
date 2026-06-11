# Quickstart: Artifact Browser Validation Guide

**Branch**: `001-single-agent-run/artifact-viewer` | **Date**: 2026-06-10

This guide documents runnable validation scenarios that prove the artifact browser feature
works end-to-end. It covers prerequisites, setup, and the expected outcomes for each scenario.
Implementation details belong in `tasks.md`; contracts are in `contracts/artifacts-api.md`.

---

## Prerequisites

- Scaffolder API running locally: `dotnet run --project apps/Scaffolder.Api`
- Web UI running locally: `cd apps/web && npm run dev`
- CLI built: `dotnet build apps/Scaffolder.Cli`
- A local git repository available (e.g., a test repo at `~/testrepo` with at least one branch)
- `SCAFFOLDER_API_URL=http://localhost:5000` and `SCAFFOLDER_API_KEY=<key>` set for CLI tests

---

## Scenario 1 — Artifact Browser Opens Before Any Write (FR-038, SC-013)

**Goal**: Confirm the browser opens immediately on run creation with an empty file tree.

### Setup
Submit a run and immediately open the artifact browser before the agent writes anything.

```bash
# Submit a run (note the run_id)
scaffolder run submit
# Enter repo path, branch, task, model source

# Immediately — open artifact browser (do not wait for run to complete)
scaffolder run artifacts <run-id>
```

### Expected Outcomes
- Artifact tree endpoint returns `200 OK` with `"files": []` and `"is_live": true`
- CLI prints an empty table with header row and no file rows
- Web UI: `ArtifactBrowser` renders with empty file tree and "No files yet" placeholder
- No error or timeout occurs; the browser is fully accessible

---

## Scenario 2 — File Tree Updates Live During Run (FR-038, SC-013)

**Goal**: Confirm the file tree updates within 5 seconds of a write without manual refresh.

### Setup
Submit a run against a repo with source files. Watch it from the Web UI.

### Steps
1. Open `http://localhost:5173/runs/<run-id>` — artifact browser tab visible
2. Wait for agent to execute its first `write_file` call

### Expected Outcomes
- Within 5 seconds of the `write_file` tool.result appearing in the SSE stream, the new or
  modified file appears in the artifact file tree
- File is annotated with the correct status badge (`new`, `modified`, or `deleted`)
- No manual refresh was needed (SC-013)

---

## Scenario 3 — Filter Tabs (FR-035, SC-014)

**Goal**: Confirm each of the four filters returns the correct subset of files.

### Setup
Submit a run, let the agent make several commits and also leave some uncommitted changes.

### Steps
Use the API directly to verify each filter:

```bash
# All files
curl -H "Authorization: Bearer $API_KEY" \
  "http://localhost:5000/api/runs/<run-id>/artifacts?filter=all"

# Committed files only
curl -H "Authorization: Bearer $API_KEY" \
  "http://localhost:5000/api/runs/<run-id>/artifacts?filter=committed"

# Uncommitted files only
curl -H "Authorization: Bearer $API_KEY" \
  "http://localhost:5000/api/runs/<run-id>/artifacts?filter=uncommitted"

# Last-commit files only
curl -H "Authorization: Bearer $API_KEY" \
  "http://localhost:5000/api/runs/<run-id>/artifacts?filter=last-commit"
```

From CLI:
```bash
scaffolder run artifacts <run-id> --filter committed
scaffolder run artifacts <run-id> --filter uncommitted
scaffolder run artifacts <run-id> --filter last-commit
```

### Expected Outcomes
- `all`: union of the committed and uncommitted sets
- `committed`: only files whose changes appear in the worktree branch commits vs the originating branch
- `uncommitted`: only files with staged or working-directory changes vs worktree HEAD; empty if the
  agent commits after every write
- `last-commit`: only the files touched in the most recent worktree commit
- SC-014: 100% accuracy — no extra or missing files in any tab

---

## Scenario 4 — Readonly Diff View with Line Numbers (FR-036, SC-015)

**Goal**: Confirm source file diff is accurate and shows line numbers.

### Steps
Select a modified source file in the artifact browser:

```bash
# Web UI: click the file in the tree
# CLI:
scaffolder run artifacts <run-id>
# Select the source file from the prompt
```

Or fetch via API:
```bash
curl -H "Authorization: Bearer $API_KEY" \
  "http://localhost:5000/api/runs/<run-id>/artifacts/src/utils/format.ts"
```

### Expected Outcomes
- `kind` is `"diff"`, `diff` field contains a valid unified diff
- Web UI: `PerFileDiffViewer` shows additions in green, removals in red, line numbers in gutter
- Line numbers match the actual file positions (hunk headers parse correctly)
- No editing is possible; the view is strictly readonly
- SC-015: diff matches actual worktree changes — compare with `git diff` in the worktree

### Verification
```bash
# From within the worktree directory for the run
cd <WorktreePath>
git diff <originating-branch>..HEAD -- src/utils/format.ts
# Output should match the diff returned by the API
```

---

## Scenario 5 — Markdown File Renders as CommonMark (FR-037)

**Goal**: Confirm Markdown files render as formatted CommonMark, not raw diff.

### Steps
Ensure the agent wrote at least one `.md` file. Select it in the artifact browser.

```bash
# CLI:
scaffolder run artifacts <run-id>
# Select a .md file from the prompt
```

### Expected Outcomes
- `kind` in the API response is `"markdown"`, `content` field contains raw Markdown text
- Web UI: `MarkdownRenderer` renders the content as formatted HTML (headings, bold, lists, code blocks)
- No raw diff lines are shown
- CLI: Markdown text rendered as plain text with structural formatting (headings bold, code in panels)
- No emoji characters in rendered output (NFR-002)

---

## Scenario 6 — Review Interface (FR-039, Story 4 AS-4)

**Goal**: Confirm the artifact browser serves as the primary review interface.

### Steps
1. Wait for run to complete and reach `awaiting_review` status
2. Open `http://localhost:5173/runs/<run-id>` — review interface visible
3. Browse files using artifact browser (filter tabs, select files, inspect diffs)
4. Approve or decline via `ReviewPanel`

### Expected Outcomes
- `is_readonly` is `false` in API response (browser still interactive)
- `is_live` is `false` (run is complete)
- All four filter tabs operational
- Approve and Decline buttons visible and functional in `ReviewPanel`

---

## Scenario 7 — Historical Readonly Mode (FR-040, SC-017)

**Goal**: Confirm browser transitions to readonly after decision.

### Steps
After approving or declining a run:
```bash
# Check via API
curl -H "Authorization: Bearer $API_KEY" \
  "http://localhost:5000/api/runs/<run-id>/artifacts"
```

Open `http://localhost:5173/runs/<run-id>`.

### Expected Outcomes
- `is_readonly: true` in API response
- `is_live: false`
- Web UI: Approve/Decline buttons absent; "Historical view" label shown
- File tree, filter tabs, diff viewer, and Markdown renderer remain fully accessible
- CLI: `scaffolder run artifacts <run-id>` shows file tree; no approval prompt shown
- SC-017: no approval or editing action is accessible

---

## Scenario 8 — CLI/Web UI Full Parity (FR-041, SC-016)

**Goal**: Confirm equivalent functionality from both clients.

### Steps
Complete the full artifact-browser session from the CLI:
```bash
scaffolder run artifacts <run-id>
# Apply all four filter tabs (--filter flag)
# Open a source file → confirm diff renders
# Open a Markdown file → confirm content renders
```

Repeat the same session from the Web UI at `http://localhost:5173/runs/<run-id>`.

### Expected Outcomes
- Both clients show identical file lists for each filter
- Both clients render source file diffs accurately
- Both clients render Markdown files (CLI as plain text, Web UI as HTML)
- SC-016: file counts, statuses, and diff content match across clients

---

## Scenario 9 — Path Traversal Rejection (Research §6 / FR-007)

**Goal**: Confirm artifact file endpoints reject path escape attempts.

```bash
# Path traversal via ..
curl -H "Authorization: Bearer $API_KEY" \
  "http://localhost:5000/api/runs/<run-id>/artifacts/../../../etc/passwd"

# Absolute path (encoded)
curl -H "Authorization: Bearer $API_KEY" \
  "http://localhost:5000/api/runs/<run-id>/artifacts/%2Fetc%2Fpasswd"
```

### Expected Outcomes
- Both requests return `403 Forbidden`
- No file outside the worktree root is read
- The attempt is recorded in the operational log (FR-033 extended to API read paths)

---

## Build and Test Commands

```bash
# Run all tests
dotnet test scaffolders.sln

# Run web UI tests
cd apps/web && npm test

# Build API
dotnet build apps/Scaffolder.Api

# Build CLI
dotnet build apps/Scaffolder.Cli

# Lint web
cd apps/web && npm run lint
```
