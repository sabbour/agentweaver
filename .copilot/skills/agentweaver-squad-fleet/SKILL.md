---
name: agentweaver-squad-fleet
description: >
  Launch Squad agents in parallel across multiple GitHub issues using Fleet mode —
  each agent gets its own git worktree and branch so they never step on each other.
  The skill handles the full loop: pull open issues from GitHub, sort by priority,
  check for file-overlap conflicts, create worktrees, spawn agents in parallel, collect
  results, open PRs, merge, close issues, and print the updated status board.
  Use this skill whenever the user says "Ralph, go", "work on issues", "pick up the
  backlog", "squad go", "fleet mode", "work in parallel", "spin up the team", or asks
  Squad to start chipping at GitHub issues. Also trigger when the user says "keep
  working" or "continue" while there are open issues with go:yes. This is the primary
  way Squad executes bulk issue work — always prefer it over working issues one at a time.
---

# Squad Fleet — Parallel Issue Execution

Fleet mode is how Squad maximizes throughput: instead of working issues one by one,
multiple agents run simultaneously in isolated git worktrees. Each agent owns one
issue, one branch, one PR. They can't conflict with each other because they're in
separate checkouts.

## Step 1 — Show the current board

Before doing anything, print a status snapshot so Ahmed can see where things stand.
Run the issue-status skill (`.copilot/skills/agentweaver-issue-status/SKILL.md`) or call:

```bash
gh issue list --state open --label "go:yes" \
  --json number,title,labels,state --limit 30
```

Display as a summary table: `# | Title | Squad | Priority | Type`.

## Step 2 — Build the work queue

Fetch all open issues labeled `go:yes`. Sort them in this priority order:
1. `priority:p0` bugs first (blocking / production outage)
2. `priority:p1` bugs
3. `priority:p0` / `priority:p1` chores
4. `priority:p2` bugs
5. Features and remaining chores
6. Spikes last

Within each tier, sort by issue number ascending (older issues first).

Skip issues that already have an open PR (`gh pr list --search "#{N}"`) or an
active worktree (`git worktree list`).

## Step 3 — Conflict analysis

Before spawning, check whether any queued issues would touch overlapping files.
Two issues conflict when their domains have shared file ownership:

| If both issues touch... | They conflict |
|------------------------|---------------|
| Same component file (`apps/web/src/...`) | Yes |
| Same API controller/service | Yes |
| Same k8s manifest | Yes |
| Different domains entirely (e.g. frontend + infra) | No |

**How to check:** Read the issue title and labels. Use the routing table in
`.squad/routing.md` to determine the domain files each issue would touch. If
two issues both route to `squad:trinity` AND likely edit the same component,
serialize them (add to "stacked" queue). If they route to different squad
members, they're safe to parallelize.

Group the queue into **parallel batches**:
- Batch 1: all conflict-safe issues (run simultaneously)  
- Batch 2+: dependent or conflicting issues (run after batch 1 merges)

Show the batching plan before spawning:
```
🚀 Fleet plan:
   Batch 1 (parallel): #95 Trinity, #97 Smith+Tank, #101 Scribe
   Batch 2 (after merge): #100 Trinity
```

## Step 4 — Create worktrees

For each issue in the current batch, create a dedicated worktree. The worktree
gives the agent a fully independent checkout — it can commit, build, and test
without touching main or other issues' branches.

```bash
# Naming conventions
ISSUE=95
SLUG="confirm-button-double-submit"  # kebab-case from issue title
REPO_DIR="C:\Users\asabbour\Git\agentweaver"
WORKTREE_PATH="${REPO_DIR}-issue-${ISSUE}"   # sibling directory
BRANCH="squad/issue-${ISSUE}-${SLUG}"

# Create the worktree + branch
git -C "$REPO_DIR" worktree add "$WORKTREE_PATH" -b "$BRANCH" main

# Link node_modules to avoid reinstall (Windows)
cmd /c "mklink /J ${WORKTREE_PATH}\apps\web\node_modules ${REPO_DIR}\apps\web\node_modules" 2>nul || true

# Reuse if worktree already exists
git -C "$REPO_DIR" worktree list | grep "issue-${ISSUE}" && echo "reusing"
```

If a worktree already exists for an issue, reuse it — pull latest from its branch
and resume.

## Step 5 — Spawn agents in parallel (Fleet launch)

Spawn one agent per issue in the same turn (all background). This is Fleet mode —
maximum parallelism, all agents start simultaneously.

Each agent prompt must include:
- Issue number, title, full body
- `WORKTREE_PATH` — absolute path to their worktree
- `TEAM_ROOT` — path to `.squad/` (main checkout, not worktree)
- `BRANCH` — their branch name
- Domain routing: which squad member they are and what they own
- Commit format and PR instructions (see Step 6)
- The `squad-fleet` skill path for reference: `.copilot/skills/agentweaver-squad-fleet/SKILL.md`

**Minimal agent spawn prompt template:**
```
You are {AgentName}, the {Role}.
WORKTREE_PATH: {absolute path to this issue's worktree}
TEAM_ROOT: {main checkout}/.squad
BRANCH: squad/issue-{N}-{slug}
CURRENT_DATETIME: {datetime}

Issue #{N}: {title}
{full issue body}

Your job:
1. Work entirely inside WORKTREE_PATH — never switch branches or touch other worktrees
2. Implement the fix/feature described in the issue
3. Run relevant tests: npm --prefix apps\web test -- --run --testPathPattern={relevant}
4. Commit: git -C WORKTREE_PATH commit -m "type(scope): description (#N)"
5. Push: git -C WORKTREE_PATH push -u origin BRANCH
6. Open PR: gh pr create --title "type(scope): description (#N)" --body "Closes #N" --base main --head BRANCH
7. Report: issue number, files changed, test results, PR URL

Docs disposition from issue: {copy the ## Docs disposition section}
If docs are needed, note it — Scribe will pick up docs work after your PR merges.
```

## Step 6 — Commit and PR format

Every agent must follow this format:

**Commit message:** `type(scope): short description (#N)`
- Same `type(scope)` as the issue title
- Example: `bug(run-page): disable confirm button on click (#95)`

**PR title:** identical to commit message  
**PR body:**
```markdown
Closes #{issue_number}

## What changed
{brief description}

## Testing
{test command run + result}
```

If the issue body has a **Docs disposition** section saying docs are needed,
add a note in the PR body: `📝 Docs needed — see issue for disposition`.

## Step 7 — Collect results and merge

As agents complete, collect their results. For each completed agent:
1. Check tests passed
2. Check PR is open (`gh pr view {pr_number} --json state,checks`)
3. If CI is green → merge: `gh pr merge {pr_number} --squash --delete-branch`
4. Close the issue if not auto-closed: `gh issue close {N} --comment "Fixed in {PR_URL}"`
5. Clean up the worktree: `git worktree remove {path} --force && git branch -d {branch}`

If an agent failed or tests didn't pass, don't merge — report the failure and
queue it for retry or manual review.

## Step 8 — Docs pass

After all PRs in a batch merge, check each closed issue's **Docs disposition**:
- `docs-feature` needed → spawn Scribe with `.copilot/skills/agentweaver-docs-feature/SKILL.md`
- `docs-sync` needed → spawn Scribe with `.copilot/skills/agentweaver-docs-sync/SKILL.md`
- "No docs needed" / internal change → skip

## Step 9 — Print the updated board

After the batch completes, run the issue-status skill again to show the updated
pipeline. Highlight what changed:

```
✅ Batch 1 complete — 3 issues merged, 0 failed
📊 Updated board:
[issue-status table]

🔄 Batch 2 queued: #100 Trinity (chore(graph-view))
   Starting now...
```

If more batches remain, proceed immediately to Step 4 for the next batch.
If the board is clear, report: `📋 Board is clear.`

---

## Conflict-safe defaults

When in doubt about conflicts, be conservative: serialize rather than parallelize.
A merge conflict wastes more time than a sequential run.

**Always serialize:**
- Two issues editing the same React component
- Two issues touching the same API controller
- Any issue with `priority:p0` — run it alone first

**Always parallelize:**
- Frontend issue + backend issue (different file trees)
- Docs issue + any code issue
- Two issues in completely separate feature areas

## Integration with issue-status skill

The fleet skill feeds directly into the issue-status board:
- Before fleet: run status to show what's queued
- During fleet: each agent's commit SHA flows into the `Commit` column
- After fleet: merged PRs update `Deployed` (once AKS deploy runs)
- Docs pass updates the `Docs` column

See `.copilot/skills/agentweaver-issue-status/SKILL.md` for the full board format.
