# Deep dives

Use these articles to understand how Agentweaver v1 works across service
boundaries. Start with the system overview, then follow the subsystem that
interests you. The pages describe checked-in behavior; they do not imply that a
planned feature or a deployment is available.

The [architecture pages](../architecture/overview.md) explain component
boundaries. The [reference](../reference/contracts.md) records contracts and
endpoints. These deep dives connect those boundaries to request flow, state, and
failure handling.

## System and orchestration

- [System overview](./00-system-overview.md) — Trace requests, identify state
  owners, and distinguish history from execution state.
- [Gateway](./gateway.md) — Follow authentication, allowlisted routing, browser
  access, and journal-backed event streams.
- [Orchestration](./orchestration.md) — Understand decision gates, validated
  WorkPlans, explicit dispatch, and recovery.
- [Events and sessions](./events-sessions.md) — Learn how the journal, addressed
  messages, session material, and usage records work.
- [Agent runtime](./agent-runtime.md) — Follow runtime bootstrap, SDK turns,
  tool authorization, and recovery.
- [MCP server](./mcp-server.md) — See how Gateway operations become authenticated
  MCP tools, and how acceptance differs from completion.

## Identity, configuration, and execution resources

- [Identity and secrets](./identity-secrets.md) — Understand broker identity,
  purpose-bound grants, secret references, and credential redemption.
- [Projects and configuration](./projects-config.md) — Learn how project
  authority, revisioned settings, skills, and accepted run selections work.
- [Environment and sandbox](./environment-sandbox.md) — Follow environment
  lifecycle, workspace volumes, network egress, and runtime placement.
- [Providers and models](./providers-models.md) — Understand provider selection,
  capability negotiation, resource pins, and model bindings.
- [Source Control](./source-control.md) — Follow repository pins, isolated
  workspaces, output capture, pull requests, and approval-bound merges.

## Data and operations

- [Knowledge and memory](./knowledge-memory.md) — Learn how records, proposals,
  accepted decisions, context compilation, and Memory providers work.
- [Persistence](./persistence.md) — Understand service-owned PostgreSQL schemas,
  object bytes, outboxes, inboxes, and retry boundaries.
- [Telemetry](./telemetry.md) — Learn how OpenTelemetry signals and exporters
  relate to durable product evidence.
- [Infrastructure and deployment](./infra-deployment.md) — Review Azure and
  Kubernetes source, workload configuration, images, and release boundaries.
- [Testing strategy](./testing-strategy.md) — Choose checks for service logic,
  PostgreSQL behavior, harnesses, and acceptance evidence.
