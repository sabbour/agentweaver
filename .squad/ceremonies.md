# Ceremonies

> Team meetings that happen before or after work. Each squad configures their own.

## Design Review

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | before |
| **Condition** | multi-agent task involving 2+ agents modifying shared systems |
| **Facilitator** | lead |
| **Participants** | all-relevant |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. Review the task and requirements
2. Agree on interfaces and contracts between components
3. Identify risks and edge cases
4. Assign action items

---

## Retrospective

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | after |
| **Condition** | build failure, test failure, or reviewer rejection |
| **Facilitator** | lead |
| **Participants** | all-involved |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. What happened? (facts only)
2. Root cause analysis
3. What should change?
4. Action items for next iteration


---

## Retrospective with Enforcement

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | weekly |
| **Condition** | No *retrospective* log in .squad/log/ within the last 7 days |
| **Facilitator** | lead |
| **Participants** | all |
| **Time budget** | focused |
| **Enabled** | yes |
| **Enforcement skill** | retro-enforcement |

**Agenda:**
1. What shipped this week? (closed issues, merged PRs)
2. What did not ship? (open issues, blockers)
3. Root cause on any failures
4. Action items -- each MUST become a GitHub Issue labeled retro-action

**Coordinator integration:**
At round start, call Test-RetroOverdue (see skill retro-enforcement). If overdue, run this ceremony before the work queue.

**Why GitHub Issues, not markdown:**
Production data: 0% completion across 6 retros using markdown checklists, 100% after switching to GitHub Issues.

---

## Pre-Implementation Review

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | before |
| **Condition** | any implementation task (code, feature, or system change) |
| **Facilitator** | morpheus |
| **Participants** | seraph + rubber-duck |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. Rubber-duck: review the proposed architecture and design decisions for soundness — flag anything that would lead the implementation in the wrong direction
2. Seraph: security review of the proposed design — identify threat vectors, sandbox boundary risks, governance gaps, and prompt injection surfaces before any code is written
3. Record findings in the decisions inbox
4. Block: if rubber-duck or Seraph returns a blocking finding, implementation MUST NOT start until it is resolved

**Coordinator integration:**
Before spawning any implementation agent, run this ceremony. Do not spawn the implementation agent until both reviews complete without a blocking finding.

---

## Post-Implementation Review

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | after |
| **Condition** | any implementation task completes |
| **Facilitator** | morpheus |
| **Participants** | code-review + seraph |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. Code-review: review the implemented code against the spec and team standards — flag bugs, logic errors, spec coverage gaps, dead code, and naming issues; architecture soundness is in scope but style is not
2. Seraph: security review of the implemented code — audit sandbox enforcement, event log hygiene, governance bypass paths, and any new attack surface introduced
3. Record findings in the decisions inbox
4. Block: if code-review or Seraph returns a blocking finding, the task is NOT done — the implementing agent (or a different agent if Reviewer Rejection Lockout applies) must address all findings before the task closes

---

## Docs

| Field | Value |
|-------|-------|
| **Trigger** | auto |
| **When** | after |
| **Condition** | any implementation task completes (runs alongside Post-Implementation Review) |
| **Facilitator** | trinity |
| **Participants** | trinity |
| **Time budget** | focused |
| **Enabled** | ✅ yes |

**Agenda:**
1. For every user-facing feature shipped, add or update the corresponding docs site page(s) under `docs/`
2. API changes: update `docs/reference/api.md`
3. CLI changes: update `docs/reference/cli.md`
4. Web UI changes: update `docs/reference/web.md`
5. New concepts or architecture: add or update `docs/guide/` or `docs/architecture/`
6. Verify the docs site builds clean (`npm run docs:build` from repo root)
7. Block: docs must build and reflect the current shipped state — no stubs, no "coming soon", no references to removed code
