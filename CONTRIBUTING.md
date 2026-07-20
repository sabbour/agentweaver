# Contributing to Agentweaver

Thanks for considering a contribution! This doc covers everything you need to get set up,
make a change, and get it merged.

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

1. **Work directly against `main`.** This repo does not use a `dev`/`preview` staging
   branch flow — commits are made (or PRs opened) straight against `main`. Use a
   short-lived branch with a descriptive, conventional prefix for anything you want
   reviewed first, e.g. `fix/123-short-desc`, `feat/short-desc`, `docs/short-desc`.
2. **Keep changes focused.** Scope one change to one concern — it's easier to review and
   to revert if something goes wrong.
3. **Add or update tests** for any behavior change (see Testing below).
4. **Update docs** if you changed user-facing behavior, npm scripts, or configuration —
   `README.md` and `docs/guide/` are the two places most likely to need updates.
5. **Run the relevant test suite(s) locally before you push.** CI (see
   [Continuous integration](#continuous-integration) below) re-runs the full suite on
   every pull request and on pushes to `main`, but running the affected suite locally
   first keeps the feedback loop short and avoids red PRs.
6. **Verify live for anything with runtime/deploy impact**, not just via unit tests —
   e.g. after a change to `scripts/azure/`, run the affected command for real (with
   `--dry-run` where supported) against an actual environment before considering the
   change done. Peer review or a passing unit test alone does not mean a fix is verified
   or deployed — only confirming it live, after a real deploy, does.

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

Pull requests (and pushes to `main`) are automatically verified by the
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

CI is not yet enforced as a required status check via branch protection. Treat a green run
(with, at most, the advisory `Web lint` job red) as the bar for merging, and do not merge a
PR with a failing blocking job.

## Opening a pull request

- **Keep the PR scoped to one concern** and give it a conventional-commit-style title
  (see [Commit messages](#commit-messages)) — the title is what shows up in the generated
  changelog and the GitHub Release notes.
- **Describe what changed and why**, and how you verified it (which suite(s) you ran, and
  any live/deploy verification for runtime changes).
- **Make sure the blocking CI jobs are green** and that you have not introduced new lint
  findings before asking for review or merging.

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
contributors follow [Making a change](#making-a-change) (direct-to-main, PR or direct
commit, CI gating) and can skip this section.

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

The assigned agent branches as `squad/{issue-number}-{slug}`, commits with a
conventional-commit message that references the issue (`Closes #{number}`, including the
`Co-authored-by: Copilot` trailer), pushes, and opens a PR with `gh pr create` against
`main`. The full lifecycle, spawn context, and merge commands live in
[`.squad/templates/issue-lifecycle.md`](.squad/templates/issue-lifecycle.md); the
orchestration rules live in [`.github/agents/squad.agent.md`](.github/agents/squad.agent.md).
Agent PRs are gated by the same [CI](#continuous-integration) as everyone else's.

**Peer review and the reviewer-rejection protocol.** When an agent with a Reviewer role
(Tester, Code Reviewer, Lead, or Rai for Responsible AI) rejects another agent's work, the
original author is **locked out** of revising that artifact — a *different* agent must
produce the next version, and the Reviewer chooses whether to reassign it or escalate to a
newly spawned specialist. The Coordinator enforces this mechanically. The full rules are in
the "Reviewer Rejection Protocol" section of `squad.agent.md`.

**Rubber-ducking.** Before a non-trivial or risky change ships, the Coordinator can invoke a
`rubber-duck` review pass — a dedicated critical-feedback agent whose only job is to hunt for
bugs, logic errors, and design flaws before anything is committed. It is invoked at the
Coordinator's discretion for higher-risk work, not automatically on every change.

**Auditable decisions.** Meaningful design decisions are recorded to the decisions inbox
(`.squad/decisions/inbox/`); Scribe periodically merges those into the canonical
`.squad/decisions.md`. This keeps agent-driven changes traceable back to the reasoning behind
them.

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
