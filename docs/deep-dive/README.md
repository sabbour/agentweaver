# Agentweaver Deep Dives

Start here for detailed subsystem documentation. These pages are designed to be read in order, but each stands alone.

- [System overview](./00-system-overview.md) — top-level architecture, component inventory, AKS topology, run lifecycle, stack, and glossary.
- [API core](./api-core.md) — REST API host, endpoint groups, stores, run/project services, and backend composition.
- [Auth & security](./auth-security.md) — API keys, GitHub auth, OAuth/MCP token flow, authorization, and security guardrails.
- [Orchestration](./orchestration.md) — coordinator OutcomeSpec/WorkPlan flow, child dispatch, collective assembly, review, and recovery.
- [Team & casting engine](./team-casting.md) — blueprint/catalog casting, universes, naming, charters, squad persistence, and memory.
- [Agent runtime](./agent-runtime.md) — MAF workflow wiring, Copilot/Foundry runners, tools, RAI, Scribe, and event emission.
- [Sandbox](./sandbox.md) — filesystem containment, command execution, sandbox policy, executor selection, and AKS sandbox claims.
- [MCP server](./mcp-server.md) — MCP transports, tools, bearer propagation, OAuth resource metadata, and API client behavior.
- [Frontend](./frontend.md) — React SPA structure, API client, SSE timeline, coordinator UI, review pages, and static hosting.
- [Projects & workspaces](./projects.md) — project lifecycle, GitHub repository linking, workspace provisioning, and per-project defaults.
- [Infrastructure deployment](./infra-deployment.md) — AKS manifests, Gateway routes, PVCs, CSI secrets, network policy, and deployment scripts.
- [Data persistence](./data-persistence.md) — SQLite stores, EF Core MemoryDb, run events, decisions, memory, sessions, and checkpoint/state recovery.
