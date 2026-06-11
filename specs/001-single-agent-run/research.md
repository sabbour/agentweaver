# Research: Artifact Browser (User Story 5, FR-034–FR-041)

**Branch**: `001-single-agent-run/artifact-viewer` | **Date**: 2026-06-10

---

## 1. LibGit2Sharp — Artifact Tree and Per-File Diff

### Decision
Use LibGit2Sharp (already a dependency via `Scaffolder.Api.Git.WorktreeManager`) for all
four filter queries and per-file diff extraction. No new git library needed.

### Filter Queries

| Filter | LibGit2Sharp approach |
|---|---|
| **All** | Union of committed diff (origin tip tree → worktree HEAD tree) plus `RetrieveStatus()` for Index*/Workdir* entries not yet in HEAD |
| **Committed** | `repo.Diff.Compare<TreeChanges>(originTip.Tree, worktreeHead.Tree)` — produces add/modify/delete/rename entries |
| **Uncommitted** | `repo.RetrieveStatus(StatusOptions { IncludeUntracked = true })` filtered for Index* or Workdir* flags |
| **Last commit** | If `worktreeHead.Parents.Count > 0`: `repo.Diff.Compare<TreeChanges>(worktreeHead.Parents.First().Tree, worktreeHead.Tree)`; else empty list |

All operations are performed on the worktree repository (opening a `Repository(worktreePath)` instance,
not the bare origin repo), so they correctly reflect the worktree's own HEAD and working directory.

### Per-File Diff

`repo.Diff.Compare<Patch>(oldTree, newTree, new[] { relativePath })` produces a single-file unified
diff. For uncommitted changes (file modified in working tree but not yet committed), generate a patch
from the worktree index/working-directory against the last committed state of that file using
`repo.Diff.Compare<Patch>(repo.Head.Tip.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory, paths)`.

### Rationale
LibGit2Sharp is already vendored, proven in `WorktreeManager`, and exposes exactly the primitives
needed. No additional NuGet dependency required on the backend.

### Alternatives Considered
- `git` CLI subprocess: rejected — slower, platform-dependent path quoting, no handle-level safety
- `libgit2` P/Invoke directly: rejected — LibGit2Sharp is the managed wrapper already in use

---

## 2. CommonMark Rendering — Web UI

### Decision
Use the already-installed `react-markdown@10.1.0` + `remark-gfm@4.0.1` + `rehype-sanitize@6.0.0`
packages. No additional npm dependencies required.

### Usage Pattern
```tsx
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';

<ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
  {markdownContent}
</ReactMarkdown>
```

`rehype-sanitize` is mandatory to prevent XSS from agent-written Markdown (Principles IX and X).
`remark-gfm` adds tables, strikethrough, and task lists beyond CommonMark baseline.

### Rationale
These packages are already in `package.json`; no install step needed. `react-markdown` is the
de-facto standard for safe CommonMark rendering in React. Applying `rehype-sanitize` is a
non-negotiable security gate (FR-026 / SC-009) since the content comes from the agent.

### Alternatives Considered
- `marked` + `DOMPurify`: rejected — two packages where one (`react-markdown` + `rehype-sanitize`) suffices
- Raw `dangerouslySetInnerHTML`: rejected — XSS risk; explicitly prohibited by Principle IX

---

## 3. CommonMark Rendering — CLI

### Decision
Add `Markdig` NuGet package (v0.40, pure managed .NET, no native dependencies) to
`Scaffolder.Cli.csproj`. Use `Markdig.Extensions.AnsiMarkdownRenderer` or a custom plaintext
renderer to render Markdown into Spectre.Console-compatible markup for terminal output.

Markdig ships a `Markdig.Renderers.TextRenderer` for plain text. The CLI renders Markdown files as
plain text with structural hints: headings printed bold, code blocks in monospace, list items prefixed
with `-`. This gives a readable view without requiring a browser.

### Rationale
`Markdig` is the canonical CommonMark + GFM parser for .NET, widely used in the ecosystem
(PowerShell docs, NuGet documentation), MIT-licensed, and has zero native dependencies — safe for
cross-platform (Windows ARM64, Linux cloud). No alternative managed CommonMark parser is as
well-maintained.

### Alternatives Considered
- `CommonMark.NET`: less actively maintained; fewer extensions
- Raw text passthrough (no rendering): rejected — violates FR-037 ("rendered as formatted CommonMark")
  for the CLI client requirement of FR-041 (CLI/Web UI parity)

---

## 4. Live Update Mechanism

### Decision
Reuse the existing SSE stream (`GET /api/runs/{id}/stream`). The client refreshes the artifact tree
whenever a `tool.result` event arrives whose `toolName` in the preceding `tool.call` (looked up via
`callId`) is `"write_file"`. No new event type is introduced.

This satisfies SC-013 (≤5 seconds): SSE delivery latency is typically < 1 s; the HTTP refetch for
the artifact tree is a cheap local git status call (< 200 ms on a typical repo).

### Rationale
Introducing a dedicated `artifact.updated` SSE event was considered but rejected: it would require
the agent tool layer to emit a new event type, coupling the artifact browser to the tool
implementation. The `tool.result` approach is purely client-side logic over the already-available
stream.

### Alternatives Considered
- Polling on a timer (e.g., every 2 s): rejected — wastes server resources; may miss rapid writes
- WebSocket push for artifact updates: rejected — overkill; SSE stream already carries all needed signals

---

## 5. Line Numbers in Diff View

### Decision
Parse unified diff hunk headers client-side to derive line numbers. The header format is
`@@ -<old_start>,<old_count> +<new_start>,<new_count> @@`. Track two running counters (old line,
new line) and render a two-column gutter alongside each diff line.

Implementation lives in a pure function `parseDiffLines(rawDiff: string): DiffLine[]` where
`DiffLine` carries `{ kind: '+' | '-' | ' ' | 'hunk' | 'header', content, oldLineNo?, newLineNo? }`.
The existing `DiffViewer` component is extended to `PerFileDiffViewer` which accepts a `fileDiff`
string and renders the gutter.

### Rationale
Unified diff line-number parsing is straightforward and avoids a third-party diff library. The
existing `DiffViewer` already splits on `\n` and classifies lines — the extension is additive.

### Alternatives Considered
- `diff2html` npm library: rejected — adds a large dependency for functionality achievable in ~60 lines
- Server-side pre-computed line numbers: rejected — adds API complexity for a pure presentation concern

---

## 6. Availability Gating for Artifact Endpoints

### Decision
Artifact tree and per-file content endpoints are available for any run whose worktree has been
provisioned (i.e., `run.WorktreePath != null`). This covers all non-`Pending` statuses except `Failed`
runs where the worktree was cleaned up. Specifically: `InProgress`, `Completed`, `AwaitingReview`,
`Merging`, `Merged`, `MergeFailed`, `Declined`.

Security: per-file content endpoints are subject to the same owner-authentication gate as all other
run endpoints (FR-027). Path parameters for `GET /api/runs/{id}/artifacts/{path}` are validated
against the worktree root using the existing `SandboxPathValidator` to prevent path traversal
(FR-007 extended to API read paths).

Content-safety: the `write_file` tool already applies content-safety checks before writing to disk
(FR-025). Serving on-disk content through the artifact API therefore does not re-expose safety-failed
content — safety-failed content never reaches disk. Runs that ended in `Failed` due to a safety
failure have no worktree content to serve.

### Rationale
The spec (FR-038) requires the browser to be available from run creation. A `Pending` run has no
worktree yet, so the API correctly returns an empty-but-accessible artifact tree for it by treating
`WorktreePath == null` as "tree exists, zero files".

### Alternatives Considered
- Gate on same statuses as current `Diff` field (AwaitingReview+): rejected — violates FR-038
- Serve all content always, no gating: rejected — violates FR-040 (readonly historical mode) and
  the content-safety architecture (FR-025/FR-026)

---

## 7. Readonly Mode After Decision

### Decision
Readonly mode is enforced by the **API** (no approval action permitted after `Merged`/`Declined`/
`MergeFailed`) and reflected in the **UI** (approval/decline buttons hidden, browser header shows
"historical view" label). The artifact tree and file views remain fully accessible in readonly mode
(FR-040).

The client derives readonly mode from `run.status` (one of `'merged' | 'declined' | 'merge_failed'`).
No new API field required.

---

## 8. CLI Interactive TUI Pattern

### Decision
`scaffolder run artifacts <run-id> [--filter <all|committed|uncommitted|last-commit>]`

Uses an interactive loop:
1. Display file tree as a `Spectre.Console.Table` with status column
2. Use `SelectionPrompt` to choose a file (or `[back]` to re-filter / exit)
3. Show diff or Markdown content in a `Panel`
4. Loop back to step 2

Non-interactive mode (when stdout is not a TTY): print the file tree table and exit. This matches
the existing pattern in `RunCommands.ShowAsync`.

### Rationale
`Spectre.Console` is already the CLI rendering library. `SelectionPrompt` provides keyboard-navigable
menus without additional dependencies. The pattern matches `RunCommands.SubmitAsync`'s prompt style.
