# Implementation Plan: Artifact Browser

**Branch**: `001-single-agent-run/artifact-viewer` | **Date**: 2026-06-10 | **Spec**: `specs/001-single-agent-run/spec.md`

**Scope**: User Story 5 — FR-034–FR-041, SC-013–SC-017

**Input**: Feature specification at `specs/001-single-agent-run/spec.md` §§ User Story 5, FR-034–FR-041, Success Criteria SC-013–SC-017, Clarifications 2026-06-10

---

## Summary

The artifact browser gives users a live, navigable view of everything a run has written —
from the moment the run is created, through the review decision, and into permanent history.
It replaces the flat unified diff that the current `ReviewPanel` presents with a proper
file-tree panel, four filter tabs, a readonly per-file diff view with line numbers for source
files, and CommonMark rendering for Markdown files. Both the CLI and the Web UI expose
equivalent functionality.

**Approach:**

1. **Two new REST endpoints** in `Scaffolder.Api`:
   - `GET /api/runs/{id}/artifacts?filter=<filter>` — returns the file tree
   - `GET /api/runs/{id}/artifacts/{**path}` — returns per-file diff or content

2. **`WorktreeManager` additions** — four new LibGit2Sharp queries (already-vendored) to
   implement the All / Committed / Uncommitted / Last-commit filters and per-file diff.

3. **Web UI** — new `ArtifactBrowser` component family integrated into `WatchPage` /
   `RunWatcher`. Live updates driven by the existing SSE stream (re-fetch on `write_file`
   `tool.result`). CommonMark rendering uses already-installed `react-markdown` +
   `remark-gfm` + `rehype-sanitize`.

4. **CLI** — new `scaffolder run artifacts` subcommand with an interactive
   `SelectionPrompt` loop. Markdown rendered via `Markdig` (new NuGet dependency).

The browser requires no new database columns, no new event types, and no new streaming
infrastructure — it is a pure read layer over the existing worktree.

---

## Technical Context

**Language/Version**: C# / .NET 10 (backend); TypeScript / React 19 (web frontend)

**Primary Dependencies**:
- `LibGit2Sharp` (already vendored in `Scaffolder.Api`) — all new git queries
- `react-markdown@10.1.0` + `remark-gfm@4.0.1` + `rehype-sanitize@6.0.0`
  (all already installed in `apps/web`) — CommonMark rendering
- `Markdig` v0.40 (new NuGet, pure managed .NET) — CLI Markdown rendering
- `Spectre.Console` (already in `Scaffolder.Cli`) — CLI TUI for artifacts command
- `@fluentui/react-components@9.74.1` + `@fluentui/react-icons@2.0.328`
  (already installed) — Web UI components

**Storage**: No new tables or columns. The `Run` entity already persists `WorktreePath`
and `WorktreeBranch`; the artifact tree is computed on demand from git state.

**Testing**: xUnit (existing backend test project); Vitest (existing frontend test setup)

**Target Platform**: Windows ARM64 primary; Linux (cloud) secondary — same binary, same
LibGit2Sharp paths that `WorktreeManager` already uses

**Project Type**: Feature layer over existing backend (new API surface) + new UI components

**Performance Goals**:
- Artifact tree endpoint: ≤ 300 ms p95 (local git status call on a typical repo)
- Per-file diff endpoint: ≤ 200 ms p95 (single-file patch via LibGit2Sharp)
- SC-013 (live update latency): ≤ 5 s end-to-end after write (SSE < 1 s + REST < 300 ms)

**Constraints**:
- No new event types on the SSE stream; live updates driven by existing `tool.result` events
- No new database migrations; worktree path already persisted on `Run`
- `rehype-sanitize` is mandatory on all Markdown rendering (Principle IX / FR-026)
- All paths to artifact file endpoints validated by `SandboxPathValidator` (FR-007)

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design — no violations found.*

| Principle | Check | Status |
|---|---|---|
| I — Agent Runtime (MAF) | No agent logic added; artifact browser is a pure read layer | PASS |
| II — Model Sources | No new model-source logic | PASS |
| III — API-First | All artifact data served via new API endpoints; both clients are thin over those endpoints | PASS |
| IV — Two Clients at Parity | CLI (`scaffolder run artifacts`) and Web UI (`ArtifactBrowser`) both expose full tree + filter tabs + diff + Markdown rendering (FR-041, SC-016) | PASS |
| V — Observable Runs | Live update uses existing SSE `tool.result` events; no new event types needed | PASS |
| VI — Deployment Parity | LibGit2Sharp works identically on Windows ARM64 and Linux; `Markdig` is pure managed .NET | PASS |
| VII — No Mocks | All git queries use real LibGit2Sharp calls against the real worktree; no stubs anywhere | PASS |
| VIII — No Emojis | All new components and CLI output use text-only labels (status: "new", "modified", "deleted") | PASS |
| IX — Responsible AI | `rehype-sanitize` mandatory on Markdown rendering; artifact endpoints owner-gated; path traversal rejected | PASS |
| X — Safe Execution | Artifact endpoints are readonly; path validation via `SandboxPathValidator`; no new mutation surface | PASS |
| XI — Agent Governance Toolkit | No new governance or policy logic; no MAF changes | PASS |

---

## Project Structure

### Documentation (this feature)

```text
specs/001-single-agent-run/
├── plan.md              # This file
├── research.md          # Phase 0 output (resolved; see research.md)
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/
│   └── artifacts-api.md # Phase 1 output — API contract
└── tasks.md             # Phase 2 output (/speckit.tasks command — not yet created)
```

### Source Code Changes

```text
packages/Scaffolder.Domain/          (unchanged)

packages/Scaffolder.SandboxFs/       (unchanged — SandboxPathValidator already exists)

apps/Scaffolder.Api/
├── Artifacts/                        (NEW directory)
│   ├── ArtifactFilter.cs             (NEW — enum: All, Committed, Uncommitted, LastCommit)
│   ├── ArtifactFileStatus.cs         (NEW — enum: Added, Modified, Deleted)
│   ├── ArtifactFile.cs               (NEW — record: Path, Status, IsMarkdown)
│   ├── ArtifactQueryService.cs       (NEW — IArtifactQueryService + implementation)
│   └── ArtifactFilterParser.cs       (NEW — query-string → ArtifactFilter)
├── Contracts/
│   └── Dtos.cs                       (EXTEND — add ArtifactTreeDto, ArtifactFileDto,
│                                                ArtifactFileContentDto)
├── Git/
│   └── WorktreeManager.cs            (EXTEND — add GetArtifactFiles, GetArtifactFileContent)
└── Program.cs                        (EXTEND — register 2 new endpoints + ArtifactQueryService DI)

apps/Scaffolder.Cli/
├── ArtifactsCommand.cs               (NEW — BrowseAsync interactive loop)
├── ApiClient.cs                      (EXTEND — GetArtifactTreeAsync, GetArtifactFileAsync)
├── Models.cs                         (EXTEND — ArtifactTree, ArtifactFile, ArtifactFileContent)
└── Program.cs                        (EXTEND — add "artifacts" case to run subcommand switch)

apps/web/src/
├── api/
│   ├── types.ts                      (EXTEND — add ArtifactFilter, ArtifactFile, ArtifactTree,
│   │                                             ArtifactFileContent)
│   └── client.ts                     (EXTEND — add getArtifactTree, getArtifactFile methods)
├── components/
│   ├── ArtifactBrowser.tsx           (NEW — orchestrator: fetches tree + file, drives refresh)
│   ├── ArtifactFilterTabs.tsx        (NEW — TabList/Tab for All/Committed/Uncommitted/Last commit)
│   ├── ArtifactFileTree.tsx          (NEW — file list with status badges)
│   ├── ArtifactFilePanel.tsx         (NEW — routes to PerFileDiffViewer or MarkdownRenderer)
│   ├── PerFileDiffViewer.tsx         (NEW — extends DiffViewer with line-number gutter)
│   └── MarkdownRenderer.tsx          (NEW — react-markdown + remark-gfm + rehype-sanitize)
└── pages/
    └── WatchPage.tsx                 (EXTEND — add ArtifactBrowser tab/panel alongside RunWatcher)
```

**Structure Decision**: The existing structure of `apps/Scaffolder.Api/` uses flat directories
per concern (`Git/`, `Contracts/`, `Runs/`, etc.). The new `Artifacts/` directory follows the
same pattern and keeps artifact query logic isolated from the worktree lifecycle code in `Git/`.

---

## Architecture and Component Design

### 3.1 Backend — ArtifactQueryService

`IArtifactQueryService` is a thin orchestration layer between the API endpoints and
`WorktreeManager`. It:
- Resolves the `Run` to its `WorktreePath` and `OriginatingBranch`
- Returns an empty file list when `WorktreePath` is null (Pending runs, FR-038)
- Delegates all git queries to `WorktreeManager`
- Validates path parameters for per-file requests through `SandboxPathValidator`

Registered as `Scoped` in DI (one instance per HTTP request — no shared mutable state).

### 3.2 Backend — WorktreeManager Additions

Two new methods (see data-model.md §7 for signatures):

**`GetArtifactFiles`**: Opens `new Repository(worktreePath)`. Dispatches to the appropriate
LibGit2Sharp query based on `ArtifactFilter`. All queries return `IReadOnlyList<ArtifactFile>`.
IsMarkdown detection: `Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase)`.

**`GetArtifactFileContent`**: For source files, opens `new Repository(worktreePath)` and calls
`repo.Diff.Compare<Patch>(originTip.Tree, worktreeHead.Tree, new[] { relativePath })`. For
uncommitted files (in workdir but not HEAD), uses `DiffTargets.Index | DiffTargets.WorkingDirectory`.
For Markdown files, reads `File.ReadAllText(Path.Combine(worktreePath, relativePath))`.

### 3.3 Backend — API Endpoints

Both endpoints follow the same pattern as existing endpoints in `Program.cs`:
- Parse and validate `id` via `RunId.TryParse`
- Fetch `Run` from `SqliteRunStore`
- Owner check (`IsOwner(httpContext, run)`)
- Delegate to `IArtifactQueryService`
- Map results to DTO
- Return `Results.Json(...)`

**Availability rule**: if `run.WorktreePath` is null → return `ArtifactTreeDto` with empty `Files`.
If `run.WorktreePath` is set but directory does not exist (cleaned up) → `503` with retry hint.

### 3.4 Web UI — ArtifactBrowser

`ArtifactBrowser` is the top-level component. It:
- Fetches `GET /api/runs/{runId}/artifacts?filter={activeFilter}` on mount and on filter change
- Subscribes to `events` from `useRunStream` (passed as prop from `RunWatcher`)
- Triggers a refetch whenever a `tool.result` event arrives whose paired `tool.call` has
  `toolName === "write_file"` (see data-model.md §8 live refresh logic)
- Manages `selectedPath` state — when changed, fetches `GET /api/runs/{runId}/artifacts/{path}`
- Renders `ArtifactFilterTabs`, `ArtifactFileTree`, `ArtifactFilePanel` in a two-column layout

**Integration into WatchPage**: `WatchPage` renders `RunWatcher` and `ArtifactBrowser`
side-by-side (or stacked on narrow screens) using Fluent layout primitives. `RunWatcher`
passes its `events` array to `ArtifactBrowser` so the live-refresh effect can observe
new tool results without a second SSE connection.

**ReviewPanel integration**: `ReviewPanel` remains unchanged. The `ArtifactBrowser` provides
the per-file view that supplements (and for the file-by-file review flow, supersedes) the flat
`DiffViewer` currently rendered above `ReviewPanel`. The flat `DiffViewer` may remain for
backward compatibility or be hidden in favor of `ArtifactBrowser` — implementation decision
for `tasks.md`.

### 3.5 Web UI — PerFileDiffViewer

Extends `DiffViewer` with a line-number gutter. A pure function `parseDiffLines(diff)` returns
`DiffLine[]` where each entry carries `kind`, `content`, `oldLineNo?`, `newLineNo?`. The
component renders a CSS grid with three columns: old-line-no, new-line-no, content. Existing
color classes (`added`, `removed`, `hunk`, `fileHeader`) are preserved.

### 3.6 Web UI — MarkdownRenderer

```tsx
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeSanitize from 'rehype-sanitize';

export function MarkdownRenderer({ content }: { content: string }) {
  return (
    <div className={styles.root}>
      <ReactMarkdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeSanitize]}>
        {content}
      </ReactMarkdown>
    </div>
  );
}
```

`rehype-sanitize` is always applied — never omitted, even in review mode where content has
already been safety-checked. Defense-in-depth. Styled with `makeStyles` + `tokens` for
Fluent typographic consistency.

### 3.7 CLI — ArtifactsCommand

`scaffolder run artifacts <run-id> [--filter <filter>]`

Interactive TTY mode:
1. Fetch and display file tree as `Spectre.Console.Table` (columns: Status, Path, Type)
2. `SelectionPrompt<string>` with file paths + `[change filter]` + `[exit]`
3. On file select: fetch per-file content; dispatch to `RenderDiff` (existing) or `RenderMarkdown` (new)
4. `AnsiConsole.Write(new Rule())` to separate views; press Enter to return to file list
5. On `[exit]`: return exit code 0

Non-interactive (piped stdout): print file table once and exit.

`RenderMarkdown(string content)` uses:
```csharp
var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
var plain = Markdown.ToPlainText(content, pipeline);
AnsiConsole.Write(new Panel(plain).Border(BoxBorder.Rounded));
```

### 3.8 Readonly Mode Enforcement

**API**: the API never enforces readonly at the artifact *read* layer — the browser remains
accessible after merge/decline. The review-action endpoint (`POST /api/runs/{id}/review`) already
rejects decisions on terminal-status runs. `is_readonly: true` in the artifact tree response is
informational — the client uses it to hide approval UI, but no browser read is blocked.

**Web UI**: `ArtifactBrowser` receives `isReadonly` from the API response. When `isReadonly`:
- "Historical view" label shown in browser header
- `ReviewPanel` not rendered inside `ArtifactBrowser` (it lives outside in `RunWatcher`)
- File tree and file panel fully accessible (SC-017: readonly but visible)

**CLI**: `ArtifactsCommand` shows a `[grey]Historical view[/]` annotation on the header table
when the run status is `merged`, `declined`, or `merge_failed`. No approve/decline prompt shown.

---

## Milestones

### M1 — Backend: WorktreeManager git queries + artifact endpoints
Deliverables:
- `ArtifactFilter`, `ArtifactFileStatus`, `ArtifactFile` types
- `WorktreeManager.GetArtifactFiles` (all four filters) + `GetArtifactFileContent`
- `ArtifactQueryService` implementing the interface
- `GET /api/runs/{id}/artifacts` and `GET /api/runs/{id}/artifacts/{**path}` endpoints
- DTOs in `Dtos.cs`
- Path traversal guard via `SandboxPathValidator`
- Owner check on both endpoints
- xUnit tests for all four filter queries and per-file content (using a real test worktree)

### M2 — Web UI: ArtifactBrowser component family
Deliverables:
- `ArtifactFilterTabs`, `ArtifactFileTree`, `ArtifactFilePanel`
- `PerFileDiffViewer` with line-number gutter (and `parseDiffLines` pure function)
- `MarkdownRenderer` with `react-markdown` + `remark-gfm` + `rehype-sanitize`
- `ArtifactBrowser` orchestrator with live-refresh effect
- API client additions (`getArtifactTree`, `getArtifactFile`)
- Integration into `WatchPage` alongside `RunWatcher`
- Vitest unit tests for `parseDiffLines` and `ArtifactFileTree` status badge logic

### M3 — CLI: artifacts subcommand
Deliverables:
- `ArtifactsCommand.BrowseAsync` interactive loop
- `ApiClient` additions for artifact endpoints
- `Models.cs` additions for CLI artifact types
- `RenderMarkdown` using `Markdig`
- `Program.cs` updated with `"artifacts"` case
- `Markdig` added to `Scaffolder.Cli.csproj`
- Manual validation using quickstart scenarios 1–9

### M4 — Integration and parity validation
Deliverables:
- End-to-end validation of all quickstart scenarios (Scenarios 1–9)
- SC-013 timing verified (write → visible in tree ≤ 5 s)
- SC-014 filter accuracy confirmed for all four tabs on a multi-commit test run
- SC-015 diff accuracy confirmed (spot-checked against `git diff` in worktree)
- SC-016 CLI/Web parity confirmed (same file counts, same diffs)
- SC-017 readonly mode confirmed (no approval buttons after decision)
- NFR-002 verified (no emoji in any output)

---

## Complexity Tracking

> No constitution violations — this section documents one bounded complexity decision.

| Decision | Why | Simpler Alternative Rejected Because |
|---|---|---|
| `WorktreeManager` adds two new public methods (total: 7 methods) | All git access for artifacts is isolated in one place, consistent with the existing pattern for diff, merge, and worktree lifecycle ops | Introducing a second git wrapper class would split concerns across two files for no benefit |
| `Markdig` new NuGet dependency in `Scaffolder.Cli` | FR-037 + FR-041 require CommonMark rendering in the CLI; `Markdig` is the only well-maintained pure-managed CommonMark parser for .NET | Plain text pass-through violates FR-037 ("rendered as formatted CommonMark"); reimplementing a parser violates Principle VII |

---

## Open Questions / Risks

| ID | Description | Resolution |
|---|---|---|
| R1 | Worktree git status performance on very large repos (10k+ files) | `RetrieveStatus` with `RecurseUntrackedDirs = false` limits scope; `Committed` filter uses tree diff which is O(changed files); acceptable for typical repos in this slice |
| R2 | LibGit2Sharp `Diff.Compare<Patch>` behavior for uncommitted binary files | Binary files return a minimal diff header; `IsMarkdown` will be false; the diff viewer renders the header only — acceptable for this slice |
| R3 | `Markdig.ToPlainText` stripping all formatting | Verified in spike: headings, lists, and code blocks are retained as indented text blocks; acceptable CLI rendering |
| R4 | `react-markdown` upgrading past v10 before this lands | `package.json` pins `10.1.0`; no risk during this branch |
