# API Host Core — Conceptual Deep Dive

## Purpose & Scope

The Agentweaver API host is the composition shell for the product. It does not own every domain algorithm; instead, it makes the system runnable and observable by wiring services together, enforcing cross-cutting policy, exposing HTTP capabilities, and preparing durable state before traffic is accepted.

The API core architecture centers on:

- host bootstrap and readiness;
- dependency injection and service lifetimes;
- middleware and request authorization flow;
- the minimal API endpoint architecture;
- request/response contract principles;
- configuration selection;
- diagnostics and metrics;
- host-level invariants and extension seams.

Domain internals are covered by the focused deep dives for [Auth & security](./auth-security.md), [orchestration](./orchestration.md), [sandboxing](./sandbox.md), [memory and decisions](./memory-decisions.md), [data persistence](./data-persistence.md), and [Git integration](./git-integration.md). The API core composes those domains rather than reimplementing their internal logic.

## Effective model provider admission

`EffectiveModelProviderResolver` remains the selection authority.
`AiExecutionPlanService` encrypts caller-, project-, and operation-bound acceptance with an authenticated execution key.
BYOK configuration fingerprints include execution parameters.
Copilot identity includes the binding credential version.
The public provider fingerprint does not grant execution authority.

Covered synchronous actions reject changed provider configuration with `409 model_provider_changed` before model invocation.
Run continuations compare current resolver output with durable provenance.
They never reconstruct provider authority from event identifiers.
The runtime guard awaits provenance persistence before the next covered model call.
Multi-pass generators retain the accepted BYOK configuration rather than select another ambient provider.
Repository-only retry inheritance does not carry stale Copilot credentials into a newly accepted retry.

The admission contract covers these boundaries:

| Path | Boundary |
| --- | --- |
| Generation, casting, backlog decomposition | Opaque admission, configuration fence, accepted configuration, pre-call provenance |
| Coordinator spec drafting | Effective-provider boundary, accepted BYOK configuration, and runtime invocation guard |
| Preview classifiers and preview command proposal | Effective-provider run fence, accepted BYOK configuration, and pre-call provenance |
| Other coordinator selection/classifier actions | Copilot-only run fence and pre-call provenance |
| Worker and reviewer turns, including AgentHost | Accepted provider context and guard before application-issued calls, re-asks, and SDK retries |
| Assistant and RemoteOperator dispatch | Opaque admission, platform scope, launch fence, pre-dispatch revalidation |
| Retry, revision, restart, queued pickup | Current resolver comparison against accepted fingerprints |
| MCP actions | API execution-key preparation and forwarding |
| Web provider context | Accessible Expected/Using/Used labels and stable fingerprint comparison |

AgentHost revalidates before each application-issued model attempt through `POST /api/runs/{id}/model-provider/validate`.
The callback requires the existing run capability and the immutable provider fingerprint from pod configuration.
The API compares that fingerprint with the current resolver and accepted run provenance.
It awaits provenance persistence before it permits the call.
Provider changes return `409 model_provider_changed` without a model call.
The pod callback has no independent selection authority.

Public provider context contains redacted provider kind, scope, type, model, availability, and an opaque comparison fingerprint.
It does not contain credentials, account names, or provider-binding identities.
The UI maps `prepared`, `active`, and `completed` phases to **Expected provider**, **Using**, and **Used**.

There is no registered `/api/console/turn` route in this host.
`GET /api/auth/session` returns authentication metadata and `ai_configured`, not a model response.
`ResolveForSessionAsync` supplies that metadata and execution-context preparation.
It has no direct production model-call consumer.
The production conversation routes use the guarded Assistant and AgentHost path.

This is not proof of every request inside the external Copilot service.
Focused tests use fake model providers.
They do not prove live SDK, Kubernetes, or MCP process behavior.

### Execution-key configuration

`AiExecution:ProviderKeySigningKey` takes precedence when configured.
Otherwise, the service derives a purpose-specific key from the existing server-only `Auth:CopilotApp:ClientSecret`.
`Auth:RepoApp:ClientSecret` is the next fallback.
Existing Kubernetes deployments already provision these App secrets.
This change does not add a required mounted secret.
Production deployments without either App secret require an explicit execution signing key.
Development and test environments also permit `Auth:ApiKey` as a fallback.

All API and worker replicas must use the same secret.
Secret rotation invalidates prepared and queued execution keys.
Queued work then requires fresh submission.

## The Host in One Picture

Agentweaver uses a **minimal API + endpoint modules + stores/services** architecture. The host is a thin, explicit composition root; endpoint modules are thin adapters; services and stores contain the actual behavior.

The important separation is:

- **Program startup** decides what exists: configuration, DI registrations, boot checks, middleware, and endpoint groups.
- **Endpoint modules** decide what HTTP surface exists: routes, verbs, request DTOs, response DTOs, and per-resource authorization checks.
- **Services** decide what a business operation means.
- **Stores/providers** decide how data and external resources are accessed.

This shape keeps the host easy to reason about. Adding a feature should usually mean adding a contract, a service/store if needed, and one endpoint registration module; it should not require changing middleware or unrelated route handlers.

## Why Minimal APIs Here

Agentweaver's API is a command-and-control surface with many focused operations: start a run, stream a run, approve a tool request, list project files, read diagnostics, update a review policy, and so on. Minimal APIs fit that shape because they make route registration direct and avoid controller ceremony.

The design only works because route registration is modularized. Each feature area exposes a method that maps its routes, and startup calls those methods explicitly. That gives the project most of the clarity of controllers without forcing every small route into a controller class.

The trade-off is discipline. Minimal APIs do not automatically prevent a handler from becoming a large blob of logic. Agentweaver's intended invariant is: **endpoint handlers adapt HTTP to the domain; they do not become the domain**. If a handler starts coordinating multiple stores, retry rules, or lifecycle transitions, that logic belongs in a service.

Where this lives: `apps/Agentweaver.Api/Program.cs`; `apps/Agentweaver.Api/Endpoints/`; domain endpoint folders such as `Workflows/` and `ReviewPolicies/`.

## Bootstrap & Readiness Lifecycle

### Problem solved

The API cannot safely accept traffic until three things are true:

1. dangerous development-only bypasses are rejected in production;
2. persistent schemas are present and up to date;
3. interrupted long-running work has been reconciled enough that new requests see a coherent world.

Startup therefore behaves like a readiness gate, not just a web-server launch.

### Why this order

- **Security checks happen before service construction** so a bad production deployment dies obviously instead of exposing a partially running host.
- **Schemas are ensured before middleware and routes matter** so the first real request does not pay for schema creation or discover a missing table.
- **Recovery runs before traffic** so clients do not see stale "in progress" state that the host already knows how to reconcile.
- **Middleware is installed before endpoints are mapped as executable request handlers** so every route shares the same cross-cutting behavior.

### Trade-offs

This makes startup heavier, especially when database migrations or recovery scans are non-trivial. The benefit is a simpler runtime: handlers can assume the host is initialized, stores exist, and background recovery has already had a chance to normalize old state.

### Invariants

- Startup database work must be idempotent.
- Recovery must be safe to run after a crash or restart.
- Production auth bypasses must fail closed.
- The host should never depend on system temporary directories for application data.

Where this lives: `apps/Agentweaver.Api/Program.cs`; `apps/Agentweaver.Api/Infrastructure/`; `apps/Agentweaver.Api/Memory/`.

## Dependency Injection as the Service Graph

### Problem solved

The host coordinates long-lived state: run streams, locks, registries, background services, SQLite stores, workspace providers, GitHub clients, sandbox routing, and EF contexts. Dependency injection makes those dependencies explicit and gives each category the right lifetime.

### Lifetime logic

- **Singletons** are used for components that are stateless, internally synchronized, or intentionally process-wide: registries, locks, stream stores, provider selectors, and most raw SQLite stores. When a singleton touches SQLite, the safe pattern is connection-per-operation rather than keeping one shared connection forever.
- **Unit-of-work contexts** are isolated because EF `DbContext` is not thread-safe. Production singleton stores use `IDbContextFactory<MemoryDbContext>` to create a fresh context per operation rather than retaining a shared context.
- **Hosted services** are used for autonomous loops that are part of the host lifecycle, such as heartbeat pickup and cleanup.
- **Factories** are used when configuration chooses an implementation, such as local versus persistent-volume workspaces or sandbox execution routing.

### Trade-offs

The singleton-heavy approach makes in-process coordination straightforward and avoids repeatedly constructing expensive infrastructure. The risk is hidden shared mutable state. Agentweaver mitigates that by keeping stores connection-per-call, bounding in-memory buffers, and making coordination objects explicit.

### Rebuild rule

When adding a dependency, first decide whether it represents process-wide coordination, per-request/unit-of-work state, an external HTTP client, or a background loop. Register it according to that role; do not choose a lifetime just because another nearby service uses it.

Where this lives: `apps/Agentweaver.Api/Program.cs`.

## Middleware Pipeline

### Problem solved

Endpoints share exception handling, CORS, rate limiting, authentication, and endpoint-policy authorization. Project/resource role checks remain in the endpoint or service that knows the resource.

### Endpoint classification and authentication

The web-role request order is forwarded headers, exception handling, routing, CORS, rate limiting, endpoint-authorization integrity, authentication, unmatched-endpoint handling, and authorization. Endpoint integrity validates the selected route's authorization classification before dispatch. Missing or contradictory metadata fails closed; URL-prefix exemptions are not the authorization model.

Authentication selects a credential scheme allowed by the endpoint. Entra validation includes the configured tenant; broker-capable routes independently validate Agentweaver broker tokens. Neither GitHub organization membership nor a GitHub username grants platform access.

### Resource authorization

After endpoint-policy authorization, handlers load the relevant resource and enforce the required project role: Viewer, Contributor, or Owner. Input validation and resource lookup order varies by endpoint; this is not a second global organization middleware. Internal-service and run-capability endpoints have explicit, separate classifications.

### Rate limiting

The rate limiter middleware is globally installed, but named policies apply to endpoints that opt in. OAuth protocol endpoints opt in because they are public and abuse-sensitive. Merely installing the middleware does not rate-limit every API endpoint.

### Middleware rules

- Classify public, protocol-managed, platform, broker, internal-service, and run-capability endpoints explicitly.
- Do not infer authorization from `/api` or any other path prefix.
- Resource authorization depends on the authenticated identity and the requested resource.
- The worker role exposes `/healthz` and `/readyz`, not the web role's application API. The role split does not by itself prove that every background service is worker-exclusive.

Where this lives: `apps/Agentweaver.Api/Program.cs:1255-1326`; `apps/Agentweaver.Api/Auth/EndpointAuthorization.cs`; `apps/Agentweaver.Api/Auth/EntraAccessTokenValidator.cs`; `apps/Agentweaver.Api/Security/ProjectAuthorization.cs`.

## Endpoint Surface & Request Handling Structure

### Problem solved

The API surface is broad, but it is not arbitrary. Routes are organized around resources and capabilities so clients can predict where operations live.

The main route families are:

| Capability family | Route shape | What the handler should do |
|---|---|---|
| Health/readiness | `/health`, `/api/health`, `/api/ping`, `/healthz/workspace` | Return cheap public liveness/readiness answers for load balancers and operators. |
| Diagnostics/metrics/system | `/api/diagnostics*`, `/api/overview`, `/api/projects/{id}/dashboard`, `/api/system/runtime` | Return protected operational projections assembled from live stores and host state. |
| Auth and OAuth | `/api/auth/*`, `/.well-known/*`, `/oauth/*` | Bootstrap Entra authentication, expose OAuth/OIDC metadata, exchange tokens, and manage sessions. |
| Projects/workspaces | `/api/projects*`, `/api/projects/{id}/workspace*` | Create/list/update projects and expose workspace refs, trees, and file content through project ownership checks. |
| Runs | `/api/runs*`, `/api/projects/{id}/runs*`, `/api/projects/{id}/orchestrations` | Read run state, stream events, inspect files/diffs/history, retry, archive, delete, or start coordinator work. |
| Human-in-the-loop actions | `/api/runs/{id}/review`, `/commit`, `/request-changes`, approvals/denials/questions/autopilot | Convert user decisions into run lifecycle transitions. |
| Sandbox | `/api/sandbox-policy`, `/api/runs/{id}/sandbox/port-forward*` | Read/update project-scoped sandbox policy and manage sandbox port-forward sessions. |
| Workflow and review policy definitions | `/api/projects/{id}/workflows*`, `/api/projects/{id}/review-policies*` | Manage project-defined workflow and review policy configuration. |
| Backlog/board/decompose | `/api/projects/{id}/backlog*`, `/board`, `/workflow-stages`, `/backlog/decompose` | Expose orchestration planning queues and decomposition helpers. |
| Coordinator | `/api/projects/{id}/orchestrations`, `/api/runs/{id}/outcome-spec*`, `/work-plan`, `/children`, `/steer`, `/assembly/*` | Expose multi-agent orchestration state and control surfaces. |
| Team/casting/blueprints | `/api/projects/{id}/team*`, `/api/casting*`, `/api/blueprints*` | Manage project team model, role proposals, and blueprint generation/validation. |
| Decisions/memory/sessions | `/api/projects/{id}/decisions*`, `/memory*`, `/agents/{name}/memory*`, `/sessions*` | Expose decision inbox, decision ledger, agent memory, session context, and file interop. |

### Handler pattern

A typical protected handler follows the boundaries in [The host in one picture](#the-host-in-one-picture). A second generic client-to-store chain adds no distinct information.

The exact service/store calls differ by feature, but the responsibilities stay stable:

1. **Authenticate and authorize the endpoint** through middleware before handler execution.
2. **Bind and validate** route values, query values, and JSON bodies; load the resource needed for the operation.
3. **Authorize the resource** using the required project role and resource relationship. Do not special-case GitHub usernames such as `admin`; there is no built-in username-derived superuser role.
4. **Validate dangerous inputs centrally**, especially filesystem-relative paths used by workspace and file routes.
5. **Delegate behavior** to a service or store.
6. **Project the result** into a DTO that is safe and stable for clients.

### Route design rules

- Use `/api/projects/{id}/...` for project-scoped resources.
- Use `/api/runs/{id}/...` for run-scoped resources.
- Use `GET` for projections, `POST` for commands/state transitions, `PATCH` for partial updates, `PUT` when replacing/updating a named configuration, and `DELETE` only when deletion semantics are intended.
- Model command endpoints as subresources or actions (`/retry`, `/review`, `/commit`) when the operation is not a simple CRUD update.
- Give public protocol routes explicit endpoint authorization metadata rather than path-based exemptions.

Where this lives: `apps/Agentweaver.Api/Endpoints/`; `apps/Agentweaver.Api/Workflows/`; `apps/Agentweaver.Api/ReviewPolicies/`; `apps/Agentweaver.Api/Diagnostics/`; `apps/Agentweaver.Api/Metrics/`.

### Effective model-provider context

Before a first-party UI starts a generative action, it calls
`POST /api/ai/execution-context` with the operation name and, for project-scoped work, the
project id. The endpoint does not accept prompts and does not start execution. It resolves the
same project, platform, or user provider context used by the execution layer and returns a redacted
`effective_model_provider` object for an accessible **Expected provider** hint.

The contract distinguishes where the action was resolved from where the provider is configured.
For example, a workflow-generation action in a project can return
`resolution_scope: "project"`, `provider_scope: "platform"`, and `provider_kind: "byok"` when
the project inherits deployment-wide BYOK. `provider_key` is an opaque comparison value and must
never be displayed.

Run-associated execution persists `run.model_provider_resolved`. `GET /api/runs/{id}` projects the
latest durable event as `effective_model_provider`; it returns `null` when no resolution was
recorded and never invents provider identity from the coarse `Run.ModelSource` field. Assistant
creation and every Assistant turn emit the event so a provider switch is visible even when the
conversation keeps the same run id.

## Streaming and Durable Run Events

### Problem solved

Agent runs are long-lived and interactive. The UI needs low-latency updates while a run is active, but users also refresh browsers, reconnect, and inspect completed runs. A purely in-memory stream would be fast but fragile; a purely database-polled stream would be durable but less responsive.

Agentweaver durably appends events before exposing them to subscribers. Delivery then depends on the selected store and whether this replica has a local run-stream entry:

### Control flow

1. A run event is appended with a run id, sequence, type, and payload.
2. The event is written durably before it is acknowledged to the producer.
3. With a local `RunStreamStore` entry, the endpoint reads an atomic snapshot and waits for local changes.
4. Without that entry, it subscribes to the durable provider. `EfRunEventStream` polls shared rows after the cursor (250 ms when empty); `SqliteRunEventStream` uses durable replay and a bounded process-local channel.
5. Terminal replay ends the transport after draining its batch. Some human-review gates also emit transport `done`; that is not durable run completion.

### Why this design

- Durable-first append means a client cannot observe an event that would be lost on crash.
- Local buffers/channels bound memory; they are not a cross-replica message bus.
- Shared-table polling lets a different replica observe committed events without owning the producer's local stream.
- Sequence-based replay makes browser reconnects and `Last-Event-ID` style cursors practical.

### Invariants

- Never stream a run to a caller who cannot read that run.
- Durable append precedes live fan-out.
- Reconnects should not miss events; duplicates should be tolerable or skipped by sequence.
- Terminal events should cause clean completion rather than endless idle streams.

Where this lives: `apps/Agentweaver.Api/Infrastructure/RunStreamStore.cs`; `apps/Agentweaver.Api/Infrastructure/EfRunEventStream.cs`; `apps/Agentweaver.Api/Infrastructure/SqliteRunEventStream.cs`; `apps/Agentweaver.Api/Endpoints/RunEndpoints.cs:457-555`.

## Contracts and DTOs

### Problem solved

The API is consumed by web UI, MCP tools, and automation clients. Internal entity names, enum values, and persistence details can evolve, but the wire contract must remain stable.

Agentweaver therefore treats DTOs as a boundary layer rather than returning database entities directly.

### Contract principles

- **Explicit JSON names.** Public field names are pinned with explicit JSON metadata where needed instead of relying entirely on ambient naming policy.
- **Stable enum strings.** Domain states are translated to client-facing strings such as run status and model source values. Clients should not depend on internal enum names.
- **Projection over leakage.** Response DTOs include fields useful to clients: ids, statuses, timestamps, flags, summaries, diffs, sandbox state, and workflow metadata. They should not expose internal persistence-only columns unless those columns are intentionally part of the API.
- **Nullable means meaningfully absent.** Optional fields represent genuinely unavailable or inapplicable data, not handler laziness.
- **Requests describe intent.** A command request should contain the minimum information needed to perform the operation and should let the service derive the rest from current state.

### Trade-offs

DTO mapping adds code, but it preserves compatibility. Without DTOs, database refactors and domain enum changes would become accidental API breaking changes.

Where this lives: `apps/Agentweaver.Api/Contracts/`.

## Configuration Model

### Problem solved

The same host runs locally, in tests, and in hosted/containerized environments. Configuration decides which roots, databases, auth settings, workspace providers, and sandbox providers are active without changing code paths.

ASP.NET Core's normal configuration stack is used: appsettings, environment-specific settings, environment variables, secrets, and command-line arguments. Environment variable names can use the standard double-underscore form for nested keys.

### Important configuration families

| Family | Conceptual role |
|---|---|
| Logging | Controls host and framework log verbosity. |
| CORS | Declares browser origins allowed to call the API. Empty means no configured browser origins. |
| Auth:Entra | Defines platform identity and tenant validation; GitHub capabilities do not grant platform/project roles. |
| Auth:RepoApp / Auth:CopilotApp | Defines separate repository and Copilot capability integrations. |
| Auth:OAuth | Defines OAuth issuer/audience/signing/redirect behavior for MCP/API token flows. Production guards require safe pinned values. |
| Database | Postgres selects EF operational stores, durable events, shared checkpoints, and leases. Local SQLite uses raw operational stores plus a separate EF memory database and file checkpoints. Other EF provider options are not equivalent to the Postgres operational topology. |
| Workspace | Selects how project workspaces are resolved. Local mode honors caller-provided paths; persistent-volume/kubernetes mode maps project ids under a configured mount root. |
| Runs | Restricts allowed local repository roots for filesystem safety. |
| Coordinator | Controls heartbeat enablement and cadence. |
| Sandbox | Supplies sandbox execution settings, especially Kubernetes execution details. |
| Testing bypass flags | Development/test-only switches that must not be accepted in production. |

### Database split

There are two important persistence surfaces:

- the **operational database** for projects, runs, backlog, workflow state, and other host operations;
- the **memory database** for EF-managed memory/decision data and durable run events.

In production Postgres, both surfaces use `MemoryDbContext` and one database, with a fresh context per operation. The two-file operational/EF split applies to local SQLite. Workspace files remain a separate backup and recovery concern in either mode.

### Workspace provider split

Local workspaces and mounted/container workspaces solve different problems. Local mode is convenient for desktop development because the caller can point the project at an existing checkout. Persistent-volume/kubernetes mode is safer for hosted execution because project ids map deterministically under a known mount root, and the caller does not control arbitrary filesystem paths.

Workspace health is based on practical write probes, not only directory existence, because mounted filesystems can report misleading metadata while still failing real writes.

Where this lives: `apps/Agentweaver.Api/appsettings.json`; `apps/Agentweaver.Api/Program.cs`; `apps/Agentweaver.Api/Infrastructure/`; `apps/Agentweaver.Api/Sandbox/`.

## Diagnostics & Metrics

### Problem solved

Operators need to know whether the host is healthy and what work is happening without trusting client-side guesses. Diagnostics answer "can the system function right now?" Metrics answer "what has the system been doing?"

### Diagnostics model

Diagnostics are live checks assembled server-side:

There are three levels:

- **Public health/readiness** endpoints are cheap and suitable for load balancers.
- **System diagnostics** are protected and include database, data directory, registry, heartbeat, project-store, and GitHub checks.
- **Project diagnostics** are protected and scoped to one project: workspace availability, workflow directory, review-policy directory, active workflow, and active review policy.

The heartbeat endpoint exposes automation status: whether heartbeat is enabled, its cadence, last tick, last error, recent activity, and a catalog of host automations.

### Metrics model

Metrics are derived from live stores and known in-process state. They are not fabricated. If the system lacks a real source for a value, the API omits it rather than inventing it.

The project dashboard summarizes:

- run counts and active work;
- 30-day created/done throughput;
- agent leaderboard and success/duration data.

The global overview summarizes:

- at-a-glance host/project/run counters;
- live sessions and active workflow runs;
- active projects and recent activity;
- degraded health signals such as heartbeat problems or merge failures.

Run duration metrics subtract human-review wait time where that wait is tracked. That keeps automation performance from being distorted by time spent waiting for a person.

### Invariants

- Diagnostics and metrics should read real host state.
- Expensive checks should be bounded and explicit.
- Singleton diagnostics/metrics services must remain safe under concurrent requests.
- Do not report cost or other synthetic values without an authoritative source.

Where this lives: `apps/Agentweaver.Api/Diagnostics/`; `apps/Agentweaver.Api/Metrics/`; `apps/Agentweaver.Api/Endpoints/MetricsEndpoints.cs`.

## Host-Level Invariants & Edge Cases

These rules are the practical knowledge needed to rebuild or extend the host safely:

- **The endpoint mapping list is the routing seam.** A new feature should add a mapping extension and be called from startup explicitly.
- **Authorization is endpoint-classified.** Integrity checks reject unclassified or conflicting metadata; public access is not a prefix exemption.
- **Resource access is layered.** Authentication establishes identity; handlers/services enforce project roles and resource relationships. A GitHub login never implies platform or project access.
- **Filesystem paths are dangerous inputs.** File and diff routes should use centralized relative-path validation and reject rooted, parent-traversal, device, UNC, drive-relative, control-character, or alternate-data-stream tricks.
- **SSE has both live and durable layers.** Live buffers optimize active UI sessions; durable run events support replay and restart recovery.
- **Database split matters.** Operational state and EF memory/run-event state may live in different databases.
- **Workspace provider choice changes trust boundaries.** Local mode can honor caller paths after validation; mounted/kubernetes modes should derive paths from project ids.
- **Persistent-volume health should test real writes.** Existence checks alone are not enough for mounted filesystems.
- **Sandbox policy is project-scoped configuration.** It should preserve unrelated settings and only write for known project workspaces.
- **Checkpoints are shared in Postgres for multi-replica safety.** The production checkpoint store is `PostgresJsonCheckpointStore` (rows in `workflow_checkpoints`, selected by `ICheckpointStoreFactory` when `Database:Provider=postgres`): each checkpoint is an independent unique-PK row, so the two API replicas write concurrently with no exclusive lock and MVCC gives cross-pod resume. The file store (`FileSystemJsonCheckpointStore` wrapped by `ResilientCheckpointStore`) is the SQLite/dev fallback only. For that fallback, startup must stay defensive: corrupt index metadata is quarantined, and expected non-corruption failures (lock contention; shared-volume permission denial) fall back **quietly** to a per-pod directory (`ResilientCheckpointStore.Create` never throws) — at most one `warn` per store, no `fail`/stacktrace — so they do not flood the logs.
- **Application data should not use system temp.** Stable data belongs in the configured/local app data directory.

Where this lives: `apps/Agentweaver.Api/Endpoints/EndpointHelpers.cs`; `apps/Agentweaver.Api/Infrastructure/`; `apps/Agentweaver.Api/Sandbox/`.

## How to Add a New API Capability

A safe extension normally follows this sequence:

1. **Define the contract.** Add request/response DTOs or reuse existing ones only if the wire meaning is identical.
2. **Choose the state boundary.** Decide whether the capability uses provider-selected operational/EF stores, workspace files, GitHub, Kubernetes, or only in-memory state.
3. **Create or extend a service.** Put lifecycle, transaction, retry, and multi-store coordination logic outside the endpoint handler.
4. **Register dependencies.** Pick singleton/scoped/hosted/client lifetimes based on actual behavior, not convenience.
5. **Map endpoints in a feature module.** Keep route definitions together and call the module from startup.
6. **Apply auth and ownership.** Confirm both global middleware behavior and resource-specific authorization. Resource authorization should be ownership-based and must not depend on magic usernames.
7. **Add diagnostics or metrics only if they are real.** Avoid placeholder health checks and fabricated counters.
8. **Validate with the smallest relevant test/build path.** Endpoint changes should prove request binding, auth behavior, ownership behavior, and persistence effects where applicable.

The core design goal is not to make every feature small; it is to keep each responsibility in the layer that can own it cleanly.

<details id="diagram-context-api-core-fig4" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>API middleware · endpoint metadata is authority</td></tr>
<tr><td>takeaway</td><td>The request pipeline classifies endpoints before authentication; handlers enforce resource roles.</td></tr>
<tr><td>group-0-title</td><td>TRANSPORT / CLASSIFICATION</td></tr>
<tr><td>group-1-title</td><td>IDENTITY / RESOURCE</td></tr>
<tr><td>Forwarded headers</td><td>Forwarded headers</td></tr>
<tr><td>Forwarded headers</td><td>Then exception handler</td></tr>
<tr><td>Forwarded headers</td><td>Normalize proxy context; map unhandled errors</td></tr>
<tr><td>Forwarded headers</td><td>Program.cs:1266–1273</td></tr>
<tr><td>Routing → CORS</td><td>Routing → CORS</td></tr>
<tr><td>Routing → CORS</td><td>Then rate limiter</td></tr>
<tr><td>Routing → CORS</td><td>Route selection precedes endpoint metadata checks</td></tr>
<tr><td>Routing → CORS</td><td>Program.cs:1274–1276</td></tr>
<tr><td>Endpoint integrity</td><td>Endpoint integrity</td></tr>
<tr><td>Endpoint integrity</td><td>Classified authorization metadata</td></tr>
<tr><td>Endpoint integrity</td><td>Unclassified application endpoint fails closed</td></tr>
<tr><td>Endpoint integrity</td><td>EndpointAuthorization:139–174</td></tr>
<tr><td>Authentication</td><td>Authentication</td></tr>
<tr><td>Authentication</td><td>Validate selected credential</td></tr>
<tr><td>Authentication</td><td>Entra tenant check lives inside token validation</td></tr>
<tr><td>Authentication</td><td>Program.cs:1278</td></tr>
<tr><td>Unmatched endpoint</td><td>Unmatched endpoint</td></tr>
<tr><td>Unmatched endpoint</td><td>Explicit unmatched-route handling</td></tr>
<tr><td>Unmatched endpoint</td><td>Runs after authentication, before authorization</td></tr>
<tr><td>Unmatched endpoint</td><td>Program.cs:1279</td></tr>
<tr><td>Authorization</td><td>Authorization</td></tr>
<tr><td>Authorization</td><td>Endpoint policy evaluation</td></tr>
<tr><td>Authorization</td><td>Anonymous operational endpoints are classified</td></tr>
<tr><td>Authorization</td><td>Program.cs:1280</td></tr>
<tr><td>Endpoint handler</td><td>Endpoint handler</td></tr>
<tr><td>Endpoint handler</td><td>Bind, validate, load resource</td></tr>
<tr><td>Endpoint handler</td><td>Ordering varies; apply project / resource role</td></tr>
<tr><td>Endpoint handler</td><td>RunEndpoints:429–445</td></tr>
<tr><td>Service → DTO / result</td><td>Service → DTO / result</td></tr>
<tr><td>Service → DTO / result</td><td>Delegate governed work</td></tr>
<tr><td>Service → DTO / result</td><td>Return the handler result to caller</td></tr>
<tr><td>Service → DTO / result</td><td>Program.cs:1282–1295</td></tr>
<tr><td>Forwarded headers</td><td>next</td></tr>
<tr><td>Routing → CORS</td><td>endpoint</td></tr>
<tr><td>Endpoint integrity</td><td>valid</td></tr>
<tr><td>Unmatched endpoint</td><td>matched</td></tr>
<tr><td>Authorization</td><td>allowed</td></tr>
<tr><td>Endpoint handler</td><td>map</td></tr>
<tr><td>scope</td><td>Request lane only: startup migration/recovery is separate. Worker role exposes probes, not this app surface.</td></tr>
<tr><td>groups</td><td>TRANSPORT / CLASSIFICATION; IDENTITY / RESOURCE</td></tr>
</tbody></table>
</details>

<details id="diagram-context-api-core-fig6" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>System diagnostics · a narrow live check map</td></tr>
<tr><td>takeaway</td><td>The protected system snapshot combines checks and counts; it is not the detailed-health surface.</td></tr>
<tr><td>group-0-title</td><td>SYSTEM CHECKS</td></tr>
<tr><td>group-1-title</td><td>STATUS / COUNTS</td></tr>
<tr><td>Diagnostics request</td><td>Diagnostics request</td></tr>
<tr><td>Diagnostics request</td><td>Protected endpoint</td></tr>
<tr><td>Diagnostics request</td><td>DiagnosticsService.GetSystemDiagnosticsAsync</td></tr>
<tr><td>Diagnostics request</td><td>DiagnosticsEndpoints:52</td></tr>
<tr><td>Diagnostics service</td><td>Diagnostics service</td></tr>
<tr><td>Diagnostics service</td><td>Ordered live checks</td></tr>
<tr><td>Diagnostics service</td><td>Capture generated time and total duration</td></tr>
<tr><td>Diagnostics service</td><td>DiagnosticsService:89–136</td></tr>
<tr><td>SQLite + data directory</td><td>SQLite + data directory</td></tr>
<tr><td>SQLite + data directory</td><td>Reachability / write probe</td></tr>
<tr><td>SQLite + data directory</td><td>SQLite check is not universal PG health proof</td></tr>
<tr><td>SQLite + data directory</td><td>DiagnosticsService:1192–1230</td></tr>
<tr><td>Built-in definitions</td><td>Built-in definitions</td></tr>
<tr><td>Built-in definitions</td><td>Workflow + review-policy checks</td></tr>
<tr><td>Built-in definitions</td><td>Report registry availability, not a live run</td></tr>
<tr><td>Built-in definitions</td><td>DiagnosticsService:97–103</td></tr>
<tr><td>Heartbeat + project store</td><td>Heartbeat + project store</td></tr>
<tr><td>Heartbeat + project store</td><td>Status and project-store read</td></tr>
<tr><td>Heartbeat + project store</td><td>Report coordinator heartbeat service state</td></tr>
<tr><td>GitHub CLI / auth</td><td>GitHub CLI / auth</td></tr>
<tr><td>GitHub CLI / auth</td><td>Local CLI diagnostic surface</td></tr>
<tr><td>GitHub CLI / auth</td><td>Do not equate CLI auth with platform sign-in</td></tr>
<tr><td>GitHub CLI / auth</td><td>DiagnosticsService:1292+</td></tr>
<tr><td>Counts + pod quota</td><td>Counts + pod quota</td></tr>
<tr><td>Counts + pod quota</td><td>Provider-aware run/project counts</td></tr>
<tr><td>Counts + pod quota</td><td>Quota optional; missing/read failure → unknown</td></tr>
<tr><td>Counts + pod quota</td><td>DiagnosticsService:105–138</td></tr>
<tr><td>SystemDiagnosticsDto</td><td>SystemDiagnosticsDto</td></tr>
<tr><td>SystemDiagnosticsDto</td><td>Checks / details / durations</td></tr>
<tr><td>SystemDiagnosticsDto</td><td>API version, uptime, totals and generatedUtc</td></tr>
<tr><td>SystemDiagnosticsDto</td><td>DiagnosticsService:116–134</td></tr>
<tr><td>Diagnostics request</td><td>collect</td></tr>
<tr><td>Diagnostics service</td><td>check</td></tr>
<tr><td>Diagnostics service</td><td>counts</td></tr>
<tr><td>Counts + pod quota</td><td>summary</td></tr>
<tr><td>scope</td><td>Detailed health separately checks PostgreSQL, Key Vault, warm pool and Kubernetes; probes are cheaper.</td></tr>
<tr><td>groups</td><td>SYSTEM CHECKS; STATUS / COUNTS</td></tr>
</tbody></table>
</details>

<details id="diagram-context-canonical-api-host" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>API host: compose once, authorize each request</td></tr>
<tr><td>takeaway</td><td>Startup prepares the host. Requests never flow through migrations or recovery.</td></tr>
<tr><td>Startup composition</td><td>BOOTSTRAP</td></tr>
<tr><td>request-group-title</td><td>PER REQUEST</td></tr>
<tr><td>classification-guard</td><td>Unclassified endpoint? Fail closed.</td></tr>
<tr><td>Startup composition</td><td>Startup composition</td></tr>
<tr><td>Startup composition</td><td>Configuration and service registration</td></tr>
<tr><td>Startup composition</td><td>Provider selection, migrations, recovery, workspace checks</td></tr>
<tr><td>Startup composition</td><td>Program.cs:1200-1254</td></tr>
<tr><td>Role-specific HTTP host</td><td>Role-specific HTTP host</td></tr>
<tr><td>Role-specific HTTP host</td><td>Web API or worker probes</td></tr>
<tr><td>Role-specific HTTP host</td><td>The worker does not expose application endpoint groups</td></tr>
<tr><td>Role-specific HTTP host</td><td>Program.cs:1255-1326</td></tr>
<tr><td>Client request</td><td>Client request</td></tr>
<tr><td>Client request</td><td>Browser / MCP adapter / automation</td></tr>
<tr><td>Client request</td><td>MCP forwards a validated broker token, not GitHub auth</td></tr>
<tr><td>Client request</td><td>AgentweaverApiClient.cs:359</td></tr>
<tr><td>Cross-cutting policy</td><td>Cross-cutting policy</td></tr>
<tr><td>Cross-cutting policy</td><td>Integrity, authentication, authorization</td></tr>
<tr><td>Cross-cutting policy</td><td>Routing / CORS / rate limiting precede identity checks</td></tr>
<tr><td>Cross-cutting policy</td><td>Program.cs:1274-1295</td></tr>
<tr><td>Application services</td><td>Application services</td></tr>
<tr><td>Application services</td><td>Own operation semantics</td></tr>
<tr><td>Application services</td><td>Coordinate lifecycle, transactions, and external effects</td></tr>
<tr><td>Application services</td><td>Endpoint modules -&gt; services</td></tr>
<tr><td>Endpoint adapter</td><td>Endpoint adapter</td></tr>
<tr><td>Endpoint adapter</td><td>Bind, load, check resource role</td></tr>
<tr><td>Endpoint adapter</td><td>Viewer / Contributor / Owner; return a safe DTO</td></tr>
<tr><td>Endpoint adapter</td><td>ProjectAuthorization.cs:56-86</td></tr>
<tr><td>Provider-selected stores</td><td>Provider-selected stores</td></tr>
<tr><td>Provider-selected stores</td><td>Production Postgres / local SQLite</td></tr>
<tr><td>Provider-selected stores</td><td>Fresh context or connection per operation</td></tr>
<tr><td>Provider-selected stores</td><td>Program.cs:1026-1075</td></tr>
<tr><td>Workspace and integrations</td><td>Workspace and integrations</td></tr>
<tr><td>Workspace and integrations</td><td>Git, AgentHost, model-provider adapters</td></tr>
<tr><td>Workspace and integrations</td><td>Files and execution remain separate from database rows</td></tr>
<tr><td>Workspace and integrations</td><td>Explicit capability boundaries</td></tr>
<tr><td>Startup composition</td><td>prepares</td></tr>
<tr><td>request-policy</td><td>request</td></tr>
<tr><td>policy-handler</td><td>dispatch</td></tr>
<tr><td>Endpoint adapter</td><td>delegate</td></tr>
<tr><td>service-store</td><td>read / write</td></tr>
<tr><td>service-adapters</td><td>invoke</td></tr>
<tr><td>store-result</td><td>rows</td></tr>
<tr><td>service-result</td><td>result</td></tr>
<tr><td>Endpoint adapter</td><td>DTO / HTTP response</td></tr>
<tr><td>scope</td><td>Worker HTTP: healthz / readyz only. Background registrations are not necessarily worker-exclusive.</td></tr>
</tbody></table>
</details>

<details id="diagram-context-canonical-durable-event-stream-sequence" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>notes</td><td>LOOP · repeat durable reads; idle wait = 250 ms; Drain the whole batch before terminal close. Retryable assembly_blocked is not terminal.; Explicit-sequence reuse is idempotent only for matching type/payload. SQLite live channels are a separate lane.</td></tr>
</tbody></table>
</details>

<details id="diagram-context-canonical-provider-admission" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Accept a provider before invoking it</td></tr>
<tr><td>takeaway</td><td>Signed admission context freezes execution choice; live capability checks remain separate.</td></tr>
<tr><td>group-title0</td><td>PREPARE AND ACCEPT</td></tr>
<tr><td>group-title1</td><td>RUN BOUNDARY AND LIVE FENCES</td></tr>
<tr><td>Prepare context</td><td>Prepare context</td></tr>
<tr><td>Prepare context</td><td>Resolve effective provider</td></tr>
<tr><td>Prepare context</td><td>Bind operation + project</td></tr>
<tr><td>Prepare context</td><td>Bind subject + provider key</td></tr>
<tr><td>Prepare context</td><td>Signed • expires in 5 min</td></tr>
<tr><td>Accept request</td><td>Accept request</td></tr>
<tr><td>Accept request</td><td>Re-resolve and compare</td></tr>
<tr><td>Accept request</td><td>Verify signature + expiry</td></tr>
<tr><td>Accept request</td><td>Reject mismatched context</td></tr>
<tr><td>Accept request</td><td>Replacement context on error</td></tr>
<tr><td>Accepted plan</td><td>Accepted plan</td></tr>
<tr><td>Accepted plan</td><td>One execution provider</td></tr>
<tr><td>Accepted plan</td><td>Freeze BYOK configuration</td></tr>
<tr><td>Accepted plan</td><td>Provider choice is immutable</td></tr>
<tr><td>Accepted plan</td><td>Copilot OR BYOK</td></tr>
<tr><td>Run snapshot</td><td>Run snapshot</td></tr>
<tr><td>Run snapshot</td><td>Private durable ownership</td></tr>
<tr><td>Run snapshot</td><td>Database owner → secret ref</td></tr>
<tr><td>Run snapshot</td><td>Secret store holds snapshot</td></tr>
<tr><td>Run snapshot</td><td>Child / retry inheritance</td></tr>
<tr><td>Invocation guard</td><td>Invocation guard</td></tr>
<tr><td>Invocation guard</td><td>Check accepted run boundary</td></tr>
<tr><td>Invocation guard</td><td>Match operation and provider</td></tr>
<tr><td>Invocation guard</td><td>Reject inconsistent execution</td></tr>
<tr><td>Invocation guard</td><td>No silent provider fallback</td></tr>
<tr><td>Capability fences</td><td>Capability fences</td></tr>
<tr><td>Capability fences</td><td>Separate live permission checks</td></tr>
<tr><td>Capability fences</td><td>Before / after mint or read</td></tr>
<tr><td>Capability fences</td><td>Reject revoked or changed grant</td></tr>
<tr><td>Capability fences</td><td>Snapshot is not a bypass</td></tr>
<tr><td>relation-0</td><td>1 signed context</td></tr>
<tr><td>relation-1</td><td>2 match</td></tr>
<tr><td>relation-2</td><td>3 capture</td></tr>
<tr><td>relation-3</td><td>4 load boundary</td></tr>
<tr><td>relation-4</td><td>5 Copilot capability</td></tr>
<tr><td>assurance</td><td>Mismatch rejects with replacement context. A frozen provider snapshot does not bypass live GitHub capability fences.</td></tr>
<tr><td>assurance-0-label</td><td>Prepared key</td></tr>
<tr><td>assurance-0-fact</td><td>Five minutes; operation / subject bound.</td></tr>
<tr><td>assurance-0-source</td><td>AiExecutionPlanService.cs</td></tr>
<tr><td>assurance-1-label</td><td>Private snapshot</td></tr>
<tr><td>assurance-1-fact</td><td>DB ownership points to secret storage.</td></tr>
<tr><td>assurance-1-source</td><td>RunModelProviderSnapshotStore.cs</td></tr>
<tr><td>assurance-2-label</td><td>Live capability</td></tr>
<tr><td>assurance-2-fact</td><td>Recheck before and after mint / read.</td></tr>
<tr><td>assurance-2-source</td><td>GitHubCapabilityBroker.cs</td></tr>
<tr><td>n0</td><td>Bind operation + project; Bind subject + provider key</td></tr>
<tr><td>n1</td><td>Verify signature + expiry; Reject mismatched context</td></tr>
<tr><td>n2</td><td>Freeze BYOK configuration; Provider choice is immutable</td></tr>
<tr><td>n3</td><td>Database owner → secret ref; Secret store holds snapshot</td></tr>
<tr><td>n4</td><td>Match operation and provider; Reject inconsistent execution</td></tr>
<tr><td>n5</td><td>Before / after mint or read; Reject revoked or changed grant</td></tr>
<tr><td>groups</td><td>PREPARE AND ACCEPT; RUN BOUNDARY AND LIVE FENCES</td></tr>
</tbody></table>
</details>
