# System Overview — Conceptual Deep Dive

## Purpose and Mental Model

Agentweaver is a self-hosted control system for AI-assisted software work. Its core job is to turn an intent such as "make this change" or "coordinate a team on this outcome" into a controlled sequence of repository operations: isolate the work, let an agent act, record what happened, evaluate the result, ask a human before irreversible changes, merge only when approved, and preserve reusable learning.

The easiest way to understand the system is to separate three concerns:

1. **Intent plane** — humans, the web UI, and MCP clients describe goals, inspect progress, answer questions, and approve or reject outcomes.
2. **Control plane** — the API owns durable state, workflow orchestration, permissions, events, review gates, recovery, merge coordination, and memory.
3. **Execution plane** — agent runtimes, model providers, git worktrees, and sandboxes do the actual work under policies chosen by the control plane.

This separation is deliberate. Models are useful but non-deterministic, so Agentweaver keeps authority in deterministic services: persistent stores define truth, review gates define who may approve, merge locks define when repository history changes, and sandbox policy defines what tools may touch.

![Purpose and Mental Model: Human operator / reviewer, MCP client, Web UI, Agentweaver API, Agent runtime, Sandboxed tools, Git repo, Durable events, Decisions + memory, Model providers](../diagrams/00-system-overview-fig1.png)

<!-- Rendered from ../diagrams/src/00-system-overview-fig1.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

The platform is not just a chat wrapper around an agent. It is closer to a CI/CD-style orchestrator for agentic changes: every run has identity, state, events, a workspace, a review boundary, and a terminal result.

## Architectural Responsibilities

### Thin clients, thick control plane

The web UI and MCP server are intentionally thin. They adapt user interactions into API calls, render state, and stream events, but they do not decide run state, workflow progression, or merge behavior. This keeps all clients consistent: approving a review through the browser and approving through an MCP tool should affect the same durable gate in the same way.

The API is the authority because it can combine information that no client should own alone: project configuration, run status, reviewer identity, workflow checkpoints, persistent events, worktree paths, merge locks, memory, and recovery jobs. If a process restarts, the API can reconstruct enough state to continue or safely expose a fallback decision path.

### Isolation before execution

Agentweaver assumes generated changes are untrusted until reviewed. A run therefore works in an isolated git worktree instead of directly on the target branch. The model sees tools that are scoped to that worktree, and command execution passes through a sandbox executor. This gives the system a clean unit of work: the diff between the original branch state and the worktree result.

This design has several advantages:

- The original branch remains untouched while the agent explores and edits.
- The review artifact is concrete: a diff, tree hash, and branch/worktree output.
- Failed or declined work can be discarded without contaminating the main workspace.
- Merge conflicts become explicit workflow states rather than hidden side effects.

The trade-off is operational complexity. Worktrees, locks, sandboxes, cleanup, and recovery all have to be managed. Agentweaver accepts that complexity because it is the price of making model-written repository changes auditable and reversible.

### Durable events plus live fan-out

A run is both a state machine and a story. Operators need the live story while it is happening, and recovery needs the durable story after restarts. Agentweaver therefore treats events as write-through: first persist the event, then fan it out to live subscribers.

The invariant is that the durable event log is the source of truth. The in-memory stream is a same-replica optimization; cross-replica watchers read from the shared `RunEvents` table by `Last-Event-ID` cursor. Source: `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:15`, `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs:77`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:423`, `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:429`.

```mermaid
%%{init: {'theme':'base','themeVariables':{'fontFamily':'Segoe UI, system-ui, -apple-system, sans-serif','fontSize':'15px','primaryColor':'#E8EEF9','primaryBorderColor':'#0F6CBD','primaryTextColor':'#242424','lineColor':'#605E5C','clusterBkg':'#FAF9F8','clusterBorder':'#D2D0CE','edgeLabelBackground':'#FFFFFF'}}}%%
sequenceDiagram
    participant Step as Workflow step
    participant Store as Shared RunEvents table
    participant Web as Any web replica
    participant UI as UI / MCP client

    Step->>Store: append event with ordered sequence
    Store-->>Step: persisted
    UI->>Web: SSE with Last-Event-ID cursor
    Web->>Store: read Sequence > cursor
    Store-->>Web: ordered events
    Web-->>UI: SSE frames
```

### Human gates protect irreversible actions

Agentweaver distinguishes between reversible agent work and irreversible repository changes. Editing inside a worktree is reversible. Merging into the target branch is not. The workflow therefore stops at a human review gate before merge.

The review gate is not just a UI screen. Conceptually it is a resumable workflow port with a single pending decision. A valid reviewer can approve, request changes, or decline. The workflow consumes that decision exactly once and then moves forward. This prevents duplicated approvals, stale decisions, and accidental merges after restarts.

### Team memory closes the loop

Each run can teach the system something: a pattern, a decision, a constraint, a session summary, or a caution for future agents. Agentweaver stores those facts in a structured memory database and exports selected views into project-local `.squad` and context files. Future prompts can then include concise, project-specific knowledge instead of relying on model memory or chat history.

The important distinction is between **draft knowledge** and **accepted knowledge**. Agents can submit candidate decisions into an inbox. A Scribe or coordinator can later promote, reject, or export them. That gives the system a learning loop without letting every transient model observation become permanent project policy.

Where this lives: `apps/Agentweaver.Api`, `apps/Agentweaver.Mcp`, `apps/web`, `packages/Agentweaver.AgentRuntime`, `packages/Agentweaver.AgentTools`, `packages/Agentweaver.SandboxFs`, `packages/Agentweaver.SandboxExec`, `packages/Agentweaver.Squad`.

## Major Subsystems and How They Fit

| Subsystem | Problem it solves | Design logic |
| --- | --- | --- |
| API host | Centralizes orchestration, authorization, persistence, streaming, projects, memory, review, and merge behavior. | Keep authoritative state in one backend boundary so every client observes the same run lifecycle. |
| Web UI | Lets humans start work, watch progress, manage projects/teams, and review outcomes. | Keep presentation separate from orchestration; the UI renders backend truth rather than inventing its own workflow. |
| MCP host | Exposes Agentweaver capabilities to assistants and developer tools. | Make the same backend available to agentic clients without duplicating business logic. |
| Agent runtime | Converts workflow steps into model turns and governed tool calls. | Encapsulate model-provider mechanics behind a workflow interface so orchestration can reason in steps, gates, and outputs. |
| Agent tools | Provide file, search, edit, patch, shell, reporting, and escalation functions. | Give agents useful capabilities while keeping each capability policy-checkable. |
| Sandbox filesystem and execution | Prevent tool calls from escaping the intended workspace or running with unintended authority. | Layer policy checks: tool allowlists, path containment, symlink/reparse protection, and executor isolation. |
| Git/worktree services | Isolate changes and merge them safely. | Treat a run's output as a branchable, reviewable artifact with explicit merge coordination. |
| Team/casting engine | Creates and persists named agents, charters, blueprints, and team context. | Make roles explicit so multi-agent work is reproducible rather than ad hoc. |
| Memory and decisions | Stores reusable knowledge, current session context, and decision records. | Separate transient run output from durable project intelligence. |
| AKS deployment | Runs the platform with ingress, persistent storage, secrets, and isolated sandbox pods. | Split public services from execution sandboxes and externalize credentials/storage through cloud-native primitives. |

A rebuild should preserve the boundaries more than the exact classes. The crucial pattern is that external clients never directly operate on worktrees, memory files, or model sessions. They request intent changes from the API, and the API coordinates deterministic services around probabilistic model work.

## Single-Agent Run Lifecycle

A single-agent run is the smallest complete unit of Agentweaver work. It starts with a task and ends in one of a few terminal outcomes: merged, declined, failed, content-safety flagged, no changes, or similar terminal states.

![Single-Agent Run Lifecycle: Submit task + project + branch + options, Validate request and canonicalize repository, Create isolated worktree and run branch, Build prompt context, Agent turn uses governed tools, Commit worktree changes and compute diff, Responsible AI review, RAI result, Human review gate, Reviewer decision, Merge under repository lock, Scribe updates memory/session/export, …](../diagrams/00-system-overview-fig2.png)

<!-- Rendered from ../diagrams/src/00-system-overview-fig2.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

### What each stage is for

1. **Submission and validation** make the task explicit and bind it to a repository, branch, project, requester, model source, and run options. This is the moment an ambiguous user intent becomes a durable run.
2. **Worktree creation** creates a private editing surface. From this point forward, agent file changes are isolated from the branch being protected.
3. **Context construction** gives the model only the operating instructions it needs: the task, optional named-agent charter, relevant memory, sandbox policy, and workflow expectations.
4. **Agent execution** lets the model inspect, edit, run commands, and ask questions through governed tools. The model does not get raw host authority; it gets mediated capabilities.
5. **Commit and diff production** freeze the agent's output into a reviewable artifact. A diff is easier to review, test, merge, and audit than a stream of individual edits.
6. **RAI review** acts as an automated quality and safety checkpoint. It can pass, require revision, or stop the run if the output is unacceptable.
7. **Human review** preserves human authority over repository changes. Even a passing RAI result does not merge by itself.
8. **Merge coordination** serializes writes to the target repository and turns conflicts into workflow outcomes instead of race conditions.
9. **Scribe** captures durable lessons after the outcome is known. It is best-effort because memory updates should enrich the system, not invalidate a completed merge.

### Key invariants

- A run should have one durable identity and one ordered event stream.
- Agent edits should happen inside the run workspace, not directly on the protected branch.
- The produced diff should be reviewed before merge.
- Review decisions should be consumed at most once.
- Merges should be serialized per repository.
- Memory/Scribe failures should not retroactively fail an otherwise completed run.

Where this lives: `apps/Agentweaver.Api/Runs`, `apps/Agentweaver.Api/Endpoints`, `apps/Agentweaver.Api/Workflows`, `packages/Agentweaver.AgentRuntime/Workflow`.

## Coordinator Run Lifecycle

A coordinator run exists for work that is too broad for one linear agent pass. It adds planning, dependency management, parallel child execution in isolated child worktrees, and collective assembly.

The key idea is to move from a vague goal to a confirmed contract before agents start editing. The coordinator first drafts an **OutcomeSpec**: desired outcome, scope, assumptions, and clarifying questions. A human can revise or confirm that spec. Only after confirmation does the system decompose work into a **WorkPlan**: subtasks, dependencies, assigned agents, isolation hints, and assembly strategy.

![Coordinator Run Lifecycle: Human goal or ready backlog item, Draft OutcomeSpec, Human confirms?, Revise spec, Create WorkPlan DAG, Find ready dependency frontier, Dispatch child runs in parallel, Observe child terminal states, All usable outputs ready?, Build integration branch, Review aggregate diff, One human review, …](../diagrams/00-system-overview-fig3.png)

<!-- Rendered from ../diagrams/src/00-system-overview-fig3.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

### Why coordinator children do not each merge

Child runs are workers, not final approvers. They produce candidate branches and diffs, then stop at assemble-ready. They intentionally skip per-child RAI, human review, merge, and Scribe. The coordinator then assembles all child outputs into one integration branch and asks for one review of the whole change.

That design prevents a common multi-agent failure mode: independently "correct" child changes that conflict or form an incoherent whole. By reviewing and merging once, Agentweaver treats the user-visible outcome as the unit of approval.

### Dependency frontier model

The WorkPlan is a DAG. A subtask can run when all of its dependencies are done and its declared scope does not conflict with other subtasks being launched in the same frontier. This allows safe parallelism without pretending every task is independent.

The frontier model gives three useful properties:

- **Deterministic recovery** — after a restart, the system can recompute which subtasks are pending, running, failed, or ready for assembly.
- **Bounded parallelism** — only dependency-ready, non-conflicting work launches together.
- **Targeted rework** — if review requests changes, the coordinator can reset affected subtasks instead of discarding the entire plan.

### Coordinator invariants

- No child work starts until the OutcomeSpec is confirmed or unattended policy explicitly allows confirmation.
- A WorkPlan should be persisted before dispatch so recovery can resume from durable intent.
- Child runs should finish in assembly-ready states, not merge independently.
- Collective assembly should run RAI and human review on the aggregate diff.
- The coordinator should produce at most one final merge for the plan.

Where this lives: `apps/Agentweaver.Api/Coordinator`, `apps/Agentweaver.Api/Memory`, `apps/Agentweaver.Api/Runs`, `packages/Agentweaver.Domain`.

## Workflow Model

Agentweaver represents work as workflows rather than hard-coded endpoint scripts. Conceptually, a workflow is a graph of named nodes: agent turns, RAI checks, human gates, merge steps, Scribe steps, and terminals. Edges define how outputs move through the graph.

The default full workflow is intentionally conservative:

![Workflow Model: Agent, RAI, Terminal: safety blocked, Scribe, Human review, Terminal: declined, Merge, Terminal: done](../diagrams/00-system-overview-fig4.png)

<!-- Rendered from ../diagrams/src/00-system-overview-fig4.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

The workflow abstraction matters because it gives project authors and future features a vocabulary for changing process without rewriting orchestration primitives. However, Agentweaver does not blindly execute arbitrary graph nodes. Runtime binding classifies nodes by supported type and gate semantics, then maps them to known executors. Unsupported nodes fail closed. That preserves extensibility without allowing a malformed workflow to bypass review, RAI, or merge policy.

Trade-off: workflow graphs add indirection. The payoff is that single-agent runs, coordinator child runs, and future project-authored workflows can share the same execution concepts while choosing different pipelines. For example, coordinator child runs use a trimmed agent-only pipeline because RAI, review, and merge happen later at collective assembly.

Where this lives: `apps/Agentweaver.Api/Workflows`, `apps/Agentweaver.Api/Runs`, `packages/Agentweaver.AgentRuntime/Workflow`.

## Data, State, and Recovery

Agentweaver stores several kinds of state because each answers a different recovery question.

| State kind | Question it answers | Conceptual owner |
| --- | --- | --- |
| Run rows | What work exists, who requested it, where is its workspace, and what is its current status? | Run store |
| Run events | What happened, in what order, and what should clients replay? | Event stream |
| Workflow checkpoints | If a workflow paused or the process restarted, where can execution resume? | Workflow runtime / API |
| Request ports / review gates | Is the system waiting for a human decision, and who may provide it? | Workflow gate services |
| Memory and decisions | What project knowledge should survive beyond this run? | Memory database |
| Coordinator specs/plans/subtasks | What was promised, how was it decomposed, and which work remains? | Coordinator persistence |
| Project/workspace records | Which repositories and defaults are known to the system? | Project services |

The recovery strategy follows from the separation of live and durable state. Live streams, in-memory workflow registrations, and active process handles are useful while the service is running, but the durable records must be sufficient to avoid lying to users after a restart. When the process comes back, recovery services can inspect run statuses, pending gates, child run outcomes, and persisted plans to decide whether to resume, expose a fallback action, or mark a run failed.

The most important invariant is monotonicity: once a durable event or state transition is recorded, clients should not observe a contradictory story later. Recovery may add compensating events, but it should not pretend earlier events never happened.

## Memory and Decision Flywheel

Agentweaver's memory system is a structured feedback loop:

![Memory and Decision Flywheel: Run produces observations, Agent memory tools, Decision inbox, Memory DB, Coordinator / Scribe / human policy, Accepted decisions, Project context export, Future run prompt context](../diagrams/00-system-overview-fig5.png)

<!-- Rendered from ../diagrams/src/00-system-overview-fig5.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

This loop separates three categories of knowledge:

- **Session context** — what is currently being worked on and what matters right now.
- **Agent memory** — reusable observations, patterns, and learnings scoped to an agent or shared through tags.
- **Decisions** — durable architectural, process, scope, or technical choices that should constrain future work.

The inbox is the safety valve. Agents may propose decisions, but promotion is explicit. This avoids memory pollution while still capturing important discoveries before they disappear from the run transcript.

Exports make memory portable. Instead of burying all context in a database, Agentweaver regenerates human-readable project artifacts such as decisions, pending inbox entries, agent history, current session context, and boundary/pattern files. A rebuild should preserve this bidirectional shape: structured database for correctness and queryability; file exports for transparency, review, and prompt context.

Where this lives: `apps/Agentweaver.Api/Memory`, `packages/Agentweaver.Squad/Memory`, `.squad`, `.agentweaver/context`.

## Sandbox and Tool Governance

Agentweaver treats every model tool call as a request, not a right. The governance stack is layered so a single missed check is less likely to become a workspace escape.

![Sandbox and Tool Governance: Model requests tool call, Registered Agentweaver tool, Governance policy, Tool-specific backend checks, Path containment, Sandbox executor gate, Run worktree, Denied](../diagrams/00-system-overview-fig6.png)

<!-- Rendered from ../diagrams/src/00-system-overview-fig6.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

Key concepts:

- **Default deny**: if a tool or operation is not recognized, it should not run.
- **Capability-specific validation**: file reads, searches, edits, patches, and shell commands need different checks.
- **Path containment**: paths must resolve inside the intended workspace, including protection against symlinks, junctions, and time-of-check/time-of-use tricks.
- **Executor isolation**: shell commands should run in a real sandbox unless the operator explicitly selected direct local execution.
- **AKS sandbox claims**: in Kubernetes, runs claim isolated sandbox capacity rather than executing commands inside the API container.

The trade-off is that some legitimate commands may need extra configuration or policy support. Agentweaver prefers that friction over silent privilege expansion.

Where this lives: `packages/Agentweaver.AgentTools`, `packages/Agentweaver.SandboxFs`, `packages/Agentweaver.SandboxExec`, `apps/Agentweaver.Api/Sandbox`, `k8s/base/sandbox-*`.

## Model Providers and Agent Roles

Agentweaver distinguishes between **workflow orchestration** and **model execution**. Workflow orchestration decides when an agent turn should happen, what context it receives, which tools are available, and what to do with its output. Model execution is the provider-specific mechanism for producing that turn.

The production worker path centers on a Copilot-backed workflow agent that can persist
session state, use registered tools, and stream progress. One-shot execution uses the
same GitHub Copilot SDK runner. Custom providers are configured through that SDK rather
than a separate provider-specific runner.

Named agents add another layer above providers. A role such as reviewer, planner, or specialist is defined by charter, memory, and assignment. The same model provider can behave differently depending on that role context. This is why casting and charters are first-class: they make team behavior reproducible.

## AKS Runtime Topology

In AKS, Agentweaver separates public services, persistent state, secrets, and sandbox execution.

![AKS Runtime Topology: Users and MCP clients, Gateway API, /api /auth OAuth routes, /mcp and MCP metadata, / frontend, API service, MCP service, Frontend service, API pod, MCP pod, Frontend pods, Data PVC, …](../diagrams/00-system-overview-fig7.png)

<!-- Rendered from ../diagrams/src/00-system-overview-fig7.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

### Why the topology looks this way

- **Gateway routing** gives one public HTTPS entry point while keeping API, MCP, and frontend as independently deployable services.
- **A single API writer** matches the default SQLite persistence model. Horizontal API scaling would require a database and locking strategy designed for multiple writers.
- **Separate data and workspace volumes** distinguish application state from repository working files. They have different access patterns and backup concerns.
- **Key Vault CSI** keeps secrets out of images and manifests while making them available to pods at runtime.
- **Warm sandbox capacity** reduces run startup latency while preserving per-run isolation.
- **Network policy** should start from deny-by-default and then open only DNS, ingress, app-internal, GitHub/provider, and MCP-to-API paths required for operation.

This deployment is optimized for correctness and isolation before scale-out. The most obvious scaling pressure points are API single-writer persistence, workspace PVC throughput, sandbox pool capacity, and model-provider rate limits.

Where this lives: `k8s`, `scripts/azure`, `apps/Agentweaver.AgentHost`.

## Tech Stack Rationale

| Layer | Technology | Why it fits Agentweaver |
| --- | --- | --- |
| Backend services | .NET / ASP.NET Core | Strong fit for long-lived services, dependency injection, streaming endpoints, hosted recovery jobs, and typed domain models. |
| Workflow runtime | Microsoft Agents / MAF-style workflows | Provides a resumable graph model for agent turns, request ports, gates, and streaming execution. |
| Model providers | GitHub Copilot SDK | Supports GitHub-native agent work and custom-provider configuration through one governed runtime. |
| Persistence | SQLite plus EF Core-backed memory store options | SQLite keeps self-hosted/local deployment simple; the memory layer is structured enough to evolve toward server databases. |
| Git operations | LibGit2Sharp-style repository APIs | Enables programmatic worktree, branch, diff, and merge operations without shelling out for every repository action. |
| Web UI | React, TypeScript, Vite, Fluent UI | Good fit for a live operational UI with review forms, timelines, project screens, and reusable Microsoft-style components. |
| MCP | Model Context Protocol over stdio/HTTP | Lets external assistants use Agentweaver as a tool surface while reusing API authorization and orchestration. |
| Sandbox governance | Agent Governance Toolkit plus custom policy backend | Combines a default-deny policy engine with Agentweaver-specific file and command semantics. |
| Sandbox execution | Local executors and Kubernetes sandbox claims | Supports developer machines and production AKS without changing the conceptual run contract. |
| AKS ingress and secrets | Gateway API, workload identity, Key Vault CSI | Uses cloud-native primitives for routing and secret delivery rather than embedding deployment-specific secrets in code. |
| Observability | Durable events, diagnostics endpoints, OTLP | Makes the run timeline both user-visible and operator-debuggable. |

The common theme is pragmatic layering. Agentweaver uses simple local-first primitives where they reduce setup cost, then wraps them in boundaries that can be replaced when scale or deployment requirements grow.

## Glossary

| Term | Meaning |
| --- | --- |
| Agentweaver | The whole platform: API, web UI, MCP host, runtime, tools, sandboxing, memory, and deployment assets. |
| Run | A durable unit of agent work tied to a task, repository, branch, workspace, status, events, and output artifact. |
| Single-agent run | A direct run that uses the full pipeline: worktree, agent, RAI, human review, merge, and Scribe. |
| Coordinator run | A parent run that turns a goal into a confirmed OutcomeSpec, WorkPlan, child runs, assembly, review, merge, and Scribe. |
| OutcomeSpec | The human-confirmed contract for a coordinator run: desired outcome, scope, assumptions, and clarification state. |
| WorkPlan | The persisted DAG of coordinator subtasks, dependencies, assignments, isolation hints, and assembly status. |
| Subtask | One node in a WorkPlan, assigned to an agent and eventually represented by a child run. |
| DAG / frontier | The dependency graph and the set of currently runnable subtasks whose prerequisites are complete. |
| AssembleReady | The child-run terminal state meaning the child output is ready for collective coordinator assembly, not independently merged. |
| Collective assembly | The coordinator phase that integrates child outputs, reviews the aggregate diff, asks for one human decision, and merges once. |
| Worktree | An isolated git working directory for a run's changes. It protects the target branch until review and merge. |
| Sandbox | The execution boundary for model tools, including file containment and command isolation. |
| Blueprint | A reusable project/team template that can define roster, workflow, review, and sandbox expectations. |
| Casting | The process of selecting and persisting named agents, roles, charters, and team context for a project. |
| Charter | Role-specific instructions for a named agent, injected into that agent's run context. |
| MCP | Model Context Protocol surface that lets external assistants call Agentweaver capabilities. |
| RAI | Responsible AI review step that evaluates produced diffs and can pass, request revision, or block. |
| Scribe | Best-effort post-run memory keeper that updates session context, promotes or records learnings, and exports memory artifacts. |
| Decision inbox | Holding area for proposed decisions before they become accepted project memory. |
| Review gate | Human-in-the-loop workflow pause where an authorized reviewer approves, requests changes, or declines. |
| Memory export | Regeneration of human-readable project context files from structured memory and decision records. |

## Known limitations and scope

- Agentweaver ships a default embedded workflow and loads additional catalog and project workflows separately. The workflow model and the default pipeline are documented here; individual embedded catalog workflow resources are defined alongside their projects.
- The control plane is a single authoritative backend even though AKS deploys API, MCP, and frontend as separate processes. API and run orchestration remain the single source of truth; MCP and frontend are thin client-facing processes that render and forward backend state.
