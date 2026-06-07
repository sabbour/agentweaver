# Scaffolder Constitution

## Core Principles

### I. Agent Runtime

Every agent MUST be built on the Microsoft Agent Framework (.NET 10) (https://github.com/microsoft/agent-framework). An agent MUST
operate as an agent loop: evaluate the prompt, call tools, receive results,
and repeat until the task is complete. No alternative agent runtime or ad hoc
control flow may replace this loop.

Before implementing anything, make sure you're not reimplementing core functionality in the Microsoft Agent Framework (https://learn.microsoft.com/en-us/agent-framework/). You should build on it. 

Rationale: A single, shared runtime keeps agent behavior consistent,
inspectable, and composable across the system. Using MAF will help us keep our project lean and manageable.

### II. Model Sources

A run's model MUST come from exactly one of two providers: GitHub Copilot CLI or Microsoft Foundry.

The provider MUST be selectable per run. No other model source is permitted.

Microsoft Agent Framework supports multiple providers. For this project:
- GitHub Copilot: https://docs.github.com/en/copilot/how-tos/copilot-sdk/integrations/microsoft-agent-framework
- Microsoft Foundry: https://learn.microsoft.com/en-us/agent-framework/agents/providers/microsoft-foundry?pivots=programming-language-csharp using the ChatClientAgent

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

### VII. No Mocks, Fakes, or Placeholders

Every implementation MUST be functional from the first commit. Mock responses,
fake LLM calls, fake tool calls, stub implementations, and placeholder logic
are NEVER permitted at any layer — not in the agent loop, not in tools, not in
API handlers, not in clients.

If a real implementation cannot be completed in a given slice, the scope of
that slice MUST be reduced rather than filled with fake behavior. A narrow,
working slice is always preferred over a broad, pretend one.

Rationale: Mock and placeholder code creates churn, false confidence, and
confusion about what is actually working. It obscures real integration problems
that surface late and are expensive to fix.

### VIII. No Emojis

Emojis MUST NOT appear anywhere in the product Scaffolder builds and ships:
not in its code, UI, output, logs, generated docs, or commit messages. This
rule governs the application; it does not constrain the development tooling
(for example, Spec Kit templates and command files).

Rationale: A strict no-emoji rule across the product keeps every shipped
surface consistent, accessible, and free of rendering and encoding ambiguity.

### IX. Responsible AI

The product's use of AI MUST be responsible, transparent, and accountable:

- A named human MUST remain accountable for every run. The system MUST keep a
  person in the loop for consequential or irreversible actions (Principle X).
- AI actions MUST be transparent. Every model message, tool call, and tool
  result MUST be attributable and visible through the run's step stream and
  audit log (Principles V and X).
- The product MUST NOT generate harmful content or content that infringes
  copyright, and MUST apply content-safety checks appropriate to the active
  model provider.
- Privacy MUST be preserved. Secrets and personal data MUST NOT be exposed in
  outputs, logs, or telemetry, and MUST NOT be sent to any party beyond the
  selected model provider.
- Fairness and bias concerns MUST be considered wherever prompts, tools, or
  defaults can influence outcomes.

Rationale: An agent acts on a user's behalf, so responsible, transparent,
privacy-preserving behavior with clear human accountability is the baseline for
trusting it with real work.

### X. Safe Execution

Agent actions MUST execute inside enforced safety boundaries:

- File and process operations MUST stay within the run's designated sandbox
  (its artifact or worktree directory). Any operation that escapes the sandbox
  MUST be rejected, not merely warned about.
- Every run MUST be bounded by explicit limits, at minimum a maximum number of
  steps and a maximum wall-clock duration, and MUST end in a visible terminal
  state.
- Destructive or irreversible actions (for example merging, publishing, or
  deleting outside the sandbox) MUST require explicit human approval before they
  proceed.
- Every action MUST be auditable. The event log MUST record agent messages,
  tool calls, and tool results in order, in enough detail to reconstruct what
  the agent did.

Rationale: Bounded, sandboxed, human-gated, and auditable execution is what
makes it safe to let an agent run at all; without these limits a single run
could cause unbounded or irreversible harm.

### XI. Agent Governance Toolkit (.NET 10)

Governance of the agentic stack MUST be enforced through the Microsoft Agent
Framework (.NET 10) governance capabilities (Principle I), not reimplemented ad hoc
or pushed into client code:

- Policies and guardrails (allowed tools, permitted model sources, sandbox
  boundaries, and approval rules) MUST be enforced by the runtime/governance
  layer so that every agent and client is bound by the same rules.
- Agent behavior MUST be observable through structured telemetry (traces,
  metrics, and logs) emitted by the runtime.
- Agent behavior MUST be as deterministic and reproducible as the underlying
  models allow, and every run MUST be auditable from its recorded events
  (Principles V and IX).

Rationale: Centralizing policy, guardrails, telemetry, and auditability in the
shared .NET 10 governance toolkit keeps enforcement consistent, observable, and
tamper-resistant across every agent and every client, rather than relying on
each surface to police itself.

## Architecture & Technology Constraints

- The Microsoft Agent Framework (.NET 10) (https://github.com/microsoft/agent-framework) is the mandated agent runtime (Principle I).
- Model providers are limited to the GitHub Copilot CLI or Microsoft Foundry,
  selectable per run (Principle II).
- The backend API is authoritative; clients hold no business logic
  (Principle III).
- The two clients are a CLI (TUI) and a Web UI built with React 19 and
  Fluent 2 (Principle IV).
- Run steps (agent messages, tool calls, tool results) MUST be streamable to
  any client (Principle V).
- The same build MUST support both local-developer and hosted-cloud execution
  (Principle VI).
- AI use MUST be responsible, transparent, privacy-preserving, and free of
  harmful or copyright-infringing content, with a human accountable for every
  run (Principle IX).
- Agent actions MUST run inside an enforced sandbox under explicit step and
  time limits, with human approval required for irreversible actions and a
  complete audit trail (Principle X).
- Policy, guardrails, telemetry, and auditability MUST be enforced by the
  Microsoft Agent Framework (.NET 10) governance layer, not by individual clients
  (Principle XI).

## Development Workflow & Quality Gates

- Every change MUST be evaluated against all ten principles before merge.
  Any deviation MUST be recorded in the plan's Complexity Tracking with an
  explicit justification, or the change MUST be revised to comply.
- New capability exposed to users MUST be added to the API first, then made
  reachable from both the CLI and the Web UI (Principles III and IV).
- Run-step streaming MUST be preserved for any change that affects how runs
  execute (Principle V).
- Changes MUST be verified to run on a developer machine and to remain
  compatible with hosted-cloud execution (Principle VI).
- Reviews MUST reject any mock, fake, stub, or placeholder implementation at
  any layer. Scope MUST be reduced before fake behavior is introduced
  (Principle VII).
- Reviews MUST reject any emoji introduced into the product's code, UI,
  output, logs, generated docs, or commit messages (Principle VIII). This does
  not apply to development tooling such as Spec Kit templates.
- Reviews MUST confirm responsible-AI obligations are met: transparency of AI
  actions, privacy of secrets and personal data, no harmful or
  copyright-infringing content, and a human accountable for the run
  (Principle IX).
- Changes that affect how runs execute MUST preserve the sandbox boundary,
  the step and time limits, the human-approval gate for irreversible actions,
  and the audit trail (Principles X and XI).

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
verify that changes satisfy all ten principles; unavoidable complexity MUST
be justified in the plan's Complexity Tracking section.

**Version**: 1.2.0 | **Ratified**: 2026-06-07 | **Last Amended**: 2026-06-07
