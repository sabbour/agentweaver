# Agentweaver Deep Dives

These pages explain the **logic and concepts** behind each subsystem — enough to understand *why* it works the way it does and to rebuild it from scratch. Read in order for the full picture, or jump straight to any one.

- [System overview](./00-system-overview.md) — the whole architecture as one mental model: components, AKS topology, the run lifecycle, and core invariants.
- [API core](./api-core.md) — how the REST host is composed: endpoint groups, stores, run/project services, and the request flow.
- [Auth & security](./auth-security.md) — the trust model: API keys, GitHub auth, OAuth/MCP token flow, authorization, and security guardrails.
- [Orchestration](./orchestration.md) — the coordinator's logic: drafting an outcome, planning work, dispatching agents, assembling results, review, and recovery.
- [Team & casting](./team-casting.md) — how a team is cast from blueprints and catalogs: universes, naming, charters, and squad/memory persistence.
- [Agent runtime](./agent-runtime.md) — what happens inside an agent turn: workflow wiring, Copilot/Foundry runners, tools, RAI, Scribe, and events.
- [Sandbox](./sandbox.md) — the isolation model: filesystem containment, command execution, executor selection, and AKS sandbox claims.
- [MCP server](./mcp-server.md) — the MCP boundary: transports, tools, bearer propagation, and OAuth resource-metadata discovery.
- [Frontend](./frontend.md) — the SPA's mental model: routing, API client, the SSE timeline, snapshot+stream state, and static hosting.
- [Projects & workspaces](./projects.md) — the project lifecycle: GitHub repository linking, workspace provisioning, and per-project defaults.
- [Infrastructure](./infra-deployment.md) — the deployment logic: AKS manifests, Gateway routes, PVCs, CSI secrets, network policy, and deploy scripts.
- [Data & persistence](./data-persistence.md) — the storage model: SQLite stores, EF Core MemoryDb, run events, decisions/memory, and state recovery.
