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
  - `.NET test shard (orchestration)`
  - `.NET test shard (application and authorization)`
  - `.NET test shard (runtime and sandbox)`
  - `.NET test shard (catalog and integrations)`
  - `.NET test shard (PostgreSQL Testcontainers)`
  - `.NET test shard (process-global environment)`
  - `.NET test shard (Kata runtime)`
  - `Node toolchain tests`
  - `Web tests`
  - `Docs build`
    - `Changeset advisory`
  - `Admission findings`

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

## Admission findings required check

This check has one bootstrap sequence; it is not an exception that may recur:

1. Independently review #1490 and its existing CI, then manually squash-merge it with
   `gh pr merge 1490 --squash`. This is permitted only because the trusted workflow does
   not exist on `origin/dev` yet.
2. After `origin/dev` contains the workflow, configure repository variables
   `ADMISSION_FINDINGS_REQUIRED_SOURCES`, `ADMISSION_FINDINGS_AUTHORIZED_REVIEWERS`,
   `ADMISSION_FINDINGS_ADMISSION_OWNERS`, and
   `ADMISSION_FINDINGS_WAIVER_APPROVERS` with independently authorized principals.
   The candidate author must not appear in the source, owner, waiver, or revalidation
   role for that candidate.
3. Add **`Admission findings`** from the `Admission findings` workflow to the active
   `dev-integration-ruleset` required-status-check list, and restrict that ruleset's
   allowed merge methods to `squash`. GitHub rulesets are repository settings and cannot
   be changed safely from a source PR.
4. Open a disposable test PR, provide the current-head reviewer sources and ledger, and
   record a passing `Admission findings` run before ordinary admission resumes.

Observed on 2026-09-21: GitHub returned only direct collaborator `sabbour`; the repository
installation endpoint returned HTTP 401 for this token, and no repository variables were
listed. Ruleset `19284785` currently permits `merge`, `squash`, and `rebase` and does not
require `Admission findings`. No independent authorized identity is established by source
control. Until an administrator adds one and configures the variables above, the workflow
deliberately fails closed. Do not add the required check before step 1 has landed the
trusted workflow on `origin/dev`.
