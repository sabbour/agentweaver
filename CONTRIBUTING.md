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
5. **Run the relevant test suite(s) locally and confirm they pass before merging.**
   There is no CI pipeline in this repo wired to run `dotnet test`/`node --test`/`vitest`
   automatically — you are responsible for running and confirming the relevant suite(s)
   yourself.
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

## Questions

Open a [GitHub issue](https://github.com/sabbour/agentweaver/issues) or start a discussion —
we're happy to help you get oriented.
