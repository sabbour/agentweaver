# System overview

Use this page to trace a request through Agentweaver v1 and find which service
owns each decision and piece of state. It describes the checked-in request-driven
implementation, not a deployment topology or an automatic scheduler.

## The system at a glance

You describe work in the web app or through a Model Context Protocol (MCP)
client. Control-plane services authorize requests and commit decisions.
AgentHost runs the GitHub Copilot SDK in a selected Sandbox.

The model can propose work, but the Orchestrator decides whether that work
matches the confirmed outcome, selected workflow, current permissions, and
accepted run selection. A model reply does not approve a gate or change a
provider binding.

## Find the implementation

| Area | Main source location |
| --- | --- |
| Public HTTP API and owner routing | `services/gateway/Agentweaver.Gateway/` |
| Authentication and credentials | `services/identity/Agentweaver.Identity.Broker/` |
| Project settings and accepted run selections | `services/projects-config/Agentweaver.Projects.Config/` |
| Coordination, checkpoints, and Source Control ownership | `services/orchestrator/Agentweaver.Orchestrator/` |
| Workflow and decision rules | `services/orchestrator/Agentweaver.Orchestrator.Core/` |
| Journal, messages, material references, and accounting | `services/events-and-sessions/Agentweaver.EventsAndSessions/` |
| Resource lifecycle and runtime placement | `services/environment/Agentweaver.Environment/` |
| Knowledge records and context | `services/knowledge/Agentweaver.Knowledge/` |
| Runtime executable and collectors | `services/agenthost/Agentweaver.AgentHost/` |
| First-party MCP tools | `services/mcp/Agentweaver.Mcp/` |
| Shared contracts and adapters | `packages/` |
| Current browser application | `apps/web/src/v1/`; stable entry point: `apps/web/src/App.tsx` |

## Keep service ownership clear

The Gateway does not own a workflow database. It forwards requests to the
service that owns each operation. Projects & Config checks current project
authority. Orchestrator owns session relationships and execution state. Events &
Sessions owns ordered history and addressed-message delivery.

Source Control owner endpoints and records currently live in Orchestrator.
GitHub and workspace implementations live in `Agentweaver.SourceControl`; there
is no separate Source Control service directory.

An accepted run selection records configuration, agent and model choices,
limits, and provider candidates. It is not a provisioned resource or a lasting
permission. Resource owners negotiate and pin actual resource identities
separately, and they check current authority whenever they act. Revoked
permission does not remain valid because an earlier snapshot exists. Execution
fences reject work from an older attempt.

## Distinguish history, execution, and files

| Data | What it tells you |
| --- | --- |
| Events journal | What an owner committed as history |
| Orchestrator decisions and Microsoft Agent Framework (MAF) checkpoints | Workflow position and pending execution intents |
| SDK cache bytes | How to restore a compatible native conversation |
| Workspace storage | Repository files and generated output |

Reading history does not replay tools. Restoring a cache does not approve a new
model turn.

Shared libraries do not become separate services just because multiple hosts
use them. Each host's `Program.cs` composes its dependencies. Important shared
packages include `Agentweaver.Abstractions` for versioned contracts,
`Agentweaver.Providers` for catalog and binding checks,
`Agentweaver.AgentRuntime` for runtime bootstrap and guarded turns,
`Agentweaver.Persistence.Postgres` for outbox/inbox primitives,
`Agentweaver.SourceControl` for GitHub and isolated workspaces,
`Agentweaver.Telemetry` for OpenTelemetry signals, and provider adapters for
Sandbox, Azure Files, Blob Storage, and Key Vault.

## Follow a request

To follow this example, you need an authenticated caller and an accepted run
selection. Each operation still checks current authority at its owner.

1. **Enter through the browser or MCP.** The browser obtains a Broker token
   through its authorization flow. The v1 `AuthProvider` keeps the bearer in
   memory, and `AgentweaverGatewayClient` sends authenticated requests with
   `cache: 'no-store'`. An MCP client calls `/mcp`; the MCP service forwards the
   same validated caller bearer to Gateway.
2. **Route to the owner.** Gateway validates the Broker token and maps a finite
   `/api/v1` route to a fixed owner address. Configuration goes to Projects &
   Config, Knowledge requests go to Knowledge, and coordination requests go to
   Orchestrator. Not every request passes through Orchestrator.
3. **Commit decisions before dispatch.** Accepting a run selection first reaches
   Projects & Config. Orchestrator then accepts the run root and typed outcome,
   workflow, and WorkPlan decisions. It validates the plan, persists decision
   versions, confirmation gates, and execution state. Dispatch is explicit:
   before contacting a child AgentHost, it commits the child association,
   pending intent, and checkpoint.
4. **Execute and record the result.** AgentHost checks current owner authority
   before turns and native tool calls. Events stores turn content and SDK cache
   material. Orchestrator records immutable SDK usage source receipts, and Events
   consumes receipt references into its usage ledger.
5. **Read committed results.** Gateway serves journal pages and builds
   server-sent events (SSE) from those pages. It does not turn a live model
   callback directly into durable history. Read owner status to distinguish
   accepted work, pending work, failure, and completion.

Environment and Identity supply placement and credential authority. A dispatch
request does not replace those checks.

## Locate state

| State | Owner and storage |
| --- | --- |
| OAuth records, connections, and grants | Identity Broker PostgreSQL; secret material uses its configured secret backend |
| Membership, roles, configuration, and accepted selections | Projects & Config PostgreSQL; imported skill content also uses Object Store |
| Session tree, gates, decisions, fences, dispatch intents, and MAF checkpoints | Orchestrator PostgreSQL |
| Ordered events, provider pins, message delivery, reference metadata, and usage ledger | Events & Sessions PostgreSQL |
| Turn content, SDK caches, and produced-output packages | Object Store bytes with owner-committed references |
| Environment state, leases, volume generations, and resource effects | Environment PostgreSQL and selected resource providers |
| Knowledge records | Selected Memory provider; native PostgreSQL is the default implementation |
| Repository and generated files | Selected workspace volume and isolated Source Control workspaces |
| Pending native turns and active credentials | AgentHost process state; not a replacement for owner records or journal history |

## Understand failures

A successful HTTP response has the meaning defined by its owner. `202 Accepted`
means the owner accepted an operation; it does not establish downstream
completion. An unavailable owner, stale fence, changed registration, or missing
resource binding stops the affected operation.

Retries reuse stored identities and compare the original content. A changed
request is not an exact replay. After uncertain execution, recovery uses owner
state and evidence rather than assuming a model or tool effect ran.

## Related content

- [Gateway](./gateway.md), [orchestration](./orchestration.md), and
  [agent runtime](./agent-runtime.md) follow the request path.
- [Events and sessions](./events-sessions.md) explains durable history.
- [Persistence](./persistence.md) explains cross-service consistency.
- [Foundation architecture](../architecture/overview.md) and
  [contracts and endpoints](../reference/contracts.md) describe component and
  API boundaries.
