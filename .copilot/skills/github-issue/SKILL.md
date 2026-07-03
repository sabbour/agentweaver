---
name: github-issue
description: >
  File a well-structured GitHub issue (bug, feature, chore, docs, spike, or epic) and
  immediately dispatch the right Squad member for triage, RCA, or spec work. Use this
  skill whenever the user reports a bug, requests a new feature, mentions something to
  track, says "record this", "file an issue", "log a bug", "track a feature request",
  "Squad: reporting a bug", or otherwise wants to capture work in the GitHub backlog.
  Trigger even if the user doesn't say "GitHub" or "issue" — if they're describing a
  problem or a capability they want, this skill applies. Also trigger for chores
  (cleanup, refactoring, maintenance) and docs gaps. IMPORTANT: Also trigger when a
  user says "I can't figure out X", "X isn't working", "X doesn't work", "X is broken",
  or describes unexpected behavior — these are latent bug reports even when phrased as
  questions or help requests. After investigating, file the confirmed issue proactively.
---

# GitHub Issue Skill

You are filing a GitHub issue and dispatching the right Squad member to act on it.
This is the team's primary intake mechanism — every bug Ahmed reports, every feature
he requests, every chore he notices flows through here into tracked, routed work.

## Step 1 — Classify

Determine the **type** and **domain** from the user's description:

| Type | When | `type:` label |
|------|------|--------------|
| Bug | Something is broken, behaving wrong, or producing an error | `type:bug` |
| Feature | New capability, UX improvement, new endpoint or page | `type:feature` |
| Chore | Cleanup, refactor, rename, remove dead code, dependency update | `type:chore` |
| Docs | Missing, wrong, or outdated documentation | `type:docs` |
| Spike | Need to investigate before committing to a solution | `type:spike` |
| Epic | A large body of work that decomposes into sub-issues | `type:epic` |

**Domain → primary squad assignee** (use routing.md for edge cases):

| Domain signals | Primary | Secondary |
|----------------|---------|-----------|
| API, backend, database, endpoints, git-worktree, assembly | `squad:tank` | — |
| MAF, run orchestration, tool loop, CTS, recovery, agent-host | `squad:morpheus` | — |
| React UI, Fluent, page, component, frontend, CLI ink | `squad:trinity` | — |
| Test, regression, coverage, playwright, vitest | `squad:smith` | — |
| AKS, k8s, Docker, CI, build, release, infra | `squad:link` | — |
| Docs, security review, prompt injection, sandbox boundary | `squad:seraph` | — |

For **bugs**: always add `squad:smith` as co-assignee (Smith does RCA).
For **multi-domain** bugs: assign primary domain owner + `squad:smith`.
For **features/spikes**: use the domain owner; if scope is unclear, use `squad:smith` for research first.

## Step 2 — Pick labels

Combine: `type:*` + `squad:*` + `go:*` + `priority:*` + `release:*`

**`go:` label** (readiness):
- `go:yes` — clear enough to start immediately (bugs, simple chores, well-spec'd features)
- `go:needs-research` — needs investigation or spec before coding (complex features, spikes, unclear bugs)
- `go:no` — explicitly decided not to pursue (rare — only if user says so)

**`priority:` label** (use Ahmed's signal or default):
- `priority:p0` — blocking release / production outage
- `priority:p1` — this sprint (most bugs)
- `priority:p2` — next sprint (features, chores, docs)
- Omit if Ahmed hasn't specified and it's a backlog item.

**`release:` label**:
- Default to `release:backlog` unless Ahmed targets a specific version.

## Step 3 — Draft the issue body

Use the template for the classified type. Pull every detail from the user's description.
If they provided a run URL, logs snippet, or screenshot filename — include it verbatim.
Don't invent details; leave a `<!-- TODO -->` where info is missing.

### Bug template

```markdown
## Summary
{one-line description of what's broken}

## Run / context
{URL, run ID, or relevant identifier — if provided}

## Steps to reproduce
{numbered steps, or "Reported directly" if not described}

## Expected behavior
{what should happen}

## Actual behavior
{what actually happens — include error message / screenshot reference if provided}

## Technical notes
{any RCA hints, related code areas, or prior context Ahmed mentioned}

## Docs disposition
{Does fixing this bug change any user-visible behavior, API contract, or operator workflow?
- If yes → run `.copilot/skills/docs-feature/SKILL.md` or `.copilot/skills/docs-sync/SKILL.md` after the fix lands
- If no → state why: e.g. "internal fix only — no user-visible behavior change"}

## Reported by
{user} — {date}
```

### Feature template

```markdown
## Summary
{one-line description of the new capability}

## Motivation
{the user problem this solves}

## Proposed solution
{what Ahmed described, or "TBD — needs spec" if open-ended}

## Acceptance criteria
- [ ] {specific, testable criterion}
- [ ] {another criterion}

## Out of scope
{anything explicitly excluded, or "TBD"}

## Docs disposition
{New features always need docs. Which area?
- New UI / workflow → use `.copilot/skills/docs-feature/SKILL.md`
- Existing behavior now works differently → use `.copilot/skills/docs-sync/SKILL.md`
- Pure internal / no operator/user impact → state why docs are not needed}

## Requested by
{user} — {date}
```

### Chore template

```markdown
## Summary
{what needs to change}

## Why
{reason — code smell, performance, maintainability, user request}

## Done when
- [ ] {specific completion criterion}

## Docs disposition
{Does this chore change any behavior visible to operators or users?
- If yes → use `.copilot/skills/docs-sync/SKILL.md` to update affected pages
- If no → state why: e.g. "dead code removal — no behavior change"}

## Requested by
{user} — {date}
```

### Docs template

```markdown
## Summary
{what's missing or wrong}

## Affected area
{page, section, or component}

## What's needed
{description of the correct/missing content}

## Docs disposition
Use `.copilot/skills/docs-feature/SKILL.md` to add new content, or `.copilot/skills/docs-sync/SKILL.md` to correct drifted content.

## Requested by
{user} — {date}
```

## Step 4 — File the issue

```bash
gh issue create \
  --title "{concise title}" \
  --label "{comma-separated labels}" \
  --body "{body from Step 3}"
```

**Title format** — conventional commit style, all lowercase:

```
type(scope): short description of the work
```

| Issue type | Title prefix | Example |
|------------|-------------|---------|
| Bug | `bug(scope):` | `bug(orchestration): api pod restart breaks runs in awaiting_confirmation` |
| Feature | `feat(scope):` | `feat(dashboard): per-model token usage chart with date range picker` |
| Chore | `chore(scope):` | `chore(cluster-page): remove dead AgentPodsTable import` |
| Docs | `docs(scope):` | `docs(architecture): replace flowchart with block diagram` |
| Spike | `spike(scope):` | `spike(testing): persona-driven AKS deployment validation` |
| Epic | `epic(scope):` | `epic(workflows): scheduled and event-driven workflow execution` |

**Scope** is the kebab-case name of the area affected. Derive it from the domain:

| Domain | Typical scope |
|--------|--------------|
| Run orchestration / MAF / coordinator | `orchestration` |
| API endpoints / backend / database | `api` |
| React UI / pages / components | `dashboard`, `cluster-page`, `run-page`, `agent-config`, etc. |
| MCP tools / protocol | `mcp-integrations` |
| Workspace / files / git-worktree | `workspace` |
| Sandbox / AgentHost / Kata | `sandbox` |
| AKS / k8s / infra / CI | `aks`, `ci` |
| Docs | `docs`, `architecture`, `guide` |
| Auth / GitHub OAuth | `auth` |

Use the most specific scope that fits. If the change spans two unrelated areas, file two issues.

**Label string**: comma-separate, no spaces around commas.  
Example: `type:bug,squad:smith,squad:tank,go:yes,priority:p1,release:backlog`

## Step 5 — Dispatch Squad

After filing, output a structured dispatch block so Squad can act immediately:

```
✅ Filed #{number}: {title}
   {issue URL}

📋 Dispatch:
   {squad member} — {what they should do first}
   {secondary member if any} — {their role}

🏷️  Labels: {label list}
```

For **bugs**: Smith leads RCA → reports findings → domain owner implements fix.
For **features** with `go:needs-research`: Smith (or domain owner) produces a spec first.
For **features** with `go:yes`: domain owner picks it up directly.
For **chores/docs**: domain owner picks it up directly.

Squad should then spawn the dispatched member with this issue number as context.

---

## Label reference (quick lookup)

```
Types:    type:bug  type:feature  type:chore  type:docs  type:spike  type:epic
Squad:    squad:tank  squad:morpheus  squad:trinity  squad:smith  squad:link  squad:seraph
Go:       go:yes  go:needs-research  go:no
Priority: priority:p0  priority:p1  priority:p2
Release:  release:backlog  release:v0.x  release:v0.5.0  release:v0.6.0  release:v1.0.0
```
