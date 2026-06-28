# Agentweaver Deep Dives

These pages explain the **logic and concepts** behind each subsystem — enough to understand *why* it works the way it does and to rebuild it from scratch. Read in order for the full picture, or jump straight to any one. They're grouped by theme below.

### Foundations

- [System overview](./00-system-overview.md) — the whole architecture as one mental model: components, AKS topology, the run lifecycle, and core invariants.
- [API core](./api-core.md) — how the REST host is composed: endpoint groups, stores, run/project services, and the request flow.
- [Auth & security](./auth-security.md) — the trust model: API keys, GitHub auth, OAuth/MCP token flow, authorization, and security guardrails.

### Orchestration & agents

- [Orchestration](./orchestration.md) — the high-level run pipeline: coordinator, workflows, runs, backlog heartbeat, review/merge, and recovery.
- [Coordinator internals](./coordinator-internals.md) — a deeper look inside the coordinator: OutcomeSpec drafting, WorkPlan decomposition, child dispatch, assembly, and recovery.
- [Workflow engine](./workflow-engine.md) — how workflows are defined, triggered, generated, selected, and bound: templates, role slots, and trigger evaluation.
- [Team & casting](./team-casting.md) — how a team is cast from blueprints and catalogs: universes, naming, charters, and squad/memory persistence.
- [Agent runtime](./agent-runtime.md) — what happens inside an agent turn: workflow wiring, Copilot/Foundry runners, tools, RAI, Scribe, and events.
- [Review & merge](./review-merge.md) — the human-oversight model: review gates, approve/request-changes, reviewer lockout, and how merge happens.

### Execution & integration

- [Sandbox](./sandbox.md) — the isolation model: filesystem containment, command execution, executor selection, and AKS sandbox claims.
- [Git integration](./git-integration.md) — per-run git worktrees and the branch → commit → review → merge lifecycle, plus GitHub API usage.
- [MCP server](./mcp-server.md) — the MCP boundary: transports, tools, bearer propagation, and OAuth resource-metadata discovery.
- [Projects & workspaces](./projects.md) — the project lifecycle: GitHub repository linking, workspace provisioning, and per-project defaults.

### Data & platform

- [Data & persistence](./data-persistence.md) — the storage model: SQLite stores, EF Core MemoryDb, run events, decisions/memory, and state recovery.
- [Memory & decisions](./memory-decisions.md) — the shared-ledger governance model: decision inbox, promotion, slug de-collision, and memory import/export.
- [Events & observability](./events-observability.md) — the event-sourced run model: durable events, SSE streaming, snapshot+stream replay, diagnostics, and metrics.
- [Frontend](./frontend.md) — the SPA's mental model: routing, API client, the SSE timeline, snapshot+stream state, and static hosting.
- [Infrastructure](./infra-deployment.md) — the deployment logic: AKS manifests, Gateway routes, PVCs, CSI secrets, network policy, and deploy scripts.
- [Testing strategy](./testing-strategy.md) — how the system is verified: test layers, WebApplicationFactory hosting, fakes/fixtures, and sandbox/security testing.
