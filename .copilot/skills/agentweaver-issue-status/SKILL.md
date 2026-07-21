---
name: agentweaver-issue-status
description: >
  Print a live pipeline status board for GitHub issues — showing each issue's
  assigned agent, current status (backlog/implementing/RCA/merged), merge commit SHA,
  whether it has been deployed to the cluster, and docs disposition. Use this skill
  whenever the user asks for a status report, status board, tracking table, "what's
  in flight", "what's been deployed", "show me the board", "squad status", "issue
  tracker", or wants to know the state of open or recently closed issues. Also trigger
  when the user asks about a specific issue's deployment or docs status.
---

# Issue Status Board

You are producing a pipeline status table for GitHub issues in this repo. The goal
is to give the user an at-a-glance view of where each issue stands across the full
lifecycle: filed → committed → merged → deployed → documented.

Run the bundled script to collect all data, then render the table.

## Step 1 — Run the data collection script

```bash
python .copilot/skills/agentweaver-issue-status/scripts/collect.py \
  --repo sabbour/agentweaver \
  [--filter bugs|features|chores|open|closed|all] \
  [--squad trinity|tank|smith|morpheus|link|seraph]
```

The script outputs a JSON array of issue records. Each record has:
- `number`, `title`, `agents` (list of squad member names), `status`
- `commit` (SHA of the commit mentioning this issue, or null)
- `deployed` (true/false/null — null means unknown)
- `docs` (true/false/null — true if docs were updated, false if needed, null if N/A)
- `pr_number`, `pr_state` (open/merged/null)

If the script isn't available or fails, collect the data manually:
1. `gh issue list --state all --limit 30 --json number,title,labels,state` 
2. `git log --oneline -50` — scan for `#N` references to find commits
3. `gh pr list --state all --json number,title,state,mergedAt,headRefName` — find PRs per issue
4. Check the live deployment's image tag and provenance with the current Node.js
   toolchain (`scripts/azure/variables.mjs`, `scripts/azure/deploy.mjs`) or the
   cluster's workload metadata. Do not use the removed `scripts/aks/` shell scripts.
   Resolve the deployed tag to its Git ref, then run
   `git log {deployed_sha}..HEAD --oneline` — issues whose commits appear here are
   **not** yet deployed.

## Step 2 — Determine status for each issue

Use this decision tree:

| Condition | Status label | Emoji |
|-----------|-------------|-------|
| Issue closed, merge commit found | `{short_sha}` | ✅ |
| Issue closed, no commit found | `closed` | ✅ |
| Issue open, PR merged to main | `merged` | ✅ |
| Issue open, PR open | `in review` | 🔄 |
| Issue open, commit on branch (no PR yet) | `implementing` | 🔄 |
| Issue open, `squad:smith` is sole assignee | `RCA in progress` | 🔍 |
| Issue open, `go:needs-research` label | `needs research` | 🔍 |
| Issue open, `go:yes`, no activity | `backlog` | 📋 |
| Issue open, `go:no` | `on hold` | ⏸️ |

For **deployed**: compare the commit SHA against what's on the cluster.
- Deployed = commit is an ancestor of (or equal to) the deployed image tag commit
- Not deployed = commit is newer than the deployed tag
- Unknown = no commit found

For **docs**:
- ✅ = a docs-related commit exists for this issue, OR issue body says "no docs needed" with justification
- ⚠️ = issue body has a "Docs disposition" section that says docs are needed but no docs commit found
- ➖ = issue has no docs disposition section (file under "unknown")
- N/A for pure docs issues

## Step 3 — Render the table

Print a markdown table with these exact columns:

```
| # | Issue | Agent | Status | Commit | Deployed | Docs |
|---|-------|-------|--------|--------|----------|------|
| #88 | Coordinator restart-resume | Tank | ✅ d31ec9b | d31ec9b | ✅ | ✅ |
| #97 | Assembly blocked error | Smith+Tank | 🔍 RCA in progress | — | — | ➖ |
| #100 | Graph zoom + navigation | Trinity | 🔄 implementing | — | — | ➖ |
```

**Formatting rules:**
- Truncate issue title to ~45 chars if needed
- `Agent` column: comma-separate squad names (drop "squad:" prefix), capitalize first letter. e.g. `Trinity`, `Smith+Tank`
- `Commit` column: 7-char short SHA if found, `—` if none
- `Deployed` column: ✅ yes, ❌ no (committed but not deployed), `—` unknown
- `Docs` column: ✅ done, ⚠️ needed, ➖ not assessed, N/A

After the table, add a one-line summary:
```
📊 {N} issues — {X} deployed · {Y} in flight · {Z} backlog
```

## Step 4 — Optional filters

If the user asked for a filtered view, apply it:
- "bugs only" → filter `type:bug` issues
- "trinity's issues" → filter `squad:trinity` 
- "what's not deployed" → filter `deployed = false`
- "show closed" → include closed issues from last 30 days

Default (no filter): show all open issues + closed issues from the last 14 days.
