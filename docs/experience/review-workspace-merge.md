# Review, Workspace & Merge Experience

Agentweaver makes agent work reviewable before it becomes repository history. The web UI gives reviewers a file-by-file review surface, a read-only workspace browser, and explicit **Commit and Merge**, **Change**, and **Decline** actions; MCP exposes the same core artifacts and local repository operations through named tools. This page explains what users see, what each decision means, and why Agentweaver treats the reviewed tree hash as the boundary between proposed work and merged work.

Related docs: [Overview](./00-overview.md), [Runs & board](./runs-board-watch.md), [Coordinator & orchestration](./coordinator-orchestration.md), [Reviewing and Merging](../guide/review.md), [Review & Merge deep dive](../deep-dive/review-merge.md), and [Git Integration deep dive](../deep-dive/git-integration.md).

## The mental model

A run is not a hidden edit to the project checkout. Agentweaver creates a run branch and worktree, lets the agent work there, commits the candidate result, computes a diff against the originating branch, and pauses at review. The reviewer sees the proposed tree, the changed files, and the run timeline before deciding whether the work should merge, revise, or stop.

The review surface is intentionally local-first. Agentweaver can complete review and merge without a remote, so blank projects and local-only repositories have the same experience as cloned projects. Pushing run branches and opening remote pull requests are out of scope; remote PRs can be useful outside Agentweaver, but they are not the authoritative review surface.

The most important product contract is: **approval binds to candidate content, not just a mutable branch name**. Merge checks that the candidate branch still matches the reviewed tree hash. This does **not** promise that the final destination tree equals the candidate tree: a three-way merge can preserve newer originating-branch content, and centrally consolidated Squad state has special merge handling.

![The mental model: Agent works in run worktree, Candidate commit + tree hash, Diff against originating branch, Human Review, Reviewer decision, Verify reviewed tree hash, Local git merge, Merged, Reviewer feedback, Agent revises same run worktree, Declined, Merge failed or returns to review](../diagrams/experience-review-workspace-merge-fig1.png)

<!-- Diagram source: ../diagrams/src/experience-review-workspace-merge-fig1.drawio.
     Published PNG path is stable; edit the draw.io source, not the raster. -->

| Review-loop step | Web surface | MCP tool or limit |
|---|---|---|
| Check whether review is ready | Orchestration / selected-task timeline | `run_status`, `run_watch` |
| List candidate changes | **Changes** | `run_show_artifacts` |
| Inspect one changed file | File viewer **Diff** | `run_get_file` |
| Approve the candidate | **Commit and Merge** | `run_review` with `approved: true` |
| Ask for a revision | **Change** → feedback → **Send** | No feedback argument in `run_review`; use the web review action |
| Decline | **Decline** | `run_review` with `approved: false` |

MCP is an authenticated client, not an independent reviewer identity or a bypass of the review gate. Its review tool is binary; it must not be presented as supporting the web feedback-bearing **Change** action.

## Human Review in the web UI

When a normal run reaches **Human Review**, its status is `awaiting_review`; collective orchestration review also has a persisted work-plan `in_review` gate. Open the orchestration and its artifact or selected-task **Agent session** panel to inspect changes alongside the timeline. Standalone Workflow and Execution pages are retired.

The reviewer sees:

- a **Changes** tab with the changed-file list and line counts;
- a **Files** tab with the full run worktree browser;
- the proposed commit message when one is available;
- **Commit and Merge**, **Change**, and **Decline** controls while the run is awaiting review;
- processing spinners and inline errors when a review, request-changes, or merge action is in flight;
- result badges after an action is accepted, such as `merged`, `declined`, or `changes_requested`.

The review gate makes the candidate artifacts and review actions available in the orchestration context; a remote PR is not required.

### Approve: Commit and Merge

**Commit and Merge** is the primary approval action in the web UI. It means: "I accept this candidate tree and authorize Agentweaver to merge it to the originating branch." Approval does not ask the agent to make more edits. It moves the run from review toward guarded local merge.

After **Commit and Merge**, the UI shows a pending state and updates as merge events arrive. Success integrates the approved candidate with the originating branch and reaches a merged state. A changed candidate hash or genuine merge conflict fails rather than letting an agent invent a conflict resolution. Destination-branch changes can still be part of the final merged tree.

Approval is a content decision. The merge executor still verifies the candidate tree hash, serializes repository updates, and checks that the run branch and originating branch are in a safe state. A reviewer approves the diff; Agentweaver proves the repository can accept it.

### Request changes: Change

**Change** is the revision path. The reviewer clicks **Change**, types feedback into **Describe what the agent should change**, and clicks **Send**. **Cancel** closes the feedback box without changing the run.

A request-changes decision does not merge the current tree. Agentweaver records the feedback, marks the review step as revise, clears stale run-scoped approvals, abandons the paused review workflow, and starts a new revision cycle on the same run worktree. The agent receives the original task plus the reviewer feedback as untrusted review data and applies the requested changes in the existing worktree.

When the revision finishes, the run returns to **Human Review** with a new candidate tree and diff. The review bar appears again even if an earlier request-changes result is still visible in local UI state. The reviewer then makes a fresh decision on the revised output.

### Reject: Decline

**Decline** is the terminal rejection path in the web UI. It means the reviewer does not want this run's current work to continue. The run becomes declined, the originating branch stays unchanged, and the reviewer should submit a new run if they want a different attempt.

Use **Decline** when the work is not worth revising in place, when the task is obsolete, or when the reviewer wants to discard the candidate. Use **Change** when the direction is right but the output needs specific fixes.

### Reviewer lockout at the UX level

Producing a candidate and accepting a review decision are separate operations. The API authorizes the caller and consumes a pending review decision; a producer completing its task is not itself approval. Do not read this as proof of universal independent-human identity enforcement: an authorized MCP client can submit `run_review`. Collective assembly's rejected-author rotation is a different mechanism, described in [resilient assembly review](./resilient-assembly-review.md).

Agentweaver records the reviewer identity on review and merge-related transitions when it is known. The pending review request is consumed at most once, so a double click, replayed request, or competing client does not create two decisions. If one decision moves the run out of `awaiting_review`, later attempts see conflict-style behavior instead of racing the merge.

This is not a remote pull-request ownership model. Remote PR authorship and branch protection are outside this review surface.

## MCP review tools

MCP clients use named tools instead of buttons, but the same run state is being inspected.

### `run_review`

`run_review` submits a binary review decision for a run that is awaiting review:

- `approved: true` approves the run and sends it toward merge;
- `approved: false` rejects/declines the run.

The MCP tool is intentionally simple: it maps to approve or reject. The web **Change** flow is the feedback-bearing request-changes path; the current MCP `run_review` tool does not carry a reviewer comment or start the dedicated request-changes revision endpoint. For MCP-driven review, inspect artifacts first, then call `run_review` only when the decision is approve or reject.

### `run_status` and `run_watch`

`run_status` shows the current state of a run, including whether it is awaiting review, merged, declined, failed, or merge-failed. `run_watch` streams progress and reports review-related transitions such as **Run awaiting review**. Together they let an MCP client know when the review gate is ready and when a decision has completed.

## Artifact and diff experience

The Artifact Browser is the reviewer's file-focused lens on a run, reused in orchestration artifacts and selected-task inspection. Its two views are **Changes** and **Files**.

### Changes tab

The **Changes** tab is the default review tab. It shows **Branch Changes** with total added and removed line counts, then a flat changed-file list. Each changed file row shows:

- the file name;
- added and removed line counts, such as `+12` and `-3`;
- a status badge: `A` for added, `M` for modified, `D` for deleted;
- a status-colored icon: green for added, marigold/orange for modified, red for deleted.

Clicking a row opens the file viewer modal. For changed files, the modal defaults to **Diff**. Markdown files also offer **Preview**, so reviewers can inspect rendered documentation while still reviewing the exact diff.

The changed-file list is optimized for review. It answers: "What would this run add, modify, or delete on the originating branch?" The diff is computed against the originating branch, not merely against the last commit, so the reviewer sees the full candidate contribution.

### Files tab

The **Files** tab shows the full run workspace tree. It is useful when the reviewer needs context around changed files: neighboring files, generated files, docs, or project structure. Folders expand and collapse in a tree. Files are sorted with folders first, and file icons reflect common file types such as Markdown, code, JSON, stylesheets, images, PDFs, lockfiles, and generic documents.

Clicking a file in **Files** opens the same file viewer modal, but unchanged files are treated as source/preview content rather than diffs. Markdown opens in rendered **Preview** by default; other files open as syntax-highlighted **Source** with line numbers.

### File viewer modal

The modal is the focused reading surface. It opens at a large viewport size, has a **Close** button, and preserves the distinction between changed and unchanged files.

Changed files show:

- **Diff** for the patch view;
- **Preview** for Markdown content when available;
- `Binary file — diff not available` for binary changed files.

Unchanged or workspace files show:

- **Source** for syntax-highlighted file content;
- **Preview** for Markdown files;
- `Binary file` for binary content;
- `File too large to display` when the content endpoint reports that the file is too large for inline display.

This gives reviewers a practical path through common cases. Code changes are reviewed as diffs, documentation can be reviewed both as Markdown source and rendered output, and binary or oversized content is clearly identified instead of rendered incorrectly.

### Large diffs and binary assets

Large diffs and binary assets are edge cases by design. Agentweaver does not pretend every file is readable inline.

For binary changed files, the diff viewer displays `Binary file — diff not available`. The file remains visible in the changed-file list with its added/modified/deleted status, so the reviewer knows the asset changed even though there is no textual patch.

For large source files in content view, the file viewer displays `File too large to display`. The reviewer can still use the changed-file metadata, the diff when available, the run timeline, and local repository tools to inspect the content outside the browser. The UI favors a truthful empty/too-large state over freezing the review surface.

### No-change runs

A run can reach a review-related surface with no changed files. The **Changes** tab handles this explicitly. At a review gate, an empty list becomes: **This run produced no changes to review.** The UI adds that agents may have written output outside the repository or that there was nothing to change. In coordinator contexts, it can also list subtasks that produced no changes.

This is important because Agentweaver avoids empty candidate commits. A zero-diff run should not look like shipped work. The reviewer sees that there are no repository changes and can decide whether to decline, request changes through the web flow, or submit a new task.

### Historical artifact state

After terminal states such as merged, declined, merge failed, or failed, the Artifact Browser treats the view as historical. It displays the artifact state at run completion and fixes the active filter to all changed files. This helps post-review inspection: the user is not editing the run, but they can still understand what was proposed or merged.

## MCP artifact tools

MCP mirrors the artifact experience with two review-oriented tools.

### `run_show_artifacts`

`run_show_artifacts` lists the files changed by a run. It is the MCP equivalent of opening the **Changes** tab. The response is the changed-file inventory: paths, statuses, and line-count metadata used by the UI to present added, modified, and deleted files.

Use it first when reviewing from an MCP client. It answers which files need attention before you fetch any individual content or diff.

### `run_get_file`

`run_get_file` fetches one changed file by run id and path. It is the MCP equivalent of selecting a file in the Artifact Browser. For changed files, the response can include the diff and file metadata, including whether the file is binary. For content-oriented inspection, the related file content endpoints are what the web modal uses to show **Source** and **Preview**.

Paths are relative to the run workspace. The tool accepts normal workspace paths and encodes them safely for the API. Reviewers should pass the path exactly as returned by `run_show_artifacts`.

A typical MCP review loop is:

1. `run_status` or `run_watch` until the run is awaiting review.
2. `run_show_artifacts` to list changed files.
3. `run_get_file` for each file that needs detailed inspection.
4. `run_review` with `approved: true` to approve or `approved: false` to reject.

Use the [reviewer loop and adjacent MCP mapping](#the-mental-model) above for this sequence. Artifact inspection does not grant approval, and a binary rejection is not a request-changes message.

## Workspace experience in the web UI

The **Workspace** page is a project-scoped, read-only file browser. It is not the review decision surface and it does not expose commit or merge controls. It exists so users can browse the project repository and active run worktrees without leaving Agentweaver.

The page header says **Workspace** and the subtitle explains the scope: **Browse the project repository and active run worktrees, read-only.** The breadcrumb leads from **Projects** to the project and then to **Workspace**. The toolbar shows the current branch with a branch icon and a **Branch or worktree** dropdown.

Open a project → **Workspace** (`/projects/:projectId/workspace`) to choose a ref, browse its tree, and read a file. The branch selector changes the read target; it neither checks out a branch for editing nor approves a merge.

The dropdown contains the base project ref and any browsable run worktrees or coordinator assembly refs. Non-base refs can show a run-status badge, such as running, dispatched, completed, merged, failed, merge_failed, blocked, or parked. Selecting a different ref reloads the file tree and clears the open file.

### Browsing files

The Workspace page has two panes:

- the left pane is the file tree;
- the right pane is the read-only file viewer.

The tree uses the same underlying file-tree experience as the run **Files** tab. Folders expand and collapse. Selecting a file opens it in the right pane. If nothing is selected, the right pane says **Select a file to view its contents.** If the selected branch has no files, it says **No files in this branch yet.**

The file viewer uses the same **Source** and **Preview** behavior as the run file viewer. Markdown files can be rendered as preview; other text files are syntax-highlighted with line numbers; binary and oversized files display clear non-rendered states.

### Import to backlog

When the selected workspace file is Markdown, the page shows **Import to backlog**. This is a workspace-specific action for turning a spec-like Markdown file into backlog items. It does not change the review or merge model. The file browser remains read-only with respect to repository content.

### Workspace file picker

The `WorkspaceFilePicker` component is a smaller file-selection experience used where a dialog or form needs the user to pick a workspace file. It loads workspace files, shows **Loading workspace files...** while fetching, displays **No files found in workspace.** when empty, and shows **Selected: path** after the user picks a file. It shares the same read-only intent: choose a file, do not edit it.

## MCP workspace tools

MCP exposes the workspace browser as three tools. These tools are read-only and project-scoped.

### `list_project_workspace_refs`

`list_project_workspace_refs` lists the browsable refs for a project workspace. The response includes the base branch and active run worktrees. This is the MCP equivalent of opening the **Branch or worktree** dropdown on the Workspace page.

Use it when you need to know what can be browsed: the current project branch, a run branch, or a worktree associated with an active run.

### `list_project_workspace`

`list_project_workspace` lists the flat file tree for a project workspace at a given ref. If the ref is omitted, it defaults to the base branch. This is the MCP equivalent of loading the left file tree on the Workspace page.

The tool returns paths and file/folder metadata. MCP clients can use the flat list to construct a tree, search for likely files, or present a picker.

### `get_project_workspace_file`

`get_project_workspace_file` returns the content of one file in a project workspace at a given ref. If the ref is omitted, it defaults to the base branch. This is the MCP equivalent of selecting a file in the Workspace page and reading it in the right pane.

The path is relative to the workspace and should use forward slashes, such as `src/main.cs`. The response includes content metadata, including binary or too-large signals, so an MCP client can avoid rendering content that is not suitable for inline display.

![`get_project_workspace_file`: Workspace page or MCP client, list_project_workspace_refs, Choose base branch or run worktree, list_project_workspace, Select file, get_project_workspace_file, Read-only source / preview](../diagrams/experience-review-workspace-merge-fig2.png)

<!-- Diagram source: ../diagrams/src/experience-review-workspace-merge-fig2.drawio.
     Published PNG path is stable; edit the draw.io source, not the raster. -->

## Merge experience

Merge starts only after approval. In the web UI, approval is **Commit and Merge**. In MCP, approval is `run_review` with `approved: true`. Both feed the same local merge model.

Agentweaver merges locally. It does not require a remote, does not push the run branch, and does not open a pull request. This is deliberate: the reviewed artifact is the local candidate tree hash and diff, and the merge target is the local originating branch. Local-first merge lets Agentweaver support blank repositories, local projects, and cloned projects with one review model.

The trade-off is plain: remote PRs are not the authoritative review surface in Agentweaver. If a team also wants a remote PR, that workflow belongs outside the current API boundary.

### What merge checks

Before it advances the originating branch, Agentweaver checks the repository state. The merge path verifies that the run branch exists, the originating branch exists, and the run branch tree still equals the approved tree hash. It serializes repository updates so two approvals for the same repository do not race each other.

If the target can fast-forward, Agentweaver advances it directly; otherwise it computes a three-way merge. When the originating branch is checked out, eligible dirty tracked modifications can first be auto-committed. The merge then checks for active git operations, conflicted indexes, and unsafe collisions, and reconciles the working tree only when it can preserve content safely. When the originating branch is not checked out, a ref-only path updates that branch without changing the current checkout. Three-way merges preserve the originating side's centrally consolidated Squad bookkeeping where special handling applies (`WorktreeManager.cs:1968`, `:2045`).

### Merge success

On success, the run reaches merged state. The originating branch contains the approved changes. Successful normal merges clean up the run worktree and branch, because the candidate content is now represented by the originating branch history.

The reviewer does not need a remote to complete this. A local blank project can be reviewed and merged end to end.

### Merge conflicts and merge failed

If the originating branch has diverged and the candidate cannot be merged safely, the run becomes `merge_failed` with conflict information where available. The merge coordinator preserves the worktree on its conflict path, but terminal workflow cleanup can subsequently remove it; do not depend on an ephemeral worktree surviving every failure. Inspect recorded artifacts and diagnostics first. The reviewer approved a candidate, not an agent-authored conflict resolution.

A merge can also fail if the approved tree hash no longer matches the run branch. That protects against manual mutation of the worktree or branch after review. Approval is tied to the tree hash, so a changed candidate must go through review again.

### Repository busy or interrupted merge

Repository updates are serialized. If another merge is already operating on the same repository, a merge can return a retriable conflict instead of racing. If the process is interrupted, recovery prefers a retryable review state when possible.

## Re-review after requested changes

Requesting changes creates a new review loop, not a side comment on the old diff. The current candidate is rejected for merge, feedback is stored, and the same run worktree is reused for revision. The agent works from the existing files, applies feedback, and produces a new candidate commit and tree hash.

When the revised run returns to **Human Review**, the reviewer should treat it as a fresh decision:

1. Read the updated timeline to see what the agent changed.
2. Open **Changes** and review the new branch diff.
3. Use **Files** or **Workspace** when context is needed.
4. Click **Commit and Merge** if the revised tree is acceptable.
5. Click **Change** again if more targeted feedback is needed.
6. Click **Decline** if the run should stop.

Normal run revisions use the server's `Runs:MaxRevisions` cap (default **10**); reaching it produces a conflict response. Collective coordinator assembly is different: human request-changes rounds are **uncapped** and reset the bounded autonomous steering budget. Each round still needs a new review of the revised candidate.

## How to choose the right surface

Use the run review surface when deciding whether a candidate should merge. It has the changed-file list, diff modal, timeline, and review actions in one place.

Use the Workspace page for read-only context across the project repository or active run worktrees. Use MCP when the reviewer is an assistant, script, or terminal client that needs refs, workspace files, artifacts, diffs, and approve/reject decisions.

| User goal | Web surface | MCP tool |
|---|---|---|
| See whether a run is waiting for review | Orchestration / Agent session timeline | `run_status`, `run_watch` |
| List changed files for a run | **Changes** tab | `run_show_artifacts` |
| Inspect one changed file | File viewer modal **Diff** | `run_get_file` |
| Browse the full run worktree | **Files** tab | `list_project_workspace` for a run ref, or run workspace-backed APIs |
| Browse project refs and worktrees | **Workspace** page dropdown | `list_project_workspace_refs` |
| Read a workspace file | Workspace file viewer | `get_project_workspace_file` |
| Approve and merge | **Commit and Merge** | `run_review` with `approved: true` |
| Request revision feedback | **Change** → **Send** | Web review action; current MCP `run_review` is binary |
| Reject the run | **Decline** | `run_review` with `approved: false` |

## Edge cases reviewers should expect

### Large text files

The file viewer can decline to render very large file content inline and show **File too large to display**. Review the diff when available, use the changed-file metadata to understand scope, and inspect locally if necessary.

### Binary assets

Binary files appear in the changed-file list with status and counts, but textual diffs are not available. The modal shows **Binary file — diff not available** for changed binaries and **Binary file** for source/content viewing.

### Runs with no changes

The **Changes** tab says **This run produced no changes to review.** This usually means there was nothing to change or the agent wrote outside the repository. Agentweaver avoids empty commits, so no-change work is represented honestly.

### Requested changes followed by another review

After **Change** → **Send**, the run goes back into progress and later returns to review. The reviewer should inspect the new diff, not rely on the old one. The new tree hash is the candidate that approval binds to.


### Merge conflicts

A merge conflict turns approval into `merge_failed`. Inspect the recorded conflict and any retained artifacts before choosing recovery; worktree retention depends on the execution and cleanup path.

### Tree hash mismatch

If the candidate branch changes after review, merge refuses it. The approved content identity no longer matches the branch. The safe path is another review over the new tree.

## Summary

The REVIEW, WORKSPACE, and MERGE experiences are one local-first flow. Runs create candidate worktrees; reviewers inspect artifacts; workspace browsing provides read-only context; approval authorizes guarded integration of the candidate; request changes starts revision; rejection stops it. MCP provides inspection and binary approve/reject decisions, while the web UI provides feedback-bearing re-review. The former Changes, file-viewer, and Workspace screenshot embeds were placeholders and are omitted until genuine captures are available.

<!-- diagram-context:experience-review-workspace-merge-fig1:start -->
<details id="diagram-context-experience-review-workspace-merge-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Inspect, decide, then integrate</td></tr>
<tr><td>takeaway</td><td>Approval identifies a candidate; guarded local merge can also include target-side changes.</td></tr>
<tr><td>group-title-0</td><td>CANDIDATE AND REVIEW</td></tr>
<tr><td>group-title-1</td><td>SERVER-AUTHORITATIVE OUTCOMES</td></tr>
<tr><td>Run candidate</td><td>Run candidate</td></tr>
<tr><td>Run candidate</td><td>Worktree + recorded hash</td></tr>
<tr><td>Run candidate</td><td>files / changes / timeline</td></tr>
<tr><td>Run candidate</td><td>Inspect the actual candidate before submitting a decision.</td></tr>
<tr><td>Review decision</td><td>Review decision</td></tr>
<tr><td>Review decision</td><td>Authorized run reviewer</td></tr>
<tr><td>Review decision</td><td>approve / change / decline</td></tr>
<tr><td>Review decision</td><td>MCP run_review is binary; web supports feedback.</td></tr>
<tr><td>Guarded merge</td><td>Guarded merge</td></tr>
<tr><td>Guarded merge</td><td>Lock, CAS, hash check</td></tr>
<tr><td>Guarded merge</td><td>approved candidate identity</td></tr>
<tr><td>Guarded merge</td><td>Changed candidate fails checks; conflicts are visible.</td></tr>
<tr><td>Revised candidate</td><td>Revised candidate</td></tr>
<tr><td>Revised candidate</td><td>Feedback drives revision</td></tr>
<tr><td>Revised candidate</td><td>normal cap / collective rules</td></tr>
<tr><td>Revised candidate</td><td>A new candidate needs review; no automatic approval.</td></tr>
<tr><td>Declined / blocked</td><td>Declined / blocked</td></tr>
<tr><td>Declined / blocked</td><td>No successful integration</td></tr>
<tr><td>Declined / blocked</td><td>explicit outcome or reason</td></tr>
<tr><td>Declined / blocked</td><td>Decline, busy repository and merge conflict are distinct.</td></tr>
<tr><td>Merged history</td><td>Merged history</td></tr>
<tr><td>Merged history</td><td>Local destination updated</td></tr>
<tr><td>Merged history</td><td>three-way / ref-only merge</td></tr>
<tr><td>Merged history</td><td>Final tree may include newer target and special Squad state.</td></tr>
<tr><td>e0</td><td>inspect</td></tr>
<tr><td>e1</td><td>approve</td></tr>
<tr><td>e2</td><td>decline</td></tr>
<tr><td>e3</td><td>changes</td></tr>
<tr><td>e4</td><td>blocked</td></tr>
<tr><td>e5</td><td>guards pass</td></tr>
<tr><td>e6</td><td>re-review</td></tr>
<tr><td>note</td><td>Request changes produces the revised-candidate lane. Conflicts/busy/hash mismatch never imply merged.</td></tr>
<tr><td>n0</td><td>Inspect the actual candidate
before submitting a decision.</td></tr>
<tr><td>n1</td><td>MCP run_review is binary;
web supports feedback.</td></tr>
<tr><td>n2</td><td>Changed candidate fails checks;
conflicts are visible.</td></tr>
<tr><td>n3</td><td>A new candidate needs review;
no automatic approval.</td></tr>
<tr><td>n4</td><td>Decline, busy repository and
merge conflict are distinct.</td></tr>
<tr><td>n5</td><td>Final tree may include newer
target and special Squad state.</td></tr>
<tr><td>groups</td><td>CANDIDATE AND REVIEW; SERVER-AUTHORITATIVE OUTCOMES</td></tr>
</tbody></table>
</details>
<!-- diagram-context:experience-review-workspace-merge-fig1:end -->

<!-- diagram-context:experience-review-workspace-merge-fig2:start -->
<details id="diagram-context-experience-review-workspace-merge-fig2" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Browse a ref without changing it</td></tr>
<tr><td>takeaway</td><td>Select an allowed ref, inspect its tree, and read content through read-only operations.</td></tr>
<tr><td>group-title-0</td><td>CHOOSE THE REFERENCE</td></tr>
<tr><td>group-title-1</td><td>READ THE CONTENT</td></tr>
<tr><td>Workspace reader</td><td>Workspace reader</td></tr>
<tr><td>Workspace reader</td><td>Web page or MCP client</td></tr>
<tr><td>Workspace reader</td><td>authorized project access</td></tr>
<tr><td>Workspace reader</td><td>Browsing does not check out, edit, approve or merge.</td></tr>
<tr><td>Available refs</td><td>Available refs</td></tr>
<tr><td>Available refs</td><td>Base, run, assembly</td></tr>
<tr><td>Available refs</td><td>list_project_workspace_refs</td></tr>
<tr><td>Available refs</td><td>Only available allowed refs; default is the base branch.</td></tr>
<tr><td>Selected ref</td><td>Selected ref</td></tr>
<tr><td>Selected ref</td><td>Explicit browsing context</td></tr>
<tr><td>Selected ref</td><td>GET only</td></tr>
<tr><td>Selected ref</td><td>Changing ref clears the previous file selection.</td></tr>
<tr><td>Read-only content</td><td>Read-only content</td></tr>
<tr><td>Read-only content</td><td>Source / Markdown preview</td></tr>
<tr><td>Read-only content</td><td>binary / large / missing</td></tr>
<tr><td>Read-only content</td><td>Render supported content; surface truthful limitations.</td></tr>
<tr><td>Selected path</td><td>Selected path</td></tr>
<tr><td>Selected path</td><td>Relative file name</td></tr>
<tr><td>Selected path</td><td>get_project_workspace_file</td></tr>
<tr><td>Selected path</td><td>Pass both path and ref; invalid paths are rejected.</td></tr>
<tr><td>File tree</td><td>File tree</td></tr>
<tr><td>File tree</td><td>Paths at the selected ref</td></tr>
<tr><td>File tree</td><td>list_project_workspace</td></tr>
<tr><td>File tree</td><td>Choose a listed file; no working-tree mutation.</td></tr>
<tr><td>e0</td><td>list refs</td></tr>
<tr><td>e1</td><td>choose</td></tr>
<tr><td>e2</td><td>list tree</td></tr>
<tr><td>e3</td><td>select</td></tr>
<tr><td>e4</td><td>read</td></tr>
<tr><td>note</td><td>Unknown refs/files can return 404; invalid paths return 400. Ref browsing is not content approval.</td></tr>
<tr><td>n0</td><td>Browsing does not check out,
edit, approve or merge.</td></tr>
<tr><td>n1</td><td>Only available allowed refs;
default is the base branch.</td></tr>
<tr><td>n2</td><td>Changing ref clears the
previous file selection.</td></tr>
<tr><td>n3</td><td>Render supported content;
surface truthful limitations.</td></tr>
<tr><td>n4</td><td>Pass both path and ref;
invalid paths are rejected.</td></tr>
<tr><td>n5</td><td>Choose a listed file;
no working-tree mutation.</td></tr>
<tr><td>groups</td><td>CHOOSE THE REFERENCE; READ THE CONTENT</td></tr>
</tbody></table>
</details>
<!-- diagram-context:experience-review-workspace-merge-fig2:end -->
