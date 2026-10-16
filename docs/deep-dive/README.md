# Deep dives

These pages explain how the v1 source works across service boundaries. Start with
the system overview, then follow a request through the subsystem you want to
understand. Each page connects a mental model to source files, invariants,
lifecycle steps, and failure behavior.

The [architecture pages](../architecture/overview.md) describe component boundaries.
The [reference](../reference/contracts.md) records contracts and endpoints.
The deep dives explain how those pieces work together.

## System and orchestration

- [System overview](./00-system-overview.md) — Services, shared packages, clients,
  request flow, and state ownership.
- [Gateway](./gateway.md) — Broker authentication, finite owner routing, browser
  integration, and journal-backed event streams.
- [Orchestration](./orchestration.md) — Typed decisions, confirmation gates,
  validated WorkPlans, durable dispatch, and recovery.
- [Events and sessions](./events-sessions.md) — Ordered journal writes, replay,
  session material, addressed messages, and usage accounting.
- [Agent runtime](./agent-runtime.md) — Authenticated bootstrap, native SDK turns,
  tool authorization, durable content, and runtime recovery.
- [MCP server](./mcp-server.md) — Gateway-derived product tools, caller-bound
  requests, and the distinction between acceptance and completion.

## Identity, configuration, and execution resources

- [Identity and secrets](./identity-secrets.md) — Broker identity, purpose-bound
  grants, secret references, and credential redemption.
- [Projects and configuration](./projects-config.md) — Project authority,
  revisioned configuration, skills, and immutable run selections.
- [Environment and sandbox](./environment-sandbox.md) — Environment lifecycle,
  workspace volumes, egress, Sandbox leases, and runtime placement.
- [Providers and models](./providers-models.md) — Provider resolution, capability
  negotiation, resource pins, and model bindings.
- [Source control](./source-control.md) — Repository pins, isolated workspaces,
  output capture, pull requests, and approval-bound merge.

## Data and operations

- [Knowledge and memory](./knowledge-memory.md) — Knowledge records, proposals,
  accepted decisions, context compilation, and Memory providers.
- [Persistence](./persistence.md) — Owned PostgreSQL schemas, Object Store bytes,
  transactional outboxes, inboxes, and retry boundaries.
- [Telemetry](./telemetry.md) — OpenTelemetry composition, signals, exporters, and
  the separation between diagnostics and durable evidence.
- [Infrastructure and deployment](./infra-deployment.md) — Azure and Kubernetes
  source, workload configuration, images, and release boundaries.
- [Testing strategy](./testing-strategy.md) — Pure checks, service tests,
  PostgreSQL integration, harnesses, and acceptance evidence.
