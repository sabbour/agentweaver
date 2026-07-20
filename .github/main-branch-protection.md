# Required `main` ruleset and repository settings

Agentweaver uses protected trunk-based GitHub Flow. The repository is owned by
the personal account `sabbour`; GitHub Merge Queue is unavailable for
personal-account repositories, regardless of visibility. This file therefore
defines the enforceable near-term fallback. Workflow YAML cannot enable these
GitHub settings by itself.

## Ruleset targeting `main`

Create an **active branch ruleset** targeting only the default branch:

- Enforcement status: **Active**
- Bypass: repository administrators only, for audited emergencies; require a
  pull request or issue explaining every bypass. Do not grant routine agent,
  app, or maintainer bypass.
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

Strict up-to-date protection is less efficient than a real merge queue: when
one PR merges, every competing PR becomes stale and must update/retest before
it can merge. That churn is the only enforceable serialization mechanism
available while the repository remains personal-account-owned.

## Repository merge settings

Under **Settings → General → Pull Requests**:

- Allow squash merging: **on**
- Allow merge commits: **off**
- Allow rebase merging: **off**
- Default squash commit title: **pull request title**
- Automatically delete head branches: **on**

## Rollout

Ahmed (repo admin) must enable the ruleset and repository settings through
GitHub Settings or an audited admin API call. Before activation, confirm the
four exact check names on a pull-request run and fix any persistently red or
flaky blocking check; do not create a bypass to normalize red CI.

The authenticated CLI identity was verified to have repository admin
permission during this design pass, but no live ruleset mutation was made:
activation changes repository-wide merge behavior and should be an explicit
owner rollout after the pending process/CI changes land.

## Future organization-owned option

If the repository is transferred to a GitHub organization, revisit GitHub
Merge Queue. Public organization repositories can use it on any organization
plan; private organization repositories require GitHub Enterprise Cloud.
At that point, add the CI `merge_group` trigger, require Merge Queue, start
with merge groups of one, and disable the redundant “branch must be up to
date” rule. This is a future option, not a current action item.
