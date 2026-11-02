# System overview

Agentweaver separates intent, authority, execution and history. You confirm what
work may run. Owner services retain decisions and current permissions. AgentHost
runs guarded model turns in a selected Sandbox. The journal records what owners
committed; reading that history does not run the work again.

This is the whole-system view of Agentweaver source. Its twelve sections follow arc42.
Subsystem deep dives expand their own runtime and failure paths and link shared
deployment, decisions and concepts here. Source support, published artifacts,
observed deployment and completed acceptance are different states.

## 1. Introduction and Goals

Developers need to locate the owner of a change. Operators need to distinguish
accepted work from completed effects and recover without repeating uncertain
writes. Users need model proposals to remain inside confirmed scope and current
authority.

The main goals are explicit ownership, bounded execution, durable history and
recoverable effects. A model reply cannot approve a gate, change a provider
binding or create a credential grant.

## 2. Constraints

- The v1 rebuild is separate from the 0.x product on `dev`; legacy product
  behavior is not a default contract for this branch.
- Gateway/API, the browser client and the native .NET CLI are distinct
  client paths. Retained inbound MCP compatibility code is not a supported
  product MCP surface. Outbound remote MCP connections are separate.
- Projects & Config remains the current project-authority owner. Token role
  claims, tenant selectors and old configuration snapshots cannot create roles.
- Microsoft Agent Framework (MAF) belongs to Orchestrator. The native
  `GitHub.Copilot.SDK` session belongs to AgentHost/AgentRuntime.
- Dispatch is explicit and request-driven. A ready Pod or registered host is
  not an automatic scheduler.

## 3. Context and Scope

| Neighbor | Exchange with Agentweaver | Trust boundary |
| --- | --- | --- |
| Browser and native CLI | Authenticated Gateway operations and owner status | Broker bearer validation, then current owner authorization |
| Upstream OIDC provider | Sign-in identity | Broker validates issuer, signature, audience, expiry and nonce |
| Model source | Hosted Copilot or explicit BYOK execution | Current runtime/model binding and purpose-bound credential |
| GitHub | Repository reads, issues, pull requests and merge results | Exact repository pin and narrowly scoped credential |
| Kubernetes and storage providers | Placement, network policy, workspace and object bytes | Owner-controlled resource identity, generations and workload identity |
| Azure Monitor | Optional traces, metrics and logs | Diagnostic export, not product-state authority |

Internal owner calls use configured HTTPS roots and the backend CA. Dedicated
private operations add their own pinned mutual-TLS identity. Public issuer
discovery and external providers retain normal system trust; a forwarded
certificate header is not a TLS identity.

## 4. Solution Strategy

| Goal | Source approach | Consequence |
| --- | --- | --- |
| Keep permissions current | Owners ask Projects and recheck after waits | Remote authorization failure can stop an otherwise valid request |
| Keep execution recoverable | Commit stable intents and checkpoints before effects | Recovery must reconcile uncertainty rather than start another operation |
| Allow backend variation | Resolve candidates, negotiate resources, retain immutable pins | Changing a default does not migrate an existing run |
| Keep local changes atomic | Service transactions with outbox/inbox admission | Cross-service delivery remains at least once |
| Separate execution from evidence | Distinct MAF state, journal, SDK cache and workspace | No single history or cache read proves completion |

These are observable properties of the implementation, not a substitute for
the decision records in section 9.

## 5. Building Block View

| Block and source | Owns | Does not own |
| --- | --- | --- |
| Gateway — `services/gateway/Agentweaver.Gateway/` | Finite public `/api/v1` routing, OpenAPI and journal-backed SSE | Domain state or arbitrary upstream proxying |
| Identity — `services/identity/Agentweaver.Identity.Broker/` | OAuth records, connections, grants and redemption | Project membership |
| Projects — `services/projects-config/Agentweaver.Projects.Config/` | Roles, configuration revisions and accepted selections | Provisioned resources |
| Orchestrator — `services/orchestrator/Agentweaver.Orchestrator/` | Session tree, decisions, gates, fences, dispatch and MAF checkpoints | Journal order or Sandbox lifecycle |
| Core — `services/orchestrator/Agentweaver.Orchestrator.Core/` | Domain validation and execution planning | The owner database or an agent process |
| Environment — `services/environment/Agentweaver.Environment/` | Resource effects, volume generations and Sandbox leases | Workflow completion |
| AgentHost — `services/agenthost/Agentweaver.AgentHost/` | Guarded native runtime, collectors and process readiness | Current project roles or self-scheduling |
| Events — `services/events-and-sessions/Agentweaver.EventsAndSessions/` | Journal order, material references, messages and accounting | Execution decisions |
| Knowledge — `services/knowledge/Agentweaver.Knowledge/` | Authorized record operations and context composition | Run history |
| Browser — `apps/web/src/v1/`, entry `apps/web/src/App.tsx` | User interaction with Gateway and Broker | Server-side authority |
| Retained compatibility adapter — `services/mcp/Agentweaver.Mcp/` | Source OpenAPI-to-tool translation | A supported MCP product |

Source Control owner endpoints and state live in Orchestrator, not a separate
service directory. `Agentweaver.SourceControl` provides GitHub and isolated
workspace implementations.

Shared libraries under `packages/` are not separate services:
`Agentweaver.Abstractions` defines contracts; `Agentweaver.Providers` resolves
and verifies bindings; `Agentweaver.AgentRuntime` guards SDK work;
`Agentweaver.Persistence.Postgres` supplies outbox/inbox primitives;
`Agentweaver.Telemetry` creates diagnostic signals. Each host's `Program.cs`
composes its own dependencies. The [foundation static view](../architecture/overview.md#static-component-view)
shows contract and adapter relationships, not message order.

## 6. Runtime View

An authenticated caller and accepted run selection are prerequisites for this
representative workflow. Each owner still checks current authority.

1. **Enter through Gateway.** The browser obtains a Broker token; its
   `AuthProvider` keeps the bearer in memory and `AgentweaverGatewayClient`
   sends requests with `cache: 'no-store'`. The native CLI uses its own
   authentication path against the same finite API.
2. **Route to the owner.** Gateway validates the Broker token and forwards
   the original bearer to a fixed owner. Configuration reaches Projects;
   Knowledge reads reach Knowledge. Not every request passes Orchestrator.
3. **Confirm scope.** Projects commits the accepted selection. Orchestrator
   commits typed outcome, workflow and WorkPlan decisions, versions and gates.
4. **Persist before dispatch.** An explicit dispatch commits child
   associations, pending intents and MAF progress before contacting AgentHost.
5. **Execute under current bindings.** AgentHost rechecks registration,
   placement, credentials and action authority before turns and tools.
   Environment and Identity remain separate authority boundaries.
6. **Commit results.** Events stores typed turn content and SDK cache
   references. Orchestrator stores immutable SDK usage source receipts;
   Events consumes receipt references into accounting.
7. **Read committed state.** Gateway builds SSE from validated journal pages,
   not a live model callback. Owner status distinguishes pending, failed and
   completed work.

`202 Accepted` means that the owner accepted an operation, not that downstream
execution completed. If a response is lost, retry the same logical identity
with identical content. Changed content is not exact replay. Stale fences,
changed registrations, revoked authority and missing resource bindings stop
the affected operation.

See [orchestration](./orchestration.md) for dispatch and recovery,
[agent runtime](./agent-runtime.md) for native turns, and
[events and sessions](./events-sessions.md) for history and message delivery.

## 7. Deployment View

The checked-in target maps shared owner services onto Kubernetes
Deployment/Service pairs. AgentHost runs per Sandbox rather than as another
shared Deployment. Environment uses Kubernetes/CSI adapters for placement and
workspace resources; Events uses private Blob storage for material bytes.

| State | Storage and owner |
| --- | --- |
| OAuth records, connections and grants | Identity PostgreSQL; configured secret backend for secret bytes |
| Membership, roles, configuration and accepted selections | Projects PostgreSQL; Object Store for imported skill content |
| Session tree, decisions, fences, dispatch and MAF checkpoints | Orchestrator PostgreSQL |
| Ordered events, provider pins, delivery and usage ledger | Events PostgreSQL |
| Turn content, SDK cache and produced-output packages | Object Store bytes with owner-committed references |
| Environment state, leases, volume generations and resource effects | Environment PostgreSQL and selected providers |
| Knowledge records | Selected Memory provider; native PostgreSQL is the default |
| Repository files and generated output | Workspace volume and isolated Source Control workspace |
| Pending native turns and active credentials | AgentHost process state; not durable owner state |

Runtime and migration identities are separate. Normal startup verifies schema
and privileges; explicit maintenance performs migrations and reviewed grants.
Certificates, DNS, backend CA, credentials, resource identities and database
permissions are deployment prerequisites, not results of rendering manifests.

The [deployment deep dive](./infra-deployment.md) separates target topology,
Azure provisioning, image/package publication and workload rollout. No observed
deployment inventory is asserted here.

## 8. Crosscutting Concepts

- **Current authority:** authentication identifies the caller; Projects roles,
  exact run binding, grants and owner fences decide whether an operation may
  proceed. An accepted selection does not freeze permission.
- **Immutable binding:** accepted configuration, provider pins and runtime
  registrations have different owners. They must agree; none substitutes
  for another.
- **Consistency:** local owner transactions are atomic. Outbox/inbox retries
  do not create a transaction across services or exactly-once external effects.
- **Recovery:** journal history says what committed; MAF checkpoints say where
  execution stopped; SDK cache restores compatible conversation context;
  workspace storage holds files. Cache restore does not approve another turn.
- **Secrets and diagnostics:** credentials stay within their explicit
  purpose-bound path. Telemetry must not contain secret values, raw content or
  authorization material and does not replace receipts or accounting.

The [identity](./identity-secrets.md), [persistence](./persistence.md),
[providers](./providers-models.md) and [telemetry](./telemetry.md) views expand
these shared rules.

<span id="9-architecture-decisions"></span>

## 9. Architecture Decisions

- [ADR 0001: platform architecture](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0001-platform-architecture.md)
  is **Proposed**. Its broader target is not a statement that every feature
  is implemented or supported.
- [ADR 0002: owner-delegated issue synchronization](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0002-project-issue-sync-delegation.md)
  records **Accepted and implemented in source; not deployed**.

These repository-only records own decision status and rationale. Architecture
views explain mechanisms without creating another decision log. No CLI/API
compatibility policy is inferred from matching versions.

<span id="10-quality-requirements"></span>

## 10. Quality Requirements

These scenarios describe assessable source contracts, not measured production
SLOs. No system-wide latency or availability target is established here.

| Scenario | Context and stimulus | Block and response | Measure | Verification state |
| --- | --- | --- | --- | --- |
| Permission changes during a read | An owner wait overlaps project revocation | Owner rechecks and denies disclosure; Gateway stops SSE writes | No protected response/event after the failing recheck | Owner and Gateway controlled tests; no live claim |
| Dispatch response is lost | An explicit dispatch is retried after interruption | Orchestrator restores stable intents and reconciles joined results | Same logical operation/child identity; uncertain result is not success | Owner/checkpoint tests; complete deployed recovery is separate |
| Material expires while bytes remain | Cleanup has not yet deleted an expired object | Events checks database-time retention before retrieval and after byte read | Deny at `retain_until <= databaseTime` | Retention and failure-recovery tests; not a live Blob run |
| Journal reconnect | A reader supplies a valid saved cursor | Events returns only committed rows after that boundary | At most 200 events per page; scoped cursor cannot cross streams | Journal/provider tests; no throughput measurement |
| Provider default changes | An existing run uses a saved pin | Resource owner verifies original binding or fails | No silent backend substitution | Resolver and owner binding tests |

The [testing strategy](./testing-strategy.md) explains which checks exercise
these contracts and what fixture, PostgreSQL and deployed evidence can prove.

## 11. Risks and Technical Debt

- **External uncertainty:** a timed-out model, GitHub or storage operation may
  have taken effect. The owning service must reconcile its saved identity
  before retrying; transport failure is not proof of rollback.
- **Different recovery inputs:** missing retained turn content can prevent
  journal recovery even when a session ID or checkpoint exists. Events and
  runtime owners must preserve explicit failures rather than invent history.
- **Old unreferenced objects:** Object Store cannot list keys. Events inventory
  covers new uploads and backfilled committed references, not older orphans.
  Any broader scan needs an owner-approved contract; no container sweep is implied.
- **Verification gaps:** policy-object readback is not datapath enforcement,
  and controlled SDK tests are not paid-model acceptance. Service owners must
  obtain the corresponding separately approved native evidence.
- **Compatibility and performance:** version matching does not establish
  CLI/API compatibility, and source bounds do not establish production SLOs.
  Those requirements need explicit owner decisions and measurements.

<span id="12-glossary"></span>

## 12. Glossary

| Term | Meaning |
| --- | --- |
| Owner | Service that retains domain state and authorizes its transitions |
| Accepted selection | Immutable run configuration snapshot, not current permission |
| Candidate / negotiation / pin | Eligible adapter / actual resource facts / retained binding |
| Fence | Generation that rejects work from an older execution attempt |
| Journal cursor | Opaque stream scope and committed position, not execution authority |
| Receipt | Recorded result or evidence; its contract determines what it establishes |
| MAF checkpoint | Orchestrator-owned execution progress, not the SDK cache |
| SDK cache | Compatible native conversation recovery material, not effect replay |
