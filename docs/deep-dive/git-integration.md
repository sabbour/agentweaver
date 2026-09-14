# Git Integration — Conceptual Deep Dive

## Purpose and mental model

Agentweaver treats git as the durable content graph for agent work. The database records who asked for work, which run owns it, where the worktree lives, which branch contains the candidate result, what tree hash was reviewed, and how the merge ended. Git records the actual files.

The central idea is simple: **the project workspace is the stable repository, and every run gets an isolated branch/worktree derived from it**. Agents write inside that run workspace. Agentweaver commits the result, computes a diff against the originating branch, waits for review, and only then advances the target branch.

This gives Agentweaver three properties that are hard to get from a single mutable checkout:

1. **Isolation**: an unfinished run does not dirty the project base checkout.
2. **Parallelism**: multiple runs can modify the same repository at the same time without sharing one working directory.
3. **Reviewability**: the candidate result is a normal git tree with a stable tree hash, diff, and branch name.

Where this lives:

- `apps/Agentweaver.Api/Git/`
- `apps/Agentweaver.Api/Runs/`
- `apps/Agentweaver.Api/Coordinator/`
- `apps/Agentweaver.Api/Projects/`
- `apps/Agentweaver.Api/Auth/`
- `apps/Agentweaver.Api/Endpoints/AuthEndpoints.cs`

See also: `docs/deep-dive/projects.md` and `docs/deep-dive/data-persistence.md`.

## Core concepts

### Project workspace

A project workspace is the long-lived repository checkout. Blank projects are initialized with a baseline `.gitignore` and an initial commit so the default branch has a real tip; an empty commit is allowed when the ignore file already exists. GitHub projects are cloned into the workspace with an ephemeral access token.

The workspace is not meant to be the only place agents write. It is the repository home from which run worktrees are derived.

### Run branch

Every normal run gets a branch named:

```text
agentweaver/{runId}
```

The branch starts at the run's originating branch tip. The branch name is deterministic from the run id, which makes recovery possible: if the worktree directory is lost but the database and branch survive, Agentweaver can recreate the worktree from the same branch.

### Run worktree

A run worktree is a physical directory under the configured worktree base path. If no base path is configured, Agentweaver uses its data directory under `worktrees`. The directory name is the run id.

A worktree is the agent's working directory and sandbox root. The run record stores both the worktree path and the branch before the agent starts so restart recovery and UI browsing can find the candidate workspace.

### Candidate tree

When an agent turn ends, Agentweaver stages and commits the run's changes on the run branch. The committed tree hash becomes the identity of the reviewed result. Review and merge code treats that tree hash as a safety contract: the approved tree must still be the tree being merged.

### Originating branch

The originating branch is the branch the run started from and eventually merges back into. For project runs this is usually the project's default branch, but the run model carries it explicitly.

## Per-run worktree model

The important invariant is that the base workspace and the run workspace are different surfaces. Each candidate flows from agent edits to a commit, tree hash, and full diff; approval binds to that tree before a guarded merge advances the originating branch. A run can be abandoned, revised, inspected, merged, or cleaned up without requiring the project checkout itself to be the mutable scratchpad.

## Repository creation and GitHub cloning

Agentweaver has two project creation paths.

### Blank repository

For blank projects, Agentweaver:

1. creates or verifies an empty workspace directory;
2. initializes a git repository;
3. seeds a baseline `.gitignore` without overwriting an existing one, then creates the initial commit;
4. renames the initial branch to the configured default branch, normally `main`;
5. writes the project record only after the repository exists.

The initial commit is not cosmetic. Git worktrees and branch operations are much simpler when the default branch is not unborn. A rebuild should preserve that behavior.

### GitHub repository

For GitHub projects, Agentweaver:

1. validates that the source repository is an HTTPS GitHub URL at the project-service boundary;
2. resolves a valid GitHub access token for the project owner/caller scope;
3. clones the repository with that token as a temporary credential;
4. derives the default branch from the clone's HEAD;
5. persists repository identity and project metadata, not the token.

The clone helper can normalize `owner/repo` into a GitHub URL, but the project service currently validates the API request as a full `https://github.com/...` URL before cloning.

## Run lifecycle: branch, commit, review, merge

This is a conceptual state model. `CommittingCandidate` names the candidate-capture
operation, not a persisted `RunStatus`; it is distinct from the persisted `committing`
state used by the explicit commit endpoint.

```mermaid
stateDiagram-v2
  [*] --> Pending: run row reserved
  Pending --> InProgress: worktree path + branch persisted
  InProgress --> CommittingCandidate: agent turn finished
  CommittingCandidate --> AwaitingReview: tree hash + diff stored

  AwaitingReview --> InProgress: request changes / revision
  AwaitingReview --> Declined: reviewer declines
  AwaitingReview --> Merging: reviewer approves

  AwaitingReview --> Committing: explicit commit endpoint
  Committing --> Merging: final commit succeeds
  Committing --> AwaitingReview: commit/merge blocked or interrupted

  Merging --> Merged: fast-forward or merge commit
  Merging --> MergeFailed: conflict or tree-hash mismatch
  Merging --> AwaitingReview: retriable repository block

  Merged --> [*]
  Declined --> [*]
  MergeFailed --> [*]
```

A normal run follows this logic:

1. **Create branch and worktree**: create `agentweaver/{runId}` from the originating branch tip and check it out in a dedicated worktree with a single git-CLI `git worktree add -b agentweaver/{runId} {path} {sha}` (recovery re-checks out the already-existing branch with `git worktree add {path} agentweaver/{runId}`). Provisioning in one step — rather than the older LibGit2Sharp add-at-HEAD-then-checkout — means a run whose branch tip diverges from the primary repository HEAD in a checkout-unsafe way (for example a file/directory typechange) no longer aborts worktree creation, so dependent subtasks that base on the run integration branch provision reliably (`apps/Agentweaver.Api/Git/WorktreeManager.cs:127`, `:171`, `:178`).
2. **Persist before execution**: store the worktree path and branch on the run before the agent starts.
3. **Agent writes files**: the agent executes inside the worktree.
4. **Commit candidate result**: stage every non-ignored change, commit them on the run branch, and compute the tree hash.
5. **Compute diff**: compare the originating branch tree with the run branch tree.
6. **Wait for review**: store tree hash, diff, step count, and move to `awaiting_review`.
7. **Approve, decline, or revise**: human review either merges, declines, or sends the run back into the same worktree for another revision.
8. **Merge**: approved work advances the originating branch by fast-forward or merge commit, guarded by a repository lock and tree-hash verification.
9. **Clean up or preserve**: successful merges remove the worktree and branch; conflicts preserve the worktree for inspection.

## Commit logic

Agentweaver commits the worktree branch after the agent turn. The commit message is deterministic: `Agentweaver run {runId}`. The author identity comes from configuration, defaulting to `Agentweaver <agentweaver@localhost>`.

Staging is **scope-independent**. Agentweaver stages every changed, non-ignored path in the worktree — including deletions and renames — regardless of any coordinator subtask scope. There is no whitelist derived from a subtask's declared output paths or declared working directory. An earlier version scraped path-like tokens from the subtask scope prose and committed only matching changes; that whitelist silently dropped deliverables written to subdirectories (for example an entire `server/` tree), leaving dependent subtasks unable to see the work.

Two defensive rules keep that broad capture safe:

- **Nested git repositories are skipped.** Scaffolders such as create-react-app and Vite run their own `git init`, so a changed subdirectory can contain its own `.git`. Agentweaver walks each changed path and excludes anything at or under such a nested repository, because libgit2 would otherwise stage it as an empty gitlink (a submodule pointer) and lose the actual file tree. Skipped nested-repo roots are logged.
- **Blank projects are seeded with a baseline `.gitignore`.** When a blank project is initialized, Agentweaver writes a baseline ignore file — covering `node_modules/`, `dist/`, `build/`, `.venv/`, `__pycache__/`, `.env*`, `bin/`, `obj/`, and similar — and commits it in the initial commit, without ever clobbering an existing `.gitignore`. This keeps dependency and build artifacts out of the scope-independent staging set.

Agentweaver avoids empty commits. If staging produces no difference from HEAD, it returns the existing HEAD tree hash. That lets the workflow treat the child as a no-change result instead of manufacturing a zero-diff commit that looks like delivered work.

The diff shown to reviewers is not the last commit diff. It is the full candidate diff from the originating branch tip to the run branch tip. That is the right unit for review because it answers, "What would this run add to the target branch?"

## Review and merge safety

Merging is guarded in two layers.

The database layer controls state transitions. A run must move through compare-and-set style states such as `awaiting_review -> merging` or `awaiting_review -> committing -> merging`. This prevents two approvals, commits, declines, or request-changes operations from winning the same run.

The repository layer uses a per-repository merge lock. In PostgreSQL deployments, the lock is a session advisory lock keyed by canonical repository path so it spans API replicas. In SQLite/local development, the lock falls back to a process-wide semaphore. That serializes approvals for the same repository and closes timing windows where two runs could both inspect the same target branch tip and then race to update it.

The merge algorithm then checks:

1. the run branch still exists;
2. the originating branch still exists;
3. the run branch tree hash equals the approved tree hash;
4. the worktree branch is not already contained in the originating branch;
5. the target branch can be advanced safely.

For a checked-out originating branch, Agentweaver first attempts to commit modified or
type-changed tracked content, preserving it in history before recomputing the merge
base. It skips this step during a sequencer operation or with conflicted index entries.
If the resulting working tree is clean, a hard reset keeps the branch, index, and files
aligned with the merge result. Remaining dirty paths must reconcile losslessly with
that result or the merge returns a retriable `Blocked` outcome. It never advances a
checked-out branch ref while leaving its index and working tree stale.

Final three-way merges also preserve the originating branch's centrally maintained
Squad bookkeeping ledgers. This special handling does not suppress genuine conflicts
in application files (`WorktreeManager.cs:2031`; `SquadStateMergeTests.cs:120`).

Conflicts are terminal for that merge attempt. The run becomes `merge_failed`, conflicting files are stored where available, and the worktree is preserved for inspection.

## Detached state and dirty worktrees

A detached HEAD in the base repository is not treated as "the originating branch is checked out." In that case Agentweaver uses the ref-only path and updates the branch ref without touching the working tree. This is safe specifically because nothing reads the working tree/index relative to that ref while it is not checked out.

When the originating branch IS checked out, a ref-only update is never safe: it would advance HEAD's branch ref while leaving the index/working tree pointed at the old tree, so any path the merge added or changed but the stale index doesn't have appears as a staged deletion — even though it is fully present and correct in the new HEAD commit (this was the root cause of issue #348, where a completed run's working directory was left with staged deletions of its own committed output). So when the originating branch is checked out, Agentweaver checks for conditions that would make a hard reset unsafe:

- a merge, rebase, cherry-pick, revert, or bisect in progress;
- conflicted index entries;
- staged changes;
- modified or deleted tracked files;
- untracked files that would be overwritten by the merge result.

Sequencer state and conflicted indexes always block the merge outright — the user must resolve them first. After the tracked-content auto-commit attempt, Agentweaver compares each remaining dirty path's current content (working-directory bytes, or the index blob if no working-directory copy exists) against the merge result tree. If every affected path is byte-identical to the result (or has no content on disk/in the index at all — e.g. a stale staged deletion of a file the run never touched), the working tree is reconciled with a hard reset and the merge proceeds (`merge_mode: working-tree-reconciled`). Non-colliding untracked files do not prevent reconciliation. Otherwise the merge is blocked rather than corrupting the working directory or silently discarding local edits.

## Coordinator integration branches

Coordinator children use isolated execution checkouts and publish candidate content to
their authoritative branches. They do not collaborate by editing one shared mutable
orchestration worktree. Dependent children start from integration content verified to
contain their prerequisites; collaboration passes through committed content.

For the final assembly, Agentweaver creates an integration branch named:

```text
agentweaver/integration/{coordinatorRunId}
```

It builds that branch headlessly from the originating branch tip and merges eligible child branches in dependency order. "Headless" means it operates on git trees and refs without checking out the integration branch into a working directory.

Integration assembly is not the same conflict policy as the final merge. The git helper
processes the supplied child branches in order, skipping missing or empty branches and
already-contained tips. It fast-forwards where possible. On a tree conflict with a
merge base, it overlays the later child's changes onto the accumulated tree and records
the auto-resolution; without a merge base, it returns a conflict instead of a successful
aggregate. A successful result includes the aggregate diff, tree hash, and recorded
auto-resolutions (`WorktreeManager.cs:878–986`).

The aggregate then passes through the gates authored for the selected workflow. This
git-content view does not prescribe a fixed sequence of RAI or human gates. Final merge
still verifies the reviewed tree and can return a conflict or retriable repository block.

## Remote boundary

Local branch creation, commits, tree/diff inspection, review, and merge are separate
from remote publication. The `open_pull_request` action can create a GitHub pull request
using a live run-bound repository capability. It skips projects without a connected
GitHub repository and reports failure when the required capability is unavailable.
Creating the pull request is not evidence that this action pushes the head branch:
the executor calls the PR client with the selected head and base, without a git-push
step (`packages/Agentweaver.AgentRuntime/Workflow/OpenPullRequestTurnExecutor.cs:111–141`).

This boundary keeps candidate-content reasoning local and deterministic: Agentweaver can always explain a run through its branch, tree hash, and diff without depending on remote synchronization state.

## GitHub capabilities

Microsoft Entra establishes platform identity and project authorization. GitHub is a separately brokered capability for repository and Copilot operations. A GitHub connection cannot grant platform or project access.

The API creates and tracks GitHub capability handoffs for the authenticated platform user. Sandboxes receive only run-scoped capability data during `/configure`; they do not retrieve ambient GitHub tokens or read a per-user Key Vault token store.

Git operations use only the capability required for the operation. A missing capability fails closed rather than using a configured or shared fallback credential.

## Failure modes and how to reason about them

### Originating branch missing

Worktree creation fails if the originating branch does not exist. This is a submission/setup failure, not an agent failure. The run cannot safely infer a starting point.

Reasoning model: every run branch must be derived from a known branch tip.

### Worktree directory missing after restart

The database can remember a worktree path while the physical directory is gone. Agentweaver can recreate the worktree if the branch still exists. It prunes stale git worktree admin entries first because git may still believe the missing worktree has the branch checked out.

Reasoning model: git branch state and database metadata are durable; ephemeral worktree directories can be reconstructed when enough metadata remains.

### Resilient worktree deletion on Azure Files SMB

Worktree directories are deleted and recreated constantly: `AddDetachedWorktree` destructively recreates the shared `assembly-build-test-{…}` worktree on every assembly Build & Test, and the teardown paths remove run worktrees after merge. On the Azure Files **SMB** volume that backs `/workspace` in the cloud, a plain `Directory.Delete(path, recursive: true)` of a populated native `node_modules` tree (for example `better-sqlite3` with deep `build/Release/obj/gen/sqlite3` build artifacts) can throw `IOException: Directory not empty` (ENOTEMPTY): the BCL removes children and then rmdir's the parent, but SMB's directory-listing metadata is only eventually consistent, so a child unlink returns success while the parent's rmdir still sees the stale entry. A single transient failure in `AddDetachedWorktree` re-threw and dead-ended assembly Build & Test.

The worktree delete sites now route through `WorktreeManager.DeleteDirectoryResilient` (`apps/Agentweaver.Api/Git/WorktreeManager.cs:272`), a bounded retry that absorbs the SMB eventual-consistency window:

- **Fast path first.** Attempt 1 is the plain top-down `Directory.Delete(recursive: true)` with no extra work, so the common success case — which runs on every assembly and teardown — pays nothing.
- **Bounded retry on `IOException` / `UnauthorizedAccessException` only.** Up to four attempts total with short backoff (~150 → 300 → 600 ms, under ~2 s total — not minutes, not exponential-to-30 s), clearing read-only attributes between attempts (needed on Windows dev machines, a harmless no-op on Linux).
- **Bottom-up last resort.** On the final attempt only, it deletes deepest-first (files then directories) rather than top-down. The manual recursion is refused unless the target is under the worktree base path (reusing the existing `IsPathUnder` guard, `:221`), so a bad path can never walk outside `_basePath`.
- **Never silent-succeed.** If the directory still exists after all attempts, the last exception is re-thrown. Returning while a non-empty directory survived would let the next `git worktree add` build on a dirty tree and produce a corrupt or misleading Build & Test — so that outcome is designed out.

Applied at `AddDetachedWorktree` (the terminal failing site, `:198`), `RemoveDetachedWorktree` (`:232`), `PruneWorktreesCheckedOutOnBranch` (`:1269`), and `RemoveWorktree` (`:1871`). This is deliberately **not** a lingering-file-handle fix: on Linux `unlink` succeeds on open files (orphaning the inode), so it never causes ENOTEMPTY, and there is no "kill the build process first" step. It is a filesystem-robustness fix, kept general rather than `better-sqlite3`-specific.

Reasoning model: on an eventually-consistent network filesystem a delete that "failed" may already be converging — a bounded retry is correct, but silently proceeding on a surviving directory is not.

### Orphaned worktree branch

Worktrees are provisioned through the git CLI (`git worktree add -b agentweaver/{runId} …`), which does **not** create a throw-away branch named after the worktree. Older builds (pre-v0.9.33) used LibGit2Sharp's worktree add, whose underlying `git_worktree_add` always created such a `{runId}`-named branch as a side effect. During a rolling restart a worktree may still have been provisioned by that old code, leaving an orphaned `{runId}` branch that would make a fresh `git worktree add` fail with a name conflict. Agentweaver deletes that orphaned branch before recreating the real `agentweaver/{runId}` worktree; for worktrees created by the current git-CLI path no such branch exists, so the deletion is a harmless no-op.

Reasoning model: the run branch is `agentweaver/{runId}`; a plain `{runId}` branch is a legacy LibGit2Sharp implementation artifact.

### No changes

If an agent changes nothing, Agentweaver does not create an empty candidate commit. It returns the existing tree hash and the workflow can mark the run as no-change/completed.

Reasoning model: a zero-diff commit should not masquerade as delivered work.

### Tree hash mismatch

If the run branch tree no longer matches the approved tree hash, merge fails. This protects against changes after review, accidental manual mutation of the worktree, and restart races.

Reasoning model: approval binds to content, not to a mutable branch name.

### Merge conflicts

When the originating branch has diverged from the run branch and a three-way merge conflicts, the run becomes `merge_failed` and the worktree is preserved.

Reasoning model: Agentweaver can identify and preserve the conflict state, but it should not invent a resolution.

### Repository busy

Concurrent approvals for the same repository are serialized. If the repository lock cannot be acquired quickly, the operation returns a retriable conflict rather than racing.

Reasoning model: one repository branch update at a time keeps branch-tip reasoning valid.

### Dirty base checkout

If the originating branch is checked out and dirty, Agentweaver blocks sequencer or
index-conflict states, otherwise first attempts to preserve modified/type-changed
tracked content in a commit. Any remaining dirty state must reconcile losslessly onto
the merge result or block. Agentweaver never advances the branch ref while leaving the
checked-out working tree/index unsynced with it, since that desync is what produced
staged deletions of committed content in issue #348.

Reasoning model: advancing a ref while a branch is checked out is only safe when the index/working tree end up matching that ref exactly — so a ref-only update must never be used for a checked-out branch, only reconcile-then-reset or a hard block.

### Interrupted commit or merge

Startup recovery reverts interrupted `committing` and `merging` states back to `awaiting_review` where possible. For interrupted commits, it can recover the current worktree HEAD tree hash so the user can retry.

Reasoning model: after a crash, prefer a retryable review state over pretending a partial operation completed.

### GitHub signed out or refresh failed

GitHub project creation and GitHub repository/account listing require a valid token. If no token is available or refresh fails, the API fails closed and asks for sign-in.

Reasoning model: cloning or listing with ambiguous credentials creates confusing partial state; authentication is a precondition.

## Invariants

A rebuild should preserve these rules:

1. **Every normal run has one deterministic branch**: `agentweaver/{runId}`.
2. **Every normal run has one isolated worktree** before agent execution begins.
3. **Worktree path and branch are persisted before the agent writes files**.
4. **The database stores metadata; git stores file content**.
5. **Candidate review is based on diff and tree hash from originating branch to run branch**.
6. **Empty commits are avoided** so no-change work is represented honestly.
7. **Human approval binds to a tree hash** and merge refuses mismatches.
8. **Repository branch updates are serialized per repository**.
9. **Successful merges clean up run worktrees and branches**.
10. **Conflicted merges preserve worktrees** for human inspection.
11. **Coordinator integration branches are assembled headlessly**, with later-child conflict resolutions recorded and unresolved assembly failures kept distinct from final merge conflicts.
12. **GitHub tokens are credentials, not project metadata**.
13. **Raw access tokens are not logged or stored in run/project records**.
14. **Worktree directory deletes are resilient to SMB eventual consistency** and never silently succeed while the directory still exists.

## Trade-offs

### Worktrees over copying directories

Git worktrees are more complex than copying a repository directory, but they avoid duplicated object databases and preserve normal branch semantics. A run's result is a branch and tree, not an ad hoc folder snapshot.

### Local merge over remote review

Agentweaver can complete review and merge locally without requiring a remote. That supports blank/local projects and keeps the default deployment simpler. The trade-off is that remote review systems are not the authoritative review surface.

### Ref-only fallback

Ref-only merge applies when the originating branch is not checked out, including a
detached base HEAD. It is not a fallback for a dirty checked-out target. Files in a
checkout of another branch do not change merely because the target ref advances.

### Committed collaboration between isolated children

Isolated child checkouts avoid shared mutable working directories. The cost is that
dependent work must wait for prerequisite content to be published and verified in the
integration base; another child's uncommitted edits are not a handoff mechanism.

### SQLite metadata plus git content

This split keeps large file content and history in git while SQLite tracks lifecycle state. The trade-off is recovery must reconcile two durable systems: database rows and repository refs/worktree admin state.

## Rebuild blueprint

If rebuilding the git integration subsystem, implement it in this order:

1. Define run metadata: repository path, originating branch, worktree path, worktree branch, tree hash, diff, status, merge result, merged commit hash, and conflict list.
2. Initialize blank repositories with a baseline `.gitignore` and an initial commit so default branches are never unborn and dependency/build artifacts stay untracked.
3. Clone GitHub repositories using ephemeral HTTPS credentials from a refresh-aware token provider.
4. Create deterministic run branches as `agentweaver/{runId}` from the originating branch.
5. Add run worktrees under a controlled base path using the run id as directory name.
6. Persist worktree path and branch before agent execution.
7. Execute agents with the worktree as their working directory and sandbox boundary.
8. Stage every changed, non-ignored file (including deletions and renames), skipping nested git repositories to avoid committing them as empty gitlinks.
9. Avoid empty commits; return the current HEAD tree hash for no-change results.
10. Store the candidate tree hash, full diff against the originating branch, and review-ready state.
11. Implement request-changes by reusing the same worktree and branch for revision.
12. Implement approval with database CAS transitions and a per-repository merge lock.
13. Verify the tree hash immediately before merge.
14. Merge by fast-forward when possible, otherwise create a merge commit; use ref-only update only when the originating branch is not checked out. For checked-out targets, preserve eligible dirty tracked content first, then reset, reconcile losslessly, or block.
15. Remove worktree and branch after successful merge; preserve them after conflict.
16. Recover startup states by failing stranded in-progress runs, reverting interrupted committing/merging states, validating review-ready worktrees, and recreating missing worktrees when branch metadata is sufficient.
17. Add coordinator assembly as a separate headless integration-branch flow if multi-agent fan-out is required.

## Common gotchas

- `agentweaver/{runId}` is the real run branch; a plain run-id branch is a legacy (pre-v0.9.33) LibGit2Sharp worktree side effect — current git-CLI provisioning never creates one.
- A run branch name is not enough for approval. The tree hash is the content identity.
- The diff shown for review is against the originating branch, not just the last commit.
- A missing physical worktree can be recoverable if the database row and git branch still exist.
- A missing branch is much harder to recover because git has lost the candidate content reference.
- Dirty checked-out target branches either reconcile onto the merge result via a hard reset (when safe) or block the merge outright — they never merge ref-only while checked out, since that would desync the index/working tree from the advanced ref.
- Coordinator children use isolated checkouts; prerequisite handoffs use published, verified integration content rather than shared uncommitted files.
- Worktree deletes on Azure Files SMB can transiently fail with `Directory not empty`; `WorktreeManager.DeleteDirectoryResilient` retries with backoff and never silently proceeds while the directory still exists (see [Resilient worktree deletion](#resilient-worktree-deletion-on-azure-files-smb)).
- The GitHub API usage is raw `HttpClient`, not Octokit.

<details id="diagram-context-git-integration-fig1" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Isolated candidates, guarded merge</td></tr>
<tr><td>takeaway</td><td>Runs edit isolated candidates; approval names a tree, not permission to bypass Git guards.</td></tr>
<tr><td>group-title-0</td><td>BRANCH AND WORKSPACE</td></tr>
<tr><td>group-title-1</td><td>CANDIDATE CONTENT</td></tr>
<tr><td>group-title-2</td><td>REVIEWED IDENTITY AND MERGE</td></tr>
<tr><td>Originating branch</td><td>Originating branch</td></tr>
<tr><td>Originating branch</td><td>Resolve starting commit</td></tr>
<tr><td>Originating branch</td><td>branch tip</td></tr>
<tr><td>Run branch</td><td>Run branch</td></tr>
<tr><td>Run branch</td><td>Deterministic branch name</td></tr>
<tr><td>Run branch</td><td>agentweaver/{runId}</td></tr>
<tr><td>Isolated worktree</td><td>Isolated worktree</td></tr>
<tr><td>Isolated worktree</td><td>Agent edits candidate files</td></tr>
<tr><td>Isolated worktree</td><td>not origin checkout</td></tr>
<tr><td>Capture changes</td><td>Capture changes</td></tr>
<tr><td>Capture changes</td><td>Stage non-ignored changes</td></tr>
<tr><td>Capture changes</td><td>no empty commit</td></tr>
<tr><td>Candidate tree</td><td>Candidate tree</td></tr>
<tr><td>Candidate tree</td><td>Committed content identity</td></tr>
<tr><td>Candidate tree</td><td>tree SHA</td></tr>
<tr><td>Full diff</td><td>Full diff</td></tr>
<tr><td>Full diff</td><td>Compare branch-tip trees</td></tr>
<tr><td>Full diff</td><td>origin vs candidate</td></tr>
<tr><td>Approved identity</td><td>Approved identity</td></tr>
<tr><td>Approved identity</td><td>Expected tree must match</td></tr>
<tr><td>Approved identity</td><td>expectedTreeHash</td></tr>
<tr><td>Guarded merge</td><td>Guarded merge</td></tr>
<tr><td>Guarded merge</td><td>Containment + origin safety</td></tr>
<tr><td>Guarded merge</td><td>checked-out or ref-only</td></tr>
<tr><td>Merge outcome</td><td>Merge outcome</td></tr>
<tr><td>Merge outcome</td><td>Advance, block or conflict</td></tr>
<tr><td>Merge outcome</td><td>dirty origin protected</td></tr>
<tr><td>e0</td><td>create</td></tr>
<tr><td>e1</td><td>checkout</td></tr>
<tr><td>e2</td><td>capture</td></tr>
<tr><td>e3</td><td>commit</td></tr>
<tr><td>e4</td><td>compare</td></tr>
<tr><td>e5</td><td>review</td></tr>
<tr><td>e6</td><td>match</td></tr>
<tr><td>e7</td><td>merge</td></tr>
<tr><td>groups</td><td>BRANCH AND WORKSPACE; CANDIDATE CONTENT; REVIEWED IDENTITY AND MERGE</td></tr>
</tbody></table>
</details>

<details id="diagram-context-git-integration-fig2" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Child content and integration bases</td></tr>
<tr><td>takeaway</td><td>Published branch content crosses child boundaries; a shared mutable checkout does not.</td></tr>
<tr><td>group-title-0</td><td>AUTHORITATIVE INPUTS</td></tr>
<tr><td>group-title-1</td><td>ACCUMULATION AND CONFLICT HANDLING</td></tr>
<tr><td>group-title-2</td><td>DEPENDENT CHILD OR FINAL REVIEW</td></tr>
<tr><td>Origin tip</td><td>Origin tip</td></tr>
<tr><td>Origin tip</td><td>Reset integration branch</td></tr>
<tr><td>Origin tip</td><td>authoritative repository</td></tr>
<tr><td>Published children</td><td>Published children</td></tr>
<tr><td>Published children</td><td>Isolated execution checkouts</td></tr>
<tr><td>Published children</td><td>committed branches</td></tr>
<tr><td>Ordered accumulator</td><td>Ordered accumulator</td></tr>
<tr><td>Ordered accumulator</td><td>Skip empty or contained tips</td></tr>
<tr><td>Ordered accumulator</td><td>caller supplies order</td></tr>
<tr><td>Merge conflict?</td><td>Merge conflict?</td></tr>
<tr><td>Merge conflict?</td><td>A merge base is required</td></tr>
<tr><td>Merge conflict?</td><td>not always terminal</td></tr>
<tr><td>Later-child overlay</td><td>Later-child overlay</td></tr>
<tr><td>Later-child overlay</td><td>Apply delta from merge base</td></tr>
<tr><td>Later-child overlay</td><td>record auto-resolution</td></tr>
<tr><td>Unresolved conflict</td><td>Unresolved conflict</td></tr>
<tr><td>Unresolved conflict</td><td>No merge base: fail assembly</td></tr>
<tr><td>Unresolved conflict</td><td>no partial success</td></tr>
<tr><td>Integration snapshot</td><td>Integration snapshot</td></tr>
<tr><td>Integration snapshot</td><td>Ref, tree, diff, resolutions</td></tr>
<tr><td>Integration snapshot</td><td>content contract</td></tr>
<tr><td>Dependent-child base</td><td>Dependent-child base</td></tr>
<tr><td>Dependent-child base</td><td>Verify prerequisite reachability</td></tr>
<tr><td>Dependent-child base</td><td>isolated new checkout</td></tr>
<tr><td>Final aggregate review</td><td>Final aggregate review</td></tr>
<tr><td>Final aggregate review</td><td>Authored checks, reviewed tree</td></tr>
<tr><td>Final aggregate review</td><td>then guarded merge</td></tr>
<tr><td>e0</td><td>reset</td></tr>
<tr><td>e1</td><td>ordered</td></tr>
<tr><td>e2</td><td>conflict</td></tr>
<tr><td>e3</td><td>base</td></tr>
<tr><td>e4</td><td>no base</td></tr>
<tr><td>e5</td><td>resolved</td></tr>
<tr><td>e6</td><td>clean</td></tr>
<tr><td>e7</td><td>dependency</td></tr>
<tr><td>e8</td><td>final</td></tr>
<tr><td>groups</td><td>AUTHORITATIVE INPUTS; ACCUMULATION AND CONFLICT HANDLING; DEPENDENT CHILD OR FINAL REVIEW</td></tr>
</tbody></table>
</details>
