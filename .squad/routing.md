# Work Routing

Routing rules derived from `specs/001-single-agent-run/spec.md`.

## Role to Cast Name

Routing targets below use role IDs from `squad.config.ts`. Resolve to the cast
member when dispatching (see `.squad/casting/registry.json`):

| Role ID | Cast Name |
|---------|-----------|
| backend-engineer | Tank |
| runtime-engineer | Morpheus |
| frontend-engineer | Trinity |
| qa-engineer | Smith |
| platform-engineer | Link |
| security-reviewer | Seraph |

## Keyword and Pattern Routing

- `/\bapi|endpoint|route|sse|stream\b/i` -> `backend-engineer`
- `/\breact|fluent|component|web|frontend|ui\b/i` -> `frontend-engineer`
- `/\bink|cli|terminal|tui\b/i` -> `frontend-engineer`
- `/\bagent framework|loop|orchestr|tool call|sandbox|worktree\b/i` -> `runtime-engineer`
- `/\bpath traversal|symlink|artifact directory\b/i` -> `runtime-engineer`
- `/\bsecurity|red.?team|prompt inject|jailbreak|threat model|pii|secret|credential|sandbox breach|privilege|content.?safety|governance bypass\b/i` -> `security-reviewer`
- `/\btest|vitest|playwright|coverage|contract|qa\b/i` -> `qa-engineer`
- `/\bci|cd|workflow|pipeline|container|platform\b/i` -> `platform-engineer`
- `/\bdocument|decision|changelog|history\b/i` -> `scribe`
- `/\bmemory|context|recall|previous session\b/i` -> `ralph`

## Priority Rules

1. Security, sandbox-path issues, and threat modeling route to `security-reviewer` first; implementation fixes then go to the owning agent.
2. API contract mismatches route to `backend-engineer`, with `qa-engineer` for validation.
3. Client parity issues across Web/CLI route to `frontend-engineer`, with backend collaboration when server contracts change.
4. Test failures without clear ownership route to `qa-engineer` for triage.

## Inferred Patterns (from 001-single-agent-run task routing)

- Git worktree/merge work under `apps/api/**` routes to `backend-engineer`
  (owns `apps/api/src/git`, has `git-worktree-lifecycle` capability), even though
  the bare `worktree` keyword maps to `runtime-engineer`. Sandbox path security
  inside `packages/sandbox-fs` and the agent loop stays with `runtime-engineer`.
- Monorepo setup, toolchain, dependency pinning, lint/format, and CI runner
  wiring route to `platform-engineer`.
- Test-runner *configuration* (Vitest/Playwright/contract harness) routes to
  `qa-engineer`, consistent with the `test|vitest|playwright` keyword rule.
- Documentation-only tasks (`docs/**`, README, decisions) route to `scribe`.
  When `scribe` is not an active agent, documentation tasks fall back to
  `platform-engineer` (owns `developer-experience` / onboarding / reproducible-commands).
