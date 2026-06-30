# Work Routing

How the Coordinator picks **who** handles a work request. Routing signals are
**derived from each member's charter** (`## Capabilities` + `## Responsibilities`)
— the charter is the single source of truth. This file only adds the
disambiguation and priority layer on top; it does not restate every keyword.

> When a charter's capabilities change, routing changes with it — do **not**
> maintain a parallel keyword list here that can drift out of sync.

## Role to Cast Name

Routing targets use role IDs. Resolve to the cast member when dispatching
(see `.squad/casting/registry.json` / `.squad/team.md`):

| Role ID | Cast Name |
|---------|-----------|
| backend-engineer | Tank |
| runtime-engineer | Morpheus |
| frontend-engineer | Trinity |
| qa-engineer | Smith |
| platform-engineer | Link |
| security-reviewer | Seraph |

## Domain Ownership (charter-derived)

Match the request against the owning member's **charter capabilities**. The
representative capabilities below are a pointer into each charter, not a
duplicate authority — read the charter for the full, current list.

| Role | Owns (from charter capabilities) | Charter |
|------|----------------------------------|---------|
| backend-engineer | backend-api, sse-streaming, openapi-contracts, sqlite-persistence, git-worktree-lifecycle, dotnet10-platform | `.squad/agents/tank/charter.md` |
| runtime-engineer | microsoft-agent-framework, tool-loop-orchestration, sandbox-path-security, provider-adapters, run-state-machine, governance-policy-enforcement | `.squad/agents/morpheus/charter.md` |
| frontend-engineer | react-19-fluent2, ink-cli-ui, api-client-integration, live-step-rendering, ux-flow-implementation | `.squad/agents/trinity/charter.md` |
| qa-engineer | vitest, playwright, contract-testing, regression-design, edge-case-validation | `.squad/agents/smith/charter.md` |
| platform-engineer | monorepo-tooling, github-actions, cross-platform, release-workflow, dotnet10-build, developer-experience | `.squad/agents/link/charter.md` |
| security-reviewer | prompt-injection/jailbreak review, sandbox boundary enforcement, LLM-output trust, agentic-loop security, secret/PII hygiene, model-source validation, threat modeling | `.squad/agents/seraph/charter.md` |

## Priority & Disambiguation Rules

These resolve overlaps where more than one charter could match:

1. **Security first.** Security, sandbox-path/traversal, prompt-injection, and
   threat-modeling concerns route to `security-reviewer` for review; the
   implementation fix then goes to the owning charter.
2. **Git worktree/merge under `apps/api/**`** routes to `backend-engineer`
   (owns the worktree lifecycle there) even though the bare `worktree` signal
   also appears in the runtime charter. Sandbox path security inside the agent
   loop stays with `runtime-engineer`.
3. **API contract mismatches** route to `backend-engineer`, with `qa-engineer`
   for contract/parity validation.
4. **Client parity issues** across Web/CLI route to `frontend-engineer`, with
   backend collaboration when server contracts change.
5. **Test failures without clear ownership** route to `qa-engineer` for triage.
6. **Monorepo setup, toolchain, dependency pinning, lint/format, CI runner
   wiring** route to `platform-engineer`.

## Built-in Agents (not work-assignment targets)

These members are **not** routed work by domain/keyword — they activate by
lifecycle or explicit trigger. Do not list them in Domain Ownership above.

| Agent | Activation | Role |
|-------|-----------|------|
| Scribe | Background, after every work batch; owns memory, decisions merge, session/orchestration logs | `.squad/agents/scribe/charter.md` |
| Ralph | Work monitor — runs the scan->act->rescan queue loop on "Ralph, go" / keep-working | `.squad/agents/ralph/charter.md` |
| Rai | RAI reviewer — background by default; blocking only on a critical (red) finding; auto pre-ship / pre-merge | `.squad/agents/Rai/charter.md` |

> Memory, context recall, and "what happened last session" are **Scribe's**
> domain (session logs + decisions), surfaced by the Coordinator's catch-up —
> they are not a Ralph work-routing signal.
