# Issue Lifecycle — Repo Connection & PR Flow

Reference for connecting Squad to a repository and managing the issue→branch→PR→merge lifecycle.

## Repo Connection Format

When connecting Squad to an issue tracker, store the connection in `.squad/team.md`:

```markdown
## Issue Source

**Repository:** {owner}/{repo}
**Connected:** {date}
**Platform:** {GitHub | Azure DevOps | Planner}
**Filters:**
- Labels: `{label-filter}`
- Project: `{project-name}` (ADO/Planner only)
- Plan: `{plan-id}` (Planner only)
```

**Detection triggers:**
- User says "connect to {repo}"
- User says "monitor {repo} for issues"
- Ralph is activated without an issue source

## Platform-Specific Issue States

Each platform tracks issue lifecycle differently. Squad normalizes these into a common board state.

### GitHub

| GitHub State | GitHub API Fields | Squad Board State |
|--------------|-------------------|-------------------|
| Open, no assignee | `state: open`, `assignee: null` | `untriaged` |
| Open, assigned, no branch | `state: open`, `assignee: @user`, no linked PR | `assigned` |
| Open, branch exists | `state: open`, linked branch exists | `inProgress` |
| Open, PR opened | `state: open`, PR exists, `reviewDecision: null` | `needsReview` |
| Open, PR approved | `state: open`, PR `reviewDecision: APPROVED` | `readyToMerge` |
| Open, approved PR behind dev | branch is not up to date | `needsUpdate` |
| Open, updated branch checks running | required checks pending after sync | `ciRunning` |
| Open, changes requested | `state: open`, PR `reviewDecision: CHANGES_REQUESTED` | `changesRequested` |
| Open, CI failure | `state: open`, PR `statusCheckRollup: FAILURE` | `ciFailure` |
| Closed | `state: closed` | `done` |

**Issue labels used by Squad:**
- `squad` — Issue is in Squad backlog
- `squad:{member}` — Assigned to specific agent
- `squad:untriaged` — Needs triage
- `go:needs-research` — Needs investigation before implementation
- `priority:p{N}` — Priority level (0=critical, 1=high, 2=medium, 3=low)
- `next-up` — Queued for next agent pickup

**Branch naming convention:**
```
squad/{issue-number}-{kebab-case-slug}
```
Example: `squad/42-fix-login-validation`

### Azure DevOps

| ADO State | Squad Board State |
|-----------|-------------------|
| New | `untriaged` |
| Active, no branch | `assigned` |
| Active, branch exists | `inProgress` |
| Active, PR opened | `needsReview` |
| Active, PR approved | `readyToMerge` |
| Resolved | `done` |
| Closed | `done` |

**Work item tags used by Squad:**
- `squad` — Work item is in Squad backlog
- `squad:{member}` — Assigned to specific agent

**Branch naming convention:**
```
squad/{work-item-id}-{kebab-case-slug}
```
Example: `squad/1234-add-auth-module`

### Microsoft Planner

Planner does not have native Git integration. Squad uses Planner for task tracking and GitHub/ADO for code management.

| Planner Status | Squad Board State |
|----------------|-------------------|
| Not Started | `untriaged` |
| In Progress, no PR | `inProgress` |
| In Progress, PR opened | `needsReview` |
| Completed | `done` |

**Planner→Git workflow:**
1. Task created in Planner bucket
2. Agent reads task from Planner
3. Agent creates branch in GitHub/ADO repo
4. Agent opens PR referencing Planner task ID in description
5. Agent marks task as "Completed" when PR merges

## Issue → Branch → PR → Merge Lifecycle

### 1. Issue Assignment (Triage)

**Trigger:** Ralph detects an untriaged issue or user manually assigns work.

**Actions:**
1. Read `.squad/routing.md` to determine which agent should handle the issue
2. Apply `squad:{member}` label (GitHub) or tag (ADO)
3. Transition issue to `assigned` state
4. Optionally spawn agent immediately if issue is high-priority

**Issue read command:**
```bash
# GitHub
gh issue view {number} --json number,title,body,labels,assignees

# Azure DevOps
az boards work-item show --id {id} --output json
```

### 2. Branch Creation (Start Work)

**Trigger:** Agent accepts issue assignment and begins work.

**Actions:**
1. Fetch current `origin/dev`; this repository uses `dev` as its protected integration branch.
   `main` remains stable/published-only; release branches are managed under
   [RELEASING.md](../../RELEASING.md).
2. Create the `squad/{issue-number}-{slug}` branch using the workspace rule in
   [`CONTRIBUTING.md`](../../CONTRIBUTING.md#ai-agent-contributions):
   locally run agents use a dedicated `.worktrees/{branch-slug}` worktree; hosted agents
   use the platform-provided isolated environment; humans may use either a worktree or a
   normal branch in the primary checkout.
3. Transition issue to `inProgress` state

**Branch creation commands:**

**Locally run Squad agent (required):**
```bash
git fetch origin dev
git worktree add ".worktrees/{branch-slug}" -b squad/{issue-number}-{slug} origin/dev
cd ".worktrees/{branch-slug}"
```

**Human contributor (optional worktree):**
```bash
git fetch origin dev
git checkout -b squad/{issue-number}-{slug} origin/dev
```

Hosted agents do not run local worktree commands; their host creates an isolated branch and
environment. See [worktree-reference.md](worktree-reference.md) for mechanics.

### 3. Implementation & Commit

**Actions:**
1. Agent makes code changes
2. Commits reference the issue number
3. Pushes branch to remote

**Commit message format:**
```
{type}({scope}): {description} (#{issue-number})

{detailed explanation if needed}

{breaking change notice if applicable}

Closes #{issue-number}

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

**Commit types:** `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `perf`, `style`, `build`, `ci`

**Push command:**
```bash
git push -u origin squad/{issue-number}-{slug}
```

### 4. PR Creation

**Trigger:** Agent completes implementation and is ready for review.

**Actions:**
1. Open PR from feature branch to base branch
2. Reference issue in PR description
3. Apply labels if needed
4. Transition issue to `needsReview` state

**PR creation commands:**

**GitHub:**
```bash
gh pr create --title "{title}" \
  --body "Closes #{issue-number}\n\n{description}" \
  --head squad/{issue-number}-{slug} \
  --base dev
```

**Azure DevOps:**
```bash
az repos pr create --title "{title}" \
  --description "Closes #{work-item-id}\n\n{description}" \
  --source-branch squad/{work-item-id}-{slug} \
  --target-branch dev
```

**PR description template:**
```markdown
Closes #{issue-number}

## Summary
{what changed}

## Changes
- {change 1}
- {change 2}

## Testing
{how this was tested}

{If working as a squad member:}
Working as {member} ({role})

{If needs human review:}
⚠️ This task was flagged as "needs review" — please have a squad member review before merging.
```

### 5. PR Review & Updates

**Review states:**
- **Approved** → `readyToMerge`
- **Changes requested** → `changesRequested`
- **CI failure** → `ciFailure`
- **Rejected / independent rewrite required** → reviewer-rejection lockout

**When changes are requested:**
1. Agent addresses feedback
2. Commits fixes to the same branch
3. Pushes updates
4. Requests re-review

Ordinary **changes requested** feedback never locks out the original author. Lockout applies
only when a Reviewer explicitly declares **Rejected / independent rewrite required** (use the
PR comment marker `REJECTED — requires independent rewrite`). A different agent must then
produce the next revision. Keep that marker on the PR so the handoff is auditable on GitHub;
use a `status:locked-out` label too if the repository provides it.

**Update workflow:**
```bash
# Make changes
# ⚠️ NEVER use `git add .` or `git add -A` — only stage files you intentionally changed
git add -- {specific files you modified}
git commit -m "fix: address review feedback"
git push
```

**Re-request review (GitHub):**
```bash
gh pr ready {pr-number}
```

### 6. Protected merge admission

**Trigger:** PR is approved and CI passes.

**Always verify live state before merging — never assume from a diff review alone:**
```bash
gh pr view {pr-number} --json mergeable,mergeStateStatus,statusCheckRollup \
  --jq '{mergeable, mergeState: .mergeStateStatus, checks: [.statusCheckRollup[] | {name, status, conclusion}]}'
```
Confirm `mergeable` is `MERGEABLE` (no merge conflicts) and every required check
(`.NET tests`, `Node toolchain tests`, `Web tests`, `Docs build`) shows
`conclusion: SUCCESS`. If a required check failed, rerun it once
(`gh run rerun {run-id} --failed`) to rule out a known CPU-contention flake before
concluding the PR's own changes caused a real failure.

**GitHub:** first ensure the branch is current with `dev`. If protection says
it is behind, fetch `origin/dev`, update the feature branch, push, and wait
for every required check to rerun.

```bash
gh pr merge {pr-number} --squash --delete-branch
```

If another PR merges first, GitHub marks this PR out of date:

1. Move it to `needsUpdate`.
2. Fetch current `origin/dev` and update the feature branch.
3. Resolve conflicts; never bypass a blocking check.
4. Run relevant tests, push, and wait for required CI again.
5. Squash-merge only when the branch is current and green.

This strict fallback causes update/retest churn under concurrent PRs, but
GitHub Merge Queue is unavailable while Agentweaver is personal-account-owned.
The [Branch Topology Activation Plan](../../CONTRIBUTING.md#branch-topology)
defines the measurable conditions for changing this model.

**Azure DevOps** (when the connected repository is ADO rather than Agentweaver GitHub):
```bash
az repos pr update --id {pr-id} --status completed --delete-source-branch true
```

**Post-merge actions:**
1. Issue automatically closes (if "Closes #{number}" is in PR description)
2. Feature branch is deleted
3. Squad board state transitions to `done`
4. Worktree cleanup for locally run agents

### 7. Cleanup

**Standard workflow cleanup:**
```bash
git checkout dev
git pull
git branch -d squad/{issue-number}-{slug}
```

**Locally run agent worktree cleanup:**
```bash
cd {original-cwd}
git worktree remove .worktrees/{branch-slug}
```

## Spawn Prompt Additions for Issue Work

When spawning an agent to work on an issue, include this context block:

```markdown
## ISSUE CONTEXT

**Issue:** #{number} — {title}
**Platform:** {GitHub | Azure DevOps | Planner}
**Repository:** {owner}/{repo}
**Assigned to:** {member}

**Description:**
{issue body}

**Labels/Tags:**
{labels}

**Acceptance Criteria:**
{criteria if present in issue}

**Branch:** `squad/{issue-number}-{slug}`

**Your task:**
{specific directive to the agent}

**After completing work:**
1. Commit with message referencing issue number
2. Push branch
3. Open PR using:
   ```
   gh pr create --title "{title}" --body "Closes #{number}\n\n{description}" --head squad/{issue-number}-{slug} --base dev
   ```
4. Report PR URL to coordinator
```

## Ralph's Role in Issue Lifecycle

Ralph (the work monitor) continuously checks issue and PR state:

1. **Triage:** Detects untriaged issues, assigns `squad:{member}` labels
2. **Spawn:** Launches agents for assigned issues
3. **Monitor:** Tracks PR state transitions (needsReview → changesRequested → readyToMerge)
4. **Merge:** Syncs stale PRs, waits for rerun CI, and squash-merges green PRs
5. **Cleanup:** Marks issues as done when PRs merge

**Ralph's work-check cycle:**
```
Scan → Categorize → Dispatch → Watch → Report → Loop
```

See `.squad/templates/ralph-reference.md` for Ralph's full lifecycle.

## PR Review Handling

### Automated Approval (CI-only projects)

If the project has no human reviewers configured:
1. PR opens
2. CI runs
3. If `dev` moved, Ralph updates the branch and waits for CI again
4. Ralph squash-merges the current green PR
5. Issue closes

### Human Review Required

If the project requires human approval:
1. PR opens
2. Human reviewer is notified (GitHub/ADO notifications)
3. Reviewer approves or requests changes
4. If approved + current with `dev` + CI passes, Ralph squash-merges
5. If changes requested, agent addresses feedback

### Squad Member Review

If the issue was assigned to a squad member and they authored the PR:
1. Another squad member reviews (conflict of interest avoidance)
2. Ordinary **changes requested** feedback can be revised by the original author.
3. Only an explicit **Rejected / independent rewrite required** declaration locks out the
   original author; the reviewer records `REJECTED — requires independent rewrite` on the PR,
   then another agent produces the next revision.

## Common Issue Lifecycle Patterns

### Pattern 1: Quick Fix (Single Agent, No Review)
```
Issue created → Assigned to agent → Branch created → Code fixed →
PR opened → Updated to latest dev → CI passes → Squash-merged → Issue closed
```

### Pattern 2: Feature Development (Human Review)
```
Issue created → Assigned to agent → Branch created → Feature implemented →
PR opened → Human reviews → Changes requested → Agent fixes →
Re-reviewed → Approved → Queued → Merge-group CI → Merged → Issue closed
```

### Pattern 3: Research-Then-Implement
```
Issue created → Labeled `go:needs-research` → Research agent spawned →
Research documented → Research PR merged → Implementation issue created →
Implementation agent spawned → Feature built → PR merged
```

### Pattern 4: Parallel Locally Run Agents
```
Epic issue created → Decomposed into sub-issues → Each sub-issue assigned →
Multiple agents work in parallel worktrees → PRs opened concurrently →
All PRs reviewed → Each updates/retests after preceding merges → All PRs merged → Epic closed
```

## Anti-Patterns

- ❌ Creating branches without linking to an issue
- ❌ Committing without issue reference in message
- ❌ Opening PRs without "Closes #{number}" in description
- ❌ Merging PRs before CI passes
- ❌ Leaving feature branches undeleted after merge
- ❌ Using `checkout -b` when parallel agents are active (causes working directory conflicts)
- ❌ Manually transitioning issue states — let the platform and Squad automation handle it
- ❌ Skipping the branch naming convention — breaks Ralph's tracking logic

## Migration Notes

**v0.8.x → v0.9.x (Worktree Support):**
- `checkout -b` → `git worktree add` for parallel agents
- Worktree cleanup added to post-merge flow
- `TEAM_ROOT` passing to agents to support worktree-aware state resolution

This template will be updated as worktree lifecycle support lands in #525.
