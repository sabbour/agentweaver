# Data Model: Artifact Browser (User Story 5, FR-034–FR-041)

**Branch**: `001-single-agent-run/artifact-viewer` | **Date**: 2026-06-10

---

## 1. Existing Entities (Unchanged)

### Run *(Scaffolder.Domain.Run)*

No schema changes. The artifact browser reads existing fields:

| Field | Type | Used for |
|---|---|---|
| `Id` | `RunId` | Endpoint path parameter; audit gate |
| `RepositoryPath` | `string` | Base repo for origin branch lookup |
| `OriginatingBranch` | `string` | Diff baseline for all filter modes |
| `WorktreePath` | `string?` | Root of the worktree filesystem; null = Pending |
| `WorktreeBranch` | `string?` | Branch name in the base repo for the worktree |
| `Status` | `RunStatus` | Derives readonly mode; gates artifact availability |
| `SubmittingUser` | `string` | Owner check on every artifact request |

---

## 2. New Value Types (Backend Domain)

### ArtifactFilter *(enum — Scaffolder.Api.Artifacts)*

```csharp
public enum ArtifactFilter
{
    All,          // all files differing vs originating branch (committed + uncommitted)
    Committed,    // files whose changes are committed in the worktree branch
    Uncommitted,  // files with staged or working-directory changes vs worktree HEAD
    LastCommit    // files changed only in the most recent commit in the worktree
}
```

Wire values (query string, case-insensitive): `all`, `committed`, `uncommitted`, `last-commit`.
Default: `all`.

### ArtifactFileStatus *(enum — Scaffolder.Api.Artifacts)*

```csharp
public enum ArtifactFileStatus
{
    Added,    // file does not exist in originating branch
    Modified, // file exists in originating branch with different content
    Deleted   // file was removed relative to originating branch
}
```

Wire values (JSON string): `"added"`, `"modified"`, `"deleted"`.

### ArtifactFile *(record — Scaffolder.Api.Artifacts)*

Represents one entry in the artifact tree. No database persistence — computed on demand from
the worktree's git state.

| Field | Type | Description |
|---|---|---|
| `Path` | `string` | Relative path from worktree root (forward slashes, no leading slash) |
| `Status` | `ArtifactFileStatus` | Annotation relative to originating branch |
| `IsMarkdown` | `bool` | True when extension is `.md` or `.markdown` (case-insensitive) |

---

## 3. New API DTOs (Scaffolder.Api.Contracts)

### ArtifactTreeDto

Response body for `GET /api/runs/{id}/artifacts?filter=<filter>`.

```csharp
public sealed record ArtifactTreeDto
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("filter")]
    public required string Filter { get; init; }  // echoed wire value

    [JsonPropertyName("files")]
    public required IReadOnlyList<ArtifactFileDto> Files { get; init; }

    [JsonPropertyName("is_live")]
    public required bool IsLive { get; init; }    // true while run is InProgress

    [JsonPropertyName("is_readonly")]
    public required bool IsReadonly { get; init; } // true after Merged/Declined/MergeFailed
}

public sealed record ArtifactFileDto
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }  // "added" | "modified" | "deleted"

    [JsonPropertyName("is_markdown")]
    public required bool IsMarkdown { get; init; }
}
```

### ArtifactFileContentDto

Response body for `GET /api/runs/{id}/artifacts/{**path}`.

```csharp
public sealed record ArtifactFileContentDto
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }  // "diff" | "markdown"

    /// <summary>
    /// Unified diff string (kind == "diff"). Null for Markdown files.
    /// </summary>
    [JsonPropertyName("diff")]
    public string? Diff { get; init; }

    /// <summary>
    /// Raw file content (kind == "markdown"). Null for source files.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }
}
```

---

## 4. New Frontend Types (apps/web/src/api/types.ts additions)

```typescript
export type ArtifactFilter = 'all' | 'committed' | 'uncommitted' | 'last-commit';
export type ArtifactFileStatus = 'added' | 'modified' | 'deleted';
export type ArtifactFileKind = 'diff' | 'markdown';

export interface ArtifactFile {
  path: string;
  status: ArtifactFileStatus;
  is_markdown: boolean;
}

export interface ArtifactTree {
  run_id: string;
  filter: ArtifactFilter;
  files: ArtifactFile[];
  is_live: boolean;
  is_readonly: boolean;
}

export interface ArtifactFileContent {
  path: string;
  kind: ArtifactFileKind;
  diff: string | null;
  content: string | null;
}
```

---

## 5. Git State Model (Conceptual)

The artifact tree is a virtual view computed from three git states:

```
originatingBranch.Tip.Tree     (the baseline — never changes during a run)
        |
        |  committed diff
        v
worktreeHead.Tree               (committed changes in the worktree branch)
        |
        |  uncommitted diff (staged + working dir)
        v
worktree working directory      (current on-disk state)
```

### Filter to Git Query Mapping

```
All          = committed files  UNION  uncommitted files
Committed    = repo.Diff.Compare<TreeChanges>(originTip.Tree, worktreeHead.Tree)
Uncommitted  = repo.RetrieveStatus() WHERE flags include Index* or Workdir*
LastCommit   = repo.Diff.Compare<TreeChanges>(worktreeHead.Parents[0].Tree, worktreeHead.Tree)
               (empty if worktreeHead has no parents — no commits yet)
```

### Status Derivation for "All" Filter

A file is annotated as:
- `Added` — present in worktree (committed or uncommitted), absent in origin
- `Modified` — present in both with different content, OR in both with rename
- `Deleted` — present in origin, absent in current worktree state

For uncommitted entries, `ChangeKind` from the status flags maps to `ArtifactFileStatus`:
- `NewInIndex` or `NewInWorkdir` → `Added`
- `ModifiedInIndex` or `ModifiedInWorkdir` or `RenamedInIndex` → `Modified`
- `DeletedFromIndex` or `DeletedFromWorkdir` → `Deleted`

---

## 6. New API Service (Scaffolder.Api.Artifacts.ArtifactQueryService)

Stateless service injected into API endpoints. Wraps `WorktreeManager` artifact queries.

```csharp
public interface IArtifactQueryService
{
    IReadOnlyList<ArtifactFile> GetArtifactFiles(Run run, ArtifactFilter filter);
    (string? Diff, string? Content) GetArtifactFileContent(Run run, string relativePath);
}
```

`GetArtifactFileContent` returns:
- `(Diff: string, Content: null)` for source files
- `(Diff: null, Content: string)` for Markdown files (reads raw text from worktree)

Path safety: `relativePath` is validated through `SandboxPathValidator` before any filesystem access.
If the resolved absolute path is outside the worktree root, a `403 Forbidden` is returned.

---

## 7. WorktreeManager Additions

Two new public methods on `WorktreeManager`:

```csharp
/// <summary>
/// Returns the set of files touched by the run, filtered by the requested view.
/// Always opens a fresh Repository handle to reflect the latest on-disk state.
/// </summary>
public IReadOnlyList<ArtifactFile> GetArtifactFiles(
    string repositoryPath,
    string originatingBranch,
    string worktreePath,
    ArtifactFilter filter);

/// <summary>
/// Returns the unified diff for a source file, or the raw content for a Markdown file,
/// relative to the originating branch.
/// </summary>
public (string? Diff, string? Content) GetArtifactFileContent(
    string repositoryPath,
    string originatingBranch,
    string worktreePath,
    string relativeFilePath);
```

Both methods open `new Repository(worktreePath)` (not `repositoryPath`) so git status queries
reflect the worktree's own index and working directory.

---

## 8. New Web UI Components

### Component Responsibilities

| Component | File | Key props / state |
|---|---|---|
| `ArtifactBrowser` | `components/ArtifactBrowser.tsx` | `runId`, `isLive`, `isReadonly` — orchestrates fetch, SSE-triggered refresh |
| `ArtifactFilterTabs` | `components/ArtifactFilterTabs.tsx` | `selected: ArtifactFilter`, `onChange` |
| `ArtifactFileTree` | `components/ArtifactFileTree.tsx` | `files: ArtifactFile[]`, `selectedPath`, `onSelect` |
| `ArtifactFileTreeItem` | (inline in ArtifactFileTree) | path, status badge, markdown icon |
| `ArtifactFilePanel` | `components/ArtifactFilePanel.tsx` | `content: ArtifactFileContent \| null`, `loading` — routes to PerFileDiffViewer or MarkdownRenderer |
| `PerFileDiffViewer` | `components/PerFileDiffViewer.tsx` | `diff: string` — extends DiffViewer with line-number gutter |
| `MarkdownRenderer` | `components/MarkdownRenderer.tsx` | `content: string` — wraps react-markdown with gfm + sanitize |

### Component Tree

```
ArtifactBrowser
├── ArtifactFilterTabs
├── ArtifactFileTree
│   └── ArtifactFileTreeItem[]  (path + status badge [new/modified/deleted])
└── ArtifactFilePanel
    ├── PerFileDiffViewer       (source files — readonly diff with line numbers)
    └── MarkdownRenderer        (Markdown files — CommonMark rendered)
```

### Live Refresh Logic (ArtifactBrowser)

```typescript
// On every tool.result event from useRunStream where the paired tool.call was write_file:
// re-fetch GET /api/runs/{runId}/artifacts?filter={activeFilter}
//
// Effect hook:
useEffect(() => {
  const lastEvent = events.at(-1);
  if (!lastEvent || lastEvent.type !== 'tool.result') return;
  // Look up the preceding tool.call by callId
  const callEvent = events.findLast(
    e => e.type === 'tool.call' && e.payload?.callId === lastEvent.payload?.callId
  );
  if (callEvent?.payload?.toolName === 'write_file') {
    refetchArtifactTree();
  }
}, [events]);
```

---

## 9. New CLI Command

### ArtifactsCommand (Scaffolder.Cli.ArtifactsCommand)

```csharp
// Entry: scaffolder run artifacts <run-id> [--filter all|committed|uncommitted|last-commit]
public static class ArtifactsCommand
{
    public static async Task<int> BrowseAsync(
        ApiClient api,
        string runId,
        string filter,        // default "all"
        CancellationToken ct);
}
```

Interactive loop (when TTY):
1. Fetch `GET /api/runs/{runId}/artifacts?filter={filter}` → display as `Table`
2. `SelectionPrompt` for file (or `[exit]`)
3. Fetch `GET /api/runs/{runId}/artifacts/{path}` → render diff (`RenderDiff`) or Markdown (`RenderMarkdown`)
4. Press any key → return to step 1

Non-interactive (pipe/redirect): print table once and exit (mirrors `ShowAsync` pattern).

`RenderMarkdown(string content)` uses Markdig `Markdown.ToPlainText(content)` with structural
annotations: headings indented and bold, code blocks in monospace `Panel`, bullets with `-` prefix.
