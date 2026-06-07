<!--
Sync Impact Report
==================
Version change: (template, unratified) -> 1.0.0
Bump rationale: Initial ratification. The constitution moves from an unfilled
template to a concrete document defining seven governing principles. A first
adoption of normative principles is a MAJOR baseline (1.0.0).

Principles defined (template slot -> concrete principle):
- PRINCIPLE_1 -> I. Agent Runtime (Microsoft Agent Framework)
- PRINCIPLE_2 -> II. Model Sources (Copilot SDK or Microsoft Foundry)
- PRINCIPLE_3 -> III. API-First
- PRINCIPLE_4 -> IV. Two Front-Ends at Parity
- PRINCIPLE_5 -> V. Observable Runs
- (added)     -> VI. Deployment Parity (Local and Cloud)
- (added)     -> VII. No Emojis

Sections:
- Added: Architecture & Technology Constraints (SECTION_2)
- Added: Development Workflow & Quality Gates (SECTION_3)
- Added: Governance

Templates requiring updates:
- .specify/templates/plan-template.md ............ OK (generic Constitution
  Check gate; no constitution-specific edits required)
- .specify/templates/spec-template.md ............ OK (no conflicts)
- .specify/templates/tasks-template.md ........... OK (Principle VII scopes to
  the product, not Spec Kit tooling; template emojis left intact)
- .specify/templates/checklist-template.md ....... OK (no conflicts)

Follow-up TODOs: none. RATIFICATION_DATE set to first adoption date.
-->

# Scaffolder Constitution

## Core Principles

### I. Agent Runtime

Every agent MUST be built on the Microsoft Agent Framework. An agent MUST
operate as an agent loop: evaluate the prompt, call tools, receive results,
and repeat until the task is complete. No alternative agent runtime or ad hoc
control flow may replace this loop.

Rationale: A single, shared runtime keeps agent behavior consistent,
inspectable, and composable across the system.

### II. Model Sources

A run's model MUST come from exactly one of two providers: the GitHub Copilot
SDK (the Copilot SDK specifically, NOT GitHub Models) or Microsoft Foundry.
The provider MUST be selectable per run. No other model source is permitted.

Rationale: Constraining model sources to two well-defined providers keeps
authentication, billing, and capability assumptions tractable and auditable.

### III. API-First

The backend API is the single source of truth. The agent loop, tasks, and the
stream of run steps MUST all be exposed through the API. Every user interface
is a thin client over that API and MUST contain no business logic of its own.

Rationale: A single authoritative API guarantees that all clients behave
identically and that logic lives in exactly one place.

### IV. Two Front-Ends at Parity

There MUST be exactly two clients over the API: a CLI (TUI) and a Web UI. The
Web UI MUST be built with React 19 and Fluent 2. For this phase, both clients
MUST be able to do everything the API allows.

Rationale: Two equally capable clients prove the API is complete and prevent
business logic from leaking into any single front-end.

### V. Observable Runs

A run MUST stream its steps: the agent's messages, the tool calls it makes,
and their results. Any client MUST be able to watch those steps live.

Rationale: Live, streamed observability is required to trust, debug, and
demonstrate agent behavior as it happens.

### VI. Deployment Parity (Local and Cloud)

Scaffolder MUST be designed to run on a developer machine first, and the same
build MUST also be able to run as a hosted cloud service. Neither environment
may be treated as a special case bolted on after the fact.

Rationale: A single build that runs identically locally and in the cloud
prevents environment drift and keeps the developer inner loop honest.

### VII. No Emojis

Emojis MUST NOT appear anywhere in the product Scaffolder builds and ships:
not in its code, UI, output, logs, generated docs, or commit messages. This
rule governs the application; it does not constrain the development tooling
(for example, Spec Kit templates and command files).

Rationale: A strict no-emoji rule across the product keeps every shipped
surface consistent, accessible, and free of rendering and encoding ambiguity.

## Architecture & Technology Constraints

- The Microsoft Agent Framework is the mandated agent runtime (Principle I).
- Model providers are limited to the GitHub Copilot SDK or Microsoft Foundry,
  selectable per run (Principle II).
- The backend API is authoritative; clients hold no business logic
  (Principle III).
- The two clients are a CLI (TUI) and a Web UI built with React 19 and
  Fluent 2 (Principle IV).
- Run steps (agent messages, tool calls, tool results) MUST be streamable to
  any client (Principle V).
- The same build MUST support both local-developer and hosted-cloud execution
  (Principle VI).

## Development Workflow & Quality Gates

- Every change MUST be evaluated against all seven principles before merge.
  Any deviation MUST be recorded in the plan's Complexity Tracking with an
  explicit justification, or the change MUST be revised to comply.
- New capability exposed to users MUST be added to the API first, then made
  reachable from both the CLI and the Web UI (Principles III and IV).
- Run-step streaming MUST be preserved for any change that affects how runs
  execute (Principle V).
- Changes MUST be verified to run on a developer machine and to remain
  compatible with hosted-cloud execution (Principle VI).
- Reviews MUST reject any emoji introduced into the product's code, UI,
  output, logs, generated docs, or commit messages (Principle VII). This does
  not apply to development tooling such as Spec Kit templates.

## Governance

This constitution supersedes all other practices. When any guidance conflicts
with this document, this document wins.

Amendments MUST be proposed as a written change to this file, MUST include a
rationale, and MUST update the version and dates below. Dependent templates
and runtime guidance MUST be reviewed and brought into alignment as part of
the same change.

Versioning follows semantic versioning:
- MAJOR: Backward-incompatible governance or principle removals or
  redefinitions.
- MINOR: A new principle or section is added, or guidance is materially
  expanded.
- PATCH: Clarifications, wording, or non-semantic refinements.

Compliance is reviewed at every pull request and plan gate. Reviewers MUST
verify that changes satisfy all seven principles; unavoidable complexity MUST
be justified in the plan's Complexity Tracking section.

**Version**: 1.0.0 | **Ratified**: 2026-06-07 | **Last Amended**: 2026-06-07
