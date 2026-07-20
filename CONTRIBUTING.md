# Contributing to Agentweaver

Thanks for considering a contribution! This doc is the **contribution process** guide — how
to make a change and get it merged. It has two parts:

- **Dev environment & reference** (setup, repo layout, testing, code style) — the quick
  version lives here, but the **canonical, in-depth environment setup guide is
  [Getting started](https://sabbour.me/agentweaver/guide/getting-started)** (prerequisites,
  per-platform installs, local run, OAuth config). This doc does not duplicate it — it points
  to it and adds only the repo conventions a contributor needs.
- **Contribution workflow** ([Making a change](#making-a-change), [Continuous
  integration](#continuous-integration), [Opening a pull request](#opening-a-pull-request),
  [Commit messages](#commit-messages), [AI agent contributions](#ai-agent-contributions)) —
  how a change actually lands. For the release/versioning process, see
  [RELEASING.md](RELEASING.md).

## Prerequisites

See [Prerequisites in the getting started guide](https://sabbour.me/agentweaver/guide/getting-started#prerequisites)
for the tools you'll need (git, Node.js 22+, .NET 10 SDK, and Azure CLI if you're touching
the deploy/upgrade scripts) and per-platform install instructions (winget/brew/apt-get).

## Getting set up

```bash
git clone https://github.com/sabbour/agentweaver.git
cd agentweaver
npm run setup   # checks/prepares local prerequisites (git, dotnet, node)
npm run dev     # starts the API (http://localhost:5000) and Web UI (http://localhost:5173)
```

See the [Getting started guide](https://sabbour.me/agentweaver/guide/getting-started) for the
full walkthrough, including configuring a GitHub OAuth App for local sign-in (the callback
URL for local dev is `http://localhost:5000/auth/github/callback` — the API's own origin,
no `/api` prefix, since that endpoint is mapped at the root, not under `/api`).

## Repository layout

| Path | What it is |
|---|---|
| `apps/Agentweaver.Api` | The .NET API (coordinator, runs, memory, auth, sandbox orchestration) |
| `apps/Agentweaver.Mcp` | The MCP server surface |
| `apps/web` | The React/Vite frontend |
| `docs/` | VitePress documentation site (published at sabbour.me/agentweaver) |
| `packages/` | Shared .NET libraries (agent runtime, squad model, etc.) |
| `scripts/azure` | The Node.js build/deploy/upgrade/release toolchain (no bash/PowerShell) |
| `tests/Agentweaver.Tests` | .NET test suite |
| `tests/e2e` | End-to-end tests |
| `k8s/` | Kubernetes manifests (AKS deployment) |

## Making a change

1. **Use a short-lived branch and PR for every change.** `dev` is the default,
   protected integration branch. Branch from current `origin/dev` with a descriptive
   conventional prefix, e.g. `fix/123-short-desc`, `feat/short-desc`, or
   `docs/short-desc`.
   - **Direct pushes to `dev` are not allowed**, including tiny and docs-only changes.
   - Before merge, the branch must be current with `dev` and all blocking CI must rerun
     successfully. GitHub enforces this through “require branches to be up to date
     before merging.”
   - **Squash-merge** so `dev` keeps **one commit per logical change**. GitHub
     automatically deletes the source branch after merge.
   - `main` is stable/published-only. Do not open ordinary PRs into it; it receives a
     soaked release promotion or an audited emergency hotfix only.
   - Do **not** create a long-lived local `integration`/`staging` branch as a private
     promotion pipeline. A disposable merge-test branch or worktree is fine, but delete
     it after validation.
2. **Keep changes focused.** Scope one change to one concern — it's easier to review and
   to revert if something goes wrong.
3. **Add or update tests** for any behavior change (see Testing below).
4. **Update docs** if you changed user-facing behavior, npm scripts, or configuration —
   `README.md` and `docs/guide/` are the two places most likely to need updates.
5. **Run the relevant test suite(s) locally before you push.** CI re-runs the full suite
   on every pull request and push to `dev` or `main`, but running the affected suite
   locally first keeps the feedback loop short and avoids red PRs.
6. **Verify live for anything with runtime/deploy impact**, not just via unit tests.

## Branch Topology

The active topology is `dev → release/vX.Y.Z → main`:

- **`dev`** is the default, protected integration branch. Normal PRs target it and use
  required PRs, blocking CI, up-to-date-before-merge, squash merge, and automatic source
  branch deletion.
- **`release/vX.Y.Z`** is an ephemeral release-candidate/soak branch cut from a green
  `dev` SHA. Stabilization fixes land there by PR and are immediately forward-ported to
  `dev`.
- **`main`** is stable/published-only. It receives only a promotion PR from a soaked
  release branch or an audited emergency hotfix, which must be forward-ported to `dev`.
  Release tags are cut from the exact resulting `main` promotion SHA.

This topology was deliberately activated on 2026-07-20 for room to grow; it was not the
result of the prior automatic metrics threshold. The complete operating flow is in
[RELEASING.md](RELEASING.md).

Two further-growth triggers remain forward-looking and un-fired:

- **Trigger A — Merge Queue:** when the repository is organization-owned and either at
  least **5 PRs in a rolling 14-day period** rerun blocking CI solely because another PR
  merged first, or median ready-to-merge time exceeds **one business day for two
  consecutive weeks** because of update/retest serialization. Add `merge_group` CI and
  enable Merge Queue for protected integration admission.
- **Trigger B — protected maintenance branch:** when the project explicitly commits to
  ship a fix for an older minor after an incompatible newer minor has landed. Create and
  protect `release/X.Y` from the last supported tag; publish patch tags from it and
  forward-port applicable fixes to `dev`.

For the retained rationale and original activation plan, see
[Niobe's branching growth review](.squad/decisions/inbox/niobe-branching-growth-review.md).

## Testing

Run only the suite(s) relevant to what you changed:

```bash
# .NET API / packages (requires -p:CopilotSkipCliDownload=true on ARM64/Windows
# dev machines to avoid the Copilot SDK trying to download a CLI binary)
dotnet test tests/Agentweaver.Tests/Agentweaver.Tests.csproj -p:CopilotSkipCliDownload=true

# Node.js build/deploy/upgrade/release toolchain
node --test scripts/azure/tests/*.test.mjs

# Web frontend (Vitest)
npm --prefix apps/web run test

# Web frontend lint
npm --prefix apps/web run lint

# Docs site build (only if you changed docs/)
npm run docs:build
```

## Continuous integration

Pull requests and pushes to `dev` and `main` are verified by the
[`CI` workflow](.github/workflows/ci.yml). It runs the same commands documented under
[Testing](#testing) above, split into one job per area so each gets a dedicated runner
(several .NET and web tests are timing-sensitive and flake under CPU contention if
crowded onto a single runner):

| Job | What it runs | Gating |
|---|---|---|
| `.NET tests` | `dotnet test … -p:CopilotSkipCliDownload=true` | Blocking — must pass |
| `Node toolchain tests` | `node --test scripts/azure/tests/*.test.mjs` | Blocking — must pass |
| `Web tests` | `npm --prefix apps/web run test` | Blocking — must pass |
| `Web lint` | `npm --prefix apps/web run lint` | **Advisory** — visible but non-blocking |
| `Docs build` | `npm run docs:build` | Blocking — must pass |

The `Web lint` job is currently **advisory**: a lint failure marks that one job red so the
finding stays visible on the PR, but (via job-level `continue-on-error`) it does not fail
the overall run or block merge. The `eslint-plugin-react-hooks` v7 upgrade introduced
stricter React-Compiler-style rules that surface a pre-existing backlog of violations in
`apps/web`; running lint in its own job keeps those findings visible, and the plan is to
flip it to blocking once the backlog is cleared. Until then, **do not add new lint
violations** — run `npm --prefix apps/web run lint` locally and keep your own changes clean.

The repository policy requires these four blocking jobs on a branch that is
up to date with `dev`. Ahmed must activate the GitHub ruleset described in
[`.github/dev-branch-protection.md`](.github/dev-branch-protection.md) to
make admission mechanical. Until activation, follow the same PR and strict
update/retest policy manually and never direct-push or merge around a failing
blocking job.

GitHub Merge Queue is unavailable while this repository is owned by the
personal `sabbour` account. Strict up-to-date protection causes more
update/retest churn when concurrent PRs race, but it is the enforceable
fallback. Revisit Merge Queue only if the repository moves to an organization.

## Opening a pull request

- **Keep the PR scoped to one concern** and give it a conventional-commit-style title
  (see [Commit messages](#commit-messages)) — the title is what shows up in the generated
  changelog and the GitHub Release notes.
- **Describe what changed and why**, and how you verified it (which suite(s) you ran, and
  any live/deploy verification for runtime changes).
- **Make sure the blocking CI jobs are green** and that you have not introduced new lint
  findings before asking for review.
- **Update, retest, then squash-merge.** If another PR reaches `dev` first,
  GitHub marks yours out of date. Update from `origin/dev`, resolve conflicts,
  rerun relevant tests/CI, and merge only after all required checks are green
  on the updated branch.

### Contributing from a fork

Fork the repository on GitHub, clone **your fork**, add the canonical repository as
the `upstream` remote, and create your short-lived branch from an up-to-date
`upstream/dev`. Open the PR from that branch to `dev`; it follows the same CI,
up-to-date, review, and squash-merge rules as every other contribution.

Fork PRs do not receive repository secrets: CI uses the `pull_request` trigger (not
`pull_request_target`) and its jobs do not use `secrets.*`. `CODEOWNERS` and a required
approval for non-owner PRs are **not active today**. On the first real external fork
PR, audit the fork workflow again, then add/activate those controls as a checklist
item; do not assume they already exist.

## Labels

[`.github/labels.json`](.github/labels.json) is the canonical taxonomy for new and
relabeled issues. Use one `type:*`, `priority:*`, `go:*`, and `release:*` label where
applicable; use `squad:{member}` for ownership and an optional `area:*` label for product
scope. `sync-squad-labels.yml` reads that manifest for static labels and generates squad
member labels from the roster. The legacy `bug`, `enhancement`, and `workstream:*` labels
are deprecated in favor of `type:bug`, `type:feature`, and the smaller `area:*` vocabulary;
existing issues are not being mass-relabelled.

## Commit messages

`CHANGELOG.md` is generated from commit history, bucketed by prefix. Please use a
conventional-commit-style prefix:

- `feat: ...` — new functionality
- `fix: ...` — bug fixes
- `docs: ...` — documentation-only changes
- `chore: ...` / `refactor: ...` — internal changes with no user-facing behavior change
- `test: ...` — test-only changes

## Code style

- **.NET**: follow the existing conventions in the file/module you're editing. Don't
  introduce a new formatting style into an existing file.
- **Node.js (`scripts/azure/`)**: ESM (`.mjs`), no bash/PowerShell — this toolchain is
  intentionally 100% cross-platform Node.js (it fully replaced the earlier
  bash/PowerShell scripts). Read the module header comment at the top of the relevant
  `scripts/azure/*.mjs` file before changing behavior — several non-obvious
  ordering/timing decisions are documented there specifically to avoid reintroducing
  past bugs.
- **Web**: TypeScript + React, FluentUI components. Run `npm --prefix apps/web run lint`
  before submitting.

## Where NOT to make changes

- Do not hand-edit `CHANGELOG.md` — it's generated (`python scripts/gen-changelog.py`).
- Do not add build/deploy logic outside `scripts/azure/` — bash/PowerShell scripts were
  fully removed in favor of the Node.js toolchain; don't reintroduce a second toolchain.
- Do not commit secrets (API keys, GitHub OAuth client secrets, connection strings) —
  `appsettings.Development.json` is git-ignored for local secrets; use .NET user-secrets
  or environment variables instead.
- Do not weaken auth/security checks (or otherwise take shortcuts) just to make a test
  or a manual verification pass — fix the real blocker instead.

## AI agent contributions

Some contributions to this repo are made by AI agents rather than people. This project is
developed with **Squad**, a team of named agents (Trinity, Tank, Morpheus, Smith, Link,
Seraph, Scribe, Ralph, Rai, and others), and can optionally route work to GitHub's
`@copilot` coding agent when it is on the roster. This section documents how that
agent-driven flow works. It does **not** replace the human workflow above — human
contributors follow the same branch → up-to-date PR → squash-merge path in
[Making a change](#making-a-change) and can skip this section.

**Issue-driven lifecycle.** Agent work is anchored to a GitHub issue and follows
issue -> branch -> PR -> review -> merge. The label-based automation in
[`.github/workflows/`](.github/workflows/) drives it:

- `sync-squad-labels.yml` keeps the `squad:{member}` labels in sync with the roster in
  `.squad/team.md`.
- `squad-triage.yml` reacts to the `squad` label: the Lead agent routes the issue to a
  member (or to `@copilot` when it is a good fit), applies the `squad:{member}` label, and
  adds a default `go:needs-research` verdict.
- `squad-issue-assign.yml` reacts to a `squad:{member}` label by acknowledging the
  assignment (and, for `squad:copilot`, handing the issue to the `@copilot` coding agent).
- `squad-label-enforce.yml` enforces mutual exclusivity within the `go:`, `type:`,
  `priority:`, and `release:` label namespaces.

Feature and bug issue templates add the `squad` label by default, so the triage workflow
routes them when filed. For issues filed outside those templates, add `squad` manually
to request Squad routing. Triage is a lightweight operating norm rather than a hard SLA:
handle P0 reports the same business day and route other new Squad issues within a few
business days.

The assigned agent branches as `squad/{issue-number}-{slug}`, commits with a
conventional-commit message that references the issue (`Closes #{number}`, including the
`Co-authored-by: Copilot` trailer), pushes, and opens a PR with `gh pr create` against
`dev`. The full lifecycle, spawn context, and merge commands live in
[`.squad/templates/issue-lifecycle.md`](.squad/templates/issue-lifecycle.md); the
orchestration rules live in [`.github/agents/squad.agent.md`](.github/agents/squad.agent.md).
Agent PRs are gated by the same [CI](#continuous-integration) as everyone else's.

**Branches vs. worktrees.** A **locally run** Squad agent (including a Copilot CLI agent)
must use one dedicated git worktree per issue under [`.worktrees/`](.worktrees/), reusing it
when collaborating on that issue. This prevents concurrent local agents from sharing a
working tree or index. A **hosted** agent (such as GitHub's `@copilot` coding agent) uses the
platform's isolated branch and environment instead — no local worktree applies. **Human
contributors** may use a worktree as a convenience, but a plain short-lived branch in the
main checkout is supported. The creation, reuse, dependency, team-root, and cleanup
mechanics live in [`.squad/templates/worktree-reference.md`](.squad/templates/worktree-reference.md);
do not duplicate them here.

**New feature workflow.** Proposing a new feature or capability (agent or human):

1. **Open a GitHub issue first** — no un-tracked feature work. Describe the user
   story/problem it solves.
2. **Add or update a spec under [`specs/`](specs/README.md)** before or alongside the code.
   Specs are area-grouped, **one file per user story**, each linking its GitHub issue
   number — follow the existing files' format exactly (title; `**Issue:**` + `**Area:**`
   header; `## User story`, `## Context / problem`, `## Scope` (In/Out), `## Acceptance
   criteria`, `## Notable edge cases`), and add the story to the matching area section of
   [`specs/README.md`](specs/README.md).
3. **Then follow the normal issue → branch → PR → review → merge lifecycle** above,
   including updating user-facing docs (`docs/guide/`, `README.md`) in the same change as
   required by the **Documenting your work** guidance below.

New user-facing functionality that lands **without** a corresponding `specs/` entry should
be **flagged in review**. This is a **convention, not an enforced gate**: the
[`docs-drift.yml`](.github/workflows/docs-drift.yml) nudge watches API/workflow/blueprint/
MCP code paths against `docs/**` only — it does **not** cover `specs/`, so nothing
mechanically blocks a spec-less feature PR. Reviewers are responsible for catching it.

**Bug-fix workflow.** Fixing a bug (agent or human):

1. **Open (or reuse) a GitHub issue** describing the bug: repro steps and expected vs.
   actual behavior. Don't file untracked fixes for anything beyond a trivial/obvious
   one-liner (typo, broken link, obviously-wrong constant) — anything with behavioral
   nuance or a risk of regression gets an issue.
2. **Reference the issue in the commit/PR** with `Closes #N` (see [Commit
   messages](#commit-messages)) so it auto-closes on merge.
3. **Include a regression test** that fails before the fix and passes after, whenever the
   bug is in code with a test suite — this is the existing "[add or update tests for any
   behavior change](#making-a-change)" rule applied to fixes, and it is what QA
   (Smith's charter) means by preventing regressions. A fix with no test should say why one
   isn't feasible.
4. **After merge**, the same lifecycle applies: CI-gated, and the issue closes
   automatically via `Closes #N` (or close it manually if the fix only partially addresses
   the issue).

**Peer review and the reviewer-rejection protocol.** **Changes requested** is ordinary
review feedback: the original author may revise the same PR normally, with no lockout.
Lockout occurs only when a Reviewer (Tester, Code Reviewer, Lead, or Rai for Responsible AI)
explicitly declares **Rejected / independent rewrite required** — for example, with the
exact PR comment marker `REJECTED — requires independent rewrite`. Then the original author
is **locked out** of the next revision, a different agent must produce it, and the Reviewer
chooses whether to reassign or escalate. The Coordinator enforces that rule mechanically.
The rejection marker must remain on the PR so the author rotation is auditable on GitHub
without Coordinator session history; a `status:locked-out` PR label may additionally be
used when the repository creates it. The full rules are in the "Reviewer Rejection Protocol"
section of `squad.agent.md`.

**Rubber-ducking.** Before a non-trivial or risky change ships, the Coordinator can invoke a
`rubber-duck` review pass — a dedicated critical-feedback agent whose only job is to hunt for
bugs, logic errors, and design flaws before anything is committed. It is invoked at the
Coordinator's discretion for higher-risk work, not automatically on every change.

**Auditable decisions.** Meaningful design decisions are recorded to the decisions inbox
(`.squad/decisions/inbox/`); Scribe periodically merges routine operational decisions into
the canonical `.squad/decisions.md`. Cross-cutting architecture or technical decisions that
should survive that ledger's compaction are promoted to numbered
[ADRs](docs/architecture/decisions/README.md). This keeps agent-driven changes traceable back
to the reasoning behind them.

**Documenting your work.** Docs are part of the definition of done, not a follow-up. When a
change affects user-facing behavior — npm scripts, CLI flags, setup/deploy steps, API routes
or config, the OAuth flow — update the relevant docs in the *same* change: the VitePress
guide under [`docs/guide/`](docs/guide/) (the source of truth for
https://sabbour.me/agentweaver, built with `npm run docs:build` / `docs:dev` /
`docs:preview`) and/or `README.md` (quick overview and links out). `CONTRIBUTING.md` and
`RELEASING.md` are the process docs; add inline code comments only where they genuinely
clarify intent (this repo's style avoids over-commenting). Some reference pages under
`docs/reference/` are generated by `node scripts/gen-docs.mjs` — regenerate and commit them,
never hand-edit. The [`docs-drift.yml`](.github/workflows/docs-drift.yml) workflow backs this
up: it **hard-fails** a PR when a committed generated reference (e.g.
`docs/reference/mcp-tools.md`) is stale, and posts a **non-blocking** reminder when code in
doc-relevant paths (API endpoints, workflows, blueprints, MCP tools) changes without any
`docs/**` update.

Do **not** hand-edit `CHANGELOG.md` — it is derived from git history by
`scripts/gen-changelog.py` (bucketed by conventional-commit prefix), so a well-formed commit
message is what feeds it; the GitHub Release notes are generated separately from merged PRs at
release time (see [Releasing](RELEASING.md)). Finally, keep decision records and docs
distinct: a `.squad/decisions/inbox/` entry captures *why* a choice was made (an internal
audit trail) and never substitutes for updating `docs/guide/` / `README.md`, which tell users
*how* to use the feature.

## Questions

Open a [GitHub issue](https://github.com/sabbour/agentweaver/issues) or start a discussion —
we're happy to help you get oriented.
