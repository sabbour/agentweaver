# Required `main` ruleset and repository settings

`main` is Agentweaver's stable, published-only branch. It is not the default or
normal integration branch: ordinary feature, bug-fix, documentation, and release
preparation PRs target protected `dev`.

## Ruleset targeting `main`

Create an **active branch ruleset** targeting only `main`:

- Enforcement status: **Active**
- Bypass: repository administrators only, for audited emergencies; require a pull
  request or issue explaining every bypass. Do not grant routine agent, app, or
  maintainer bypass.
- Restrict deletions: **enabled**
- Block force pushes: **enabled**
- Require linear history: **enabled**
- Require a pull request before merging: **enabled**
  - Required approvals: **0 initially** (blocking CI and strict up-to-date
    protection are the mechanical admission gate; raise this to 1 when
    independent human review is required for every change)
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

Only these PRs may enter `main`:

1. A promotion PR from a soaked `release/vX.Y.Z` branch.
2. An audited emergency hotfix PR for a critical production fix. Forward-port an
   emergency fix to `dev` before normal integration resumes (and to any supported
   maintenance branch when applicable).

## Repository merge settings

Under **Settings → General → Pull Requests**:

- Allow squash merging: **on**
- Allow merge commits: **off**
- Allow rebase merging: **off**
- Default squash commit title: **pull request title**
- Automatically delete head branches: **on**

Promotion PRs use **squash merge** so the promoted stable change is one auditable
commit on `main`; tag that exact resulting SHA. A release branch is deleted after
promotion.

## Rollout

Ahmed (repo admin) must enable this ruleset and the repository merge settings through
GitHub Settings or an audited admin API call. Before activation, confirm the four exact
check names on a pull-request run and fix any persistently red or flaky blocking check;
do not create a bypass to normalize red CI.

No live ruleset mutation was made during this migration. The companion
[`dev` ruleset](dev-branch-protection.md) must also be activated manually so the default
integration branch is never an unprotected dumping ground.

## Future organization-owned option

If the repository is transferred to a GitHub organization, GitHub Merge Queue becomes
available for consideration. Public organization repositories can use it on any
organization plan; private organization repositories require GitHub Enterprise Cloud.
Transfer alone does not activate it: evaluate the then-current integration flow before
enabling queue admission.
