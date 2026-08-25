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
    - `Changeset advisory`

## Repository merge settings

Under **Settings → General → Pull Requests**:

- Allow squash merging: **on**
- Allow merge commits: **off**
- Allow rebase merging: **off**
- Default squash commit title: **pull request title**
- Automatically delete head branches: **on**

## Activation status

**Active** as of 2026-07-21, ruleset `dev-integration-ruleset` (id `19284785`), applied via
an audited `gh api` call at Ahmed's explicit direction. Repository merge settings
(squash-only, auto-delete head branches) were applied at the same time. `Changeset
advisory` was added to the required checks the same day, once the underlying
`scripts/changesets/check.mjs` check was changed from an advisory-only warning to a
real failure for missing changesets.
