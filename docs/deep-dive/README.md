# Deep dives

Start with the [system overview](./00-system-overview.md) to understand who
decides, who executes and who records history. Then choose the owner or failure
path you need. These pages describe v1 source; they are not a live deployment
inventory or evidence that every planned feature is accepted.

The [architecture pages](../architecture/overview.md) explain component
boundaries. The [reference](../reference/contracts.md) records contracts and
endpoints. These deep dives connect those boundaries to request flow, state, and
failure handling.

## How the views fit together

The system overview uses arc42's twelve complementary views: goals,
constraints, context, strategy, building blocks, runtime, deployment,
crosscutting concepts, decisions, quality requirements, risks and glossary.
Subsystem pages combine the opening context views and delegate shared
deployment/decisions to canonical pages rather than repeat a framework.
Each expands its important runtime, failure and recovery paths.

Static dependencies do not establish message order. Runtime sequences do not
establish a deployed topology. Quality scenarios name an observable response
and verification boundary, not an invented production SLO. ADRs retain their
actual status in the [system decision view](./00-system-overview.md#9-architecture-decisions).

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
- [Retained MCP compatibility adapter](./mcp-server.md) — Understand source
  OpenAPI-to-tool translation, not a supported 1.x product entrypoint.

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
