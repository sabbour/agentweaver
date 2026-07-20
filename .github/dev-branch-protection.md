# Required `dev` ruleset and repository settings

`dev` is Agentweaver's default, protected integration branch. All normal feature,
bug-fix, documentation, and release-preparation PRs target `dev`; direct pushes are
not allowed.

## Ruleset targeting `dev`

Create an **active branch ruleset** targeting only `dev`:

- Enforcement status: **Active**
- Bypass: no routine admin bypass. Repository administrators may bypass only for an
  audited emergency, with a pull request or issue explaining the bypass.
- Restrict deletions: **enabled**
- Block force pushes: **enabled**
- Require linear history: **enabled**
- Require a pull request before merging: **enabled**
  - Required approvals: **0 initially** (raise this to 1 when independent human
    review is required for every change)
  - Dismiss stale approvals: not applicable while approvals are 0
  - Require conversation resolution: **enabled**
- Require status checks: **enabled**
  - Require branches to be up to date before merging: **enabled**
  - Required checks, with the `CI` workflow as source:
    - `.NET tests`
    - `Node toolchain tests`
    - `Web tests`
    - `Docs build`
  - Do **not** require `Web lint`; it remains advisory until its documented
    backlog is cleared.

## Repository merge settings

Under **Settings → General → Pull Requests**:

- Allow squash merging: **on**
- Allow merge commits: **off**
- Allow rebase merging: **off**
- Default squash commit title: **pull request title**
- Automatically delete head branches: **on**

## Manual activation required

Ahmed (repo admin) must activate this ruleset in **GitHub Settings → Rules → Rulesets**
and confirm the repository merge settings above. This migration intentionally does **not**
mutate GitHub rulesets through `gh api`; activation is a separate, audited owner action.
Before activation, verify the four exact required check names on a `dev` pull-request run.
