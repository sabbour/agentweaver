# System overview

## The mental model

Agentweaver separates intent, control, and execution. You describe work through
the web app or a Model Context Protocol (MCP) client. Control-plane services
authorize requests and commit decisions. AgentHost runs the GitHub Copilot SDK
inside a selected Sandbox.

The model proposes work. The Orchestrator decides whether that work matches the
confirmed outcome, selected workflow, current permissions, and accepted run
selection. A model reply does not approve a gate or change a provider binding.

Read this as a map of the checked-in implementation, not a deployment topology.
The source contains request-driven workflow dispatch and runtime hosts. Dispatch
does not schedule itself.

## Where this lives

- `services/gateway/Agentweaver.Gateway/` — Public HTTP routes and owner clients.
- `services/identity/Agentweaver.Identity.Broker/` — Authentication and credential
  authority.
- `services/projects-config/Agentweaver.Projects.Config/` — Project configuration,
  permissions, skills, and accepted run selections.
- `services/orchestrator/Agentweaver.Orchestrator/` — Durable coordination,
  execution checkpoints, runtime registration, and Source Control ownership.
- `services/orchestrator/Agentweaver.Orchestrator.Core/` — Workflow, decision,
  WorkPlan, policy, and backlog rules.
- `services/events-and-sessions/Agentweaver.EventsAndSessions/` — Journal,
  messages, material references, and accounting.
- `services/environment/Agentweaver.Environment/` — Resource lifecycle and runtime
  placement.
- `services/knowledge/Agentweaver.Knowledge/` — Knowledge records and context.
- `services/agenthost/Agentweaver.AgentHost/` — Runtime executable and collector
  entry points.
- `services/mcp/Agentweaver.Mcp/` — First-party MCP tools over Gateway.
- `packages/` — Shared contracts, adapters, and runtime libraries.
- `apps/web/src/v1/` — Current browser application, authentication, API client,
  and journal consumption.
- `apps/web/src/App.tsx` — Stable entry point that exports the v1 application.

## Core concepts and invariants

### Each service owns its state

The Gateway does not own a workflow database. It forwards requests to the service
that owns the operation. Projects & Config checks current project authority.
Orchestrator owns session relationships and execution state. Events & Sessions
owns the ordered history and addressed-message delivery.

These boundaries also apply to Source Control. Its current owner endpoints and
records live in the Orchestrator service. The GitHub and workspace implementations
live in `Agentweaver.SourceControl`; there is no separate Source Control service
directory.

### An accepted selection is not a resource or a permission

Projects & Config records the accepted configuration, agent and model choices,
limits, and provider candidates for a run. Resource owners negotiate capabilities
and pin actual resource identities separately.

Services still check current authority when they use an accepted selection.
Revoked permission does not remain valid because an earlier snapshot exists.
Execution fences reject work from an older execution attempt.

### History, execution state, and caches serve different purposes

- The Events journal records what was committed.
- Orchestrator decisions and Microsoft Agent Framework (MAF) checkpoints record
  workflow position and pending execution intents.
- SDK cache bytes help restore a compatible native conversation.
- Workspace storage holds repository files and generated output.

Reading history does not replay tools. Restoring a cache does not approve a new
model turn.

## Shared packages

| Package | Role in the flow |
| --- | --- |
| `Agentweaver.Abstractions` | Versioned provider, session, storage, environment, usage, and source-control contracts. |
| `Agentweaver.Providers` | Provider catalog validation, selection, and resource-binding checks. |
| `Agentweaver.Identity` | Grant and runtime contracts, authorized redemption, and fixed owner HTTP clients. |
| `Agentweaver.AgentRuntime` | Bootstrap, native SDK sessions, guarded turns, and recovery. |
| `Agentweaver.Persistence.Postgres` | Transactional outbox, consumer inbox, and relay primitives. |
| `Agentweaver.SourceControl` | GitHub operations and isolated Git workspace handling. |
| `Agentweaver.Providers.Sandbox.AgentSandbox` | Kubernetes-backed Sandbox and BuildTest operations. |
| `Agentweaver.Providers.Storage.AzureFiles` | Generation-bound Azure Files CSI workspace-volume operations. |
| `Agentweaver.ObjectStore.AzureBlob` | Opaque object reads and writes through Azure Blob Storage. |
| `Agentweaver.Secrets.AzureKeyVault` | Exact-version secret reads and secret-version writes. |
| `Agentweaver.Telemetry` | Shared OpenTelemetry traces, metrics, and logs. |
| `Agentweaver.Telemetry.AzureMonitor` | Azure Monitor exporter composition. |

These libraries do not become separate services just because several hosts use
them. Each host's `Program.cs` selects and constructs its dependencies.

## How a request travels

### Enter through the browser or MCP

The browser obtains a Broker token through its authorization flow. The v1
`AuthProvider` keeps its bearer session in memory. `AgentweaverGatewayClient`
sends authenticated requests to Gateway with fetch `cache: 'no-store'`.

MCP clients call the authenticated `/mcp` endpoint instead. The MCP service
converts a discovered tool call into a request to Gateway. It forwards the same
validated caller bearer.

### Route to the owner

Gateway validates the Broker token and maps a finite `/api/v1` route to a fixed
owner address. A configuration request goes to Projects & Config. A Knowledge
request goes to Knowledge. A coordination request goes to Orchestrator.
Not every request passes through Orchestrator.

For example, accepting a run selection reaches Projects & Config first. Accepting
the run root and submitting typed outcome, workflow, and WorkPlan decisions then
reach Orchestrator. Each owner enforces its own current permission and revision
checks.

### Commit decisions before dispatch

Orchestrator validates the selected workflow and proposed plan through Core. It
persists decision versions, confirmation gates, and execution state. An explicit
dispatch request reads the confirmed plan and computes ready work.

Before contacting a child AgentHost, dispatch commits the child association,
pending intent, and checkpoint. It then verifies current runtime registration and
readiness and sends the bound message. Environment and Identity supply placement
and credential authority; the dispatch request does not replace those checks.

### Execute and record the result

AgentHost authorizes the turn and native tool requests against current owners.
Events stores turn content and SDK cache material. Orchestrator records immutable
SDK usage source receipts, and Events consumes receipt references into its usage
ledger.

Gateway serves committed journal pages and constructs server-sent events (SSE)
from those pages. It does not turn a live model callback directly into durable
history. A client reads owner status to distinguish accepted work, pending work,
failure, and completion.

## Where state lives

| State | Owner and storage |
| --- | --- |
| OAuth records, connections, and grants | Identity Broker's PostgreSQL schema; secret material uses Identity's configured secret backend. |
| Membership, roles, configuration, and accepted selections | Projects & Config's PostgreSQL schema; imported skill content also uses Object Store. |
| Session tree, gates, decisions, fences, dispatch intents, and MAF checkpoints | Orchestrator's PostgreSQL schema. |
| Ordered events, provider pins, message delivery, reference metadata, and usage ledger | Events & Sessions' PostgreSQL schema. |
| Turn content, SDK caches, and produced-output packages | Object Store bytes with owner-committed references. |
| Environment state, leases, volume generations, and resource effects | Environment's PostgreSQL schema and selected resource providers. |
| Knowledge records | The selected Memory provider; native PostgreSQL is the default implementation. |
| Repository and generated files | The selected workspace volume and isolated Source Control workspaces. |
| Pending native turns and active credentials | AgentHost process state; not a substitute for owner records or journal history. |

## Failure behavior

A successful HTTP response has the meaning defined by its owner. `202 Accepted`
does not establish downstream completion. An unavailable owner, stale fence,
changed registration, or missing resource binding stops the affected operation.

Retries reuse stored identities and compare the original content. A changed
request does not become an exact replay. After uncertain execution, recovery uses
owner state and evidence rather than assuming that a model or tool effect ran.

## Continue reading

- [Gateway](./gateway.md), [orchestration](./orchestration.md), and
  [agent runtime](./agent-runtime.md) follow the request path.
- [Events and sessions](./events-sessions.md) explains the durable history.
- [Persistence](./persistence.md) explains cross-service consistency.
- [Foundation architecture](../architecture/overview.md) and
  [contracts and endpoints](../reference/contracts.md) describe the component and
  API boundaries.
