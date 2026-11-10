# Foundation architecture

Agentweaver separates deciding what may run from running it. The control plane
checks current authority and records decisions. AgentHost executes model turns
in an Environment-owned Sandbox. Events & Sessions records committed history;
it does not drive execution.

Use this page to identify the owner of a capability or state change. For request
order, recovery, deployment mapping and quality scenarios, start with the
[system deep dive](../deep-dive/00-system-overview.md). These are views of
source, not an inventory of released or running services.

## Contracts, adapters and owners

A **contract** defines a capability. An **adapter** implements that contract
for a technology. A **service owner** checks authority and retains its domain
state. A provider selection chooses an eligible adapter; resource negotiation
and pinning identify the actual backend. Neither selection nor a pin grants
permission.

| Boundary | Responsibility | Not its responsibility |
| --- | --- | --- |
| Gateway/BFF | Authenticate and route a finite HTTP API for browser and native CLI callers | Approve workflow transitions or select arbitrary upstream URLs |
| Identity Broker | Sign-in, tokens, purpose-bound grants and credential redemption | Assign project roles |
| Projects & Config | Current project authority and immutable accepted run selections | Provision selected resources |
| Orchestrator and Core | Decisions, gates, session tree, execution fences and MAF checkpoints | Treat model text as approval |
| Environment | Resource lifecycle, network intent, workspace volumes and Sandbox placement | Own workflow completion |
| AgentHost and AgentRuntime | Guard native SDK turns and tools against current owner bindings | Schedule itself or choose a new model/credential |
| Events & Sessions | Ordered history, material references, addressed delivery and accounting | Replace execution state or replay effects |
| Knowledge | Authorized records and bounded context through the selected Memory provider | Replace the run journal |

Supported client paths are Gateway/API, the browser client and the native
.NET CLI. Retained inbound MCP adapter source is not a supported product MCP
surface. [Outbound remote MCP connections](./remote-mcp-connections.md) are a
different integration.

## Resource and execution boundaries

Environment owns lifecycle, egress intent, workspace-volume orchestration, and
Sandbox leases. Its Azure Files CSI adapter provisions or releases
generation-pinned claims, while the selected Sandbox provider mounts the exact
attached volume. The native journal can store typed, redacted
`PolicyEvaluation` evidence, but it rejects generic writes without trusted
Orchestrator Core provenance. Evidence is not an action grant. The action guard
checks current grants and waits for a durable receipt before a protected effect;
downstream effect-call-site wiring is not claimed.

Environment uses a separate `EnvironmentMigration` connection for EF Core
migrations. The database maintenance runner starts its migration Job with the
reviewed runtime grant plan; normal startup checks migration history and fails
while a migration is pending.

For invocation-bound creation, Environment consumes the
[current Core invocation binding](./environment-sandbox.md#p1-invocation-bound-creation)
before effects and retains its own operation/result link. Registration and
provider reservation do not establish that a resource exists. The separate
[creation-evidence read and Host consumer](../reference/p1-creation-evidence.md)
join retained records to current Pod and mount observations. These reads do not
establish a cross-service transaction or complete native acceptance; no-send,
cursor, effect and dispatch evidence remain separate requirements.

The Identity Broker hosts caller authentication and secret-redemption
authorization. It constructs the Key Vault backend and the authorization
wrapper; the backend retrieves bytes only after the wrapper checks the grant.

Its separate migration Job applies the canonical explicit runtime table grants
after migration. The runtime checks migration history with read-only access;
it does not migrate or grant itself permissions.

The Foundation Probe resolves provider descriptors and pins binding evidence for acceptance checks. The IDs `azure-blob`, `azure-key-vault`, and `azure-monitor` identify catalog entries; they do not instantiate adapters. `AzureProbeOperations` separately constructs the Key Vault and Blob classes, while `Program` registers telemetry composition. The probe is not an agent runtime.

Projects & Config loads the catalog owner's meter-source selections and uses the
existing resolver to persist Cost candidates through its native run-selection
routes. Selection alone does not authorize an SDK producer.
The runtime pipeline separately binds actual SDK facts to a current registration
and purpose-bound Identity credential.
Orchestrator commits immutable source receipts before Events fetches and prices them.
Events uses the existing keyed Cost resolver and append-only PostgreSQL accounting.

The runtime library creates an auth-first native SDK session from a delivered
configure nonce and a current registration. The registration source compares the
confirmed work item and active turn with the current Environment lease and registered
profile. The canonical Environment manager retains its lease transaction during
the internal profile callback. Public write gates still reject run-bound tokens.
The separate read-only path uses current `ReadRunSelection` without new roles or claims.
The local combined harness exercises SDK source persistence and reference-only accounting.
It does not prove deployed AgentHost scheduling or paid model execution.

Environment's [BuildTest path](environment-sandbox#buildtest-command-and-output-collection) resolves Core's immutable accepted checkpoint and persists one stable operation in existing owner-effects state.
It uses gated offline command Pods and a separate read-only output-collector Pod.
The optional server profile pins capability, images, executable allow list, resource limits, and collector mode.
Core retains MAF checkpoint and run-state ownership.
Source checks do not prove live provider effects or complete native accounting.

## Static component view

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'" alt="Structural view of Knowledge, Events & Sessions, Environment and Orchestrator source boundaries, shared contracts, and Azure Files, Blob and Key Vault adapters. Mapped context requires current owner authority. Public Environment run-bound enrollment remains denied; internal lookup is read-only. The journal rejects untrusted PolicyEvaluation writes. This is not a deployment topology." />
  </a>
  <figcaption>Direct project references and adapter/resource relationships in source. “Candidate” labels identify source components, not release or deployment status. The <a href="./events-sessions">Sessions journal diagram</a> shows the owner interaction; the host-composition table distinguishes probe registration IDs from adapters.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.drawio'">Open editable draw.io source</a></p>

The figure is a structural component view, not runtime request order or deployment topology.
`Agentweaver.EventsAndSessions` references the shared contracts, provider catalog/resolver,
PostgreSQL outbox/inbox library, and telemetry helper. `Agentweaver.Environment` references the shared contracts, provider
catalog/resolver, Azure Files Storage provider, and Agent Sandbox provider. Its
egress flow, platform egress provider, and evidence boundary are described in
[Environment egress](./environment-egress.md). Its Sandbox lease contract is
described in [Environment Sandbox](./environment-sandbox.md).
`AzureFilesCsiWorkspaceVolumeProvider` implements `IWorkspaceVolumeProvider` and calls the Kubernetes API;
it does not itself mount the volume into a Sandbox or prove data erasure. Environment reserves and releases
the exact Workspace generation around Sandbox provider use. `AzureBlobObjectStore` implements
`IObjectStore` in the `Agentweaver.ObjectStore.AzureBlob` library.
`AzureKeyVaultSecretRedemption` implements `ISecretRedemption` in the
`Agentweaver.Secrets.AzureKeyVault` library. The table names the concrete types and
their external boundaries.

| Interface | Concrete class | Library | External boundary and edge semantics |
| --- | --- | --- | --- |
| `ISecretRedemption` (`Agentweaver.Abstractions`) | `AuthorizedSecretRedemption` | `Agentweaver.Identity` | Implements the contract; checks the exact grant and delegates backend redemption through `ISecretRedemption`. |
| `ISecretRedemption` (`Agentweaver.Abstractions`) | `AzureKeyVaultSecretRedemption` | `Agentweaver.Secrets.AzureKeyVault` | Implements the backend contract; reads the exact version from Azure Key Vault. |
| `IObjectStore` (`Agentweaver.Abstractions`) | `AzureBlobObjectStore` | `Agentweaver.ObjectStore.AzureBlob` | Implements the contract; reads or writes opaque objects in Azure Blob Storage. |

### Host composition and Foundation Probe registration IDs

| Host or ID | Composition or registration | Meaning |
| --- | --- | --- |
| Identity Broker | Constructs `AzureKeyVaultSecretRedemption` as an `ISecretRedemption` backend, `AuthorizedSecretRedemption` with the grant authority and backend, and the Identity-owned GitHub App connection and token-mint routes. | The wrapper checks actor, project, run, purpose, and exact-version grants before backend redemption. GitHub App private-key redemption and repository-token minting remain inside Identity. |
| Foundation Probe | `ProbeProviderBindings.ResolveAndPin` records provider IDs in pin evidence. Separately, `AzureProbeOperations` constructs `AzureBlobObjectStore` and `AzureKeyVaultSecretRedemption`; `Program` registers telemetry composition. | ID-based pinning does not construct adapters; the probe is an acceptance executable, not an application service host. |
| `azure-blob` | ObjectStore provider ID recorded in Foundation Probe binding evidence. | Registration ID, not the `AzureBlobObjectStore` class, library, or constructor. |
| `azure-key-vault` | SecretRedemption provider ID recorded in Foundation Probe binding evidence. | Registration ID, not the `AzureKeyVaultSecretRedemption` class, library, or constructor. |
| `azure-monitor` | Telemetry provider ID recorded in Foundation Probe binding evidence. | Registration ID, not an exporter class, library, or model vendor. |

## Component boundaries and project references

| Component | Current boundary | Direct project references |
| --- | --- | --- |
| `Agentweaver.Abstractions` | Provider, journal, addressed-message, accepted-effect, secret, object-store, environment/network-policy, authorization-context, run-selection, and Memory contracts. | — |
| `Agentweaver.Providers` | In-memory provider catalog and resolver. | `Agentweaver.Abstractions` |
| `Agentweaver.Providers.Sessions.AgentSessions` | Optional, read-only AgentSessions Sessions provider and record verifier. No service references it. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Google.Protobuf` |
| `Agentweaver.Orchestrator.Core` | Workflow validation and the platform-singleton AGT YAML Policy adapter. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Microsoft.AgentGovernance` |
| `Agentweaver.Identity` | Trusted actor, exact secret grants, separate runtime nonce contracts, and fixed owner HTTP transport. | `Agentweaver.Abstractions` |
| `Agentweaver.AgentRuntime` | Authenticated bootstrap, guarded native turns/tools, actual turn content, accounting acknowledgment, and compatible SDK-cache or journal recovery. | `Agentweaver.Abstractions`, `Agentweaver.Identity`, `GitHub.Copilot.SDK` |
| `Agentweaver.AgentHost` | HTTPS runtime executable, exact image/native-runtime verification, priority at idle boundaries, measured readiness, and trusted BuildTest repository preparation/full-output collection. No automatic scheduler. | `Agentweaver.AgentRuntime`, `Agentweaver.Telemetry`, `Agentweaver.SourceControl` |
| `Agentweaver.Secrets.AzureKeyVault` | Exact-version Azure Key Vault adapter. | `Agentweaver.Abstractions` |
| `Agentweaver.Persistence.Postgres` | Service-schema outbox, consumer inbox, and relay library. | — |
| `Agentweaver.ObjectStore.AzureBlob` | Opaque-object Azure Blob adapter. | `Agentweaver.Abstractions` |
| `Agentweaver.Telemetry` | In-process OpenTelemetry traces, metrics, and logs. | — |
| `Agentweaver.Telemetry.AzureMonitor` | Opt-in Azure Monitor exporters. | `Agentweaver.Telemetry` |
| `Agentweaver.Identity.Broker` | OAuth, secret-redemption, and Identity-owned GitHub App connection/token-mint host; caller-authentication boundary. | `Agentweaver.Identity`, `Agentweaver.Secrets.AzureKeyVault`, `Agentweaver.SourceControl` |
| `Agentweaver.EventsAndSessions` | Native journal, addressed messages, reference-only SDK usage admission, immutable Cost pricing, and exact usage totals. | `Agentweaver.Abstractions`, `Agentweaver.Identity`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Telemetry` |
| `Agentweaver.Knowledge` | Project-wide discovery plus run/agent-scoped Memory API, context compiler, native PostgreSQL default, and optional Cosmos and Redis adapter candidates. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Telemetry` |
| `Agentweaver.Environment` | Environment-owned egress, workspace volumes, Sandbox leases, registered runtime profiles, and authenticated nonce delivery. | `Agentweaver.Abstractions`, `Agentweaver.Identity`, `Agentweaver.Providers`, `Agentweaver.Providers.Storage.AzureFiles`, `Agentweaver.Providers.Sandbox.AgentSandbox` |
| `Agentweaver.Orchestrator` | HTTP owner for session trees, forks, recovery, turn boundaries, runtime registration, immutable SDK source receipts, message outbox, and parent notifications. It checks current Projects authority. | `Agentweaver.Abstractions`, `Agentweaver.Identity`, `Agentweaver.Orchestrator.Core`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres` |
| `Agentweaver.FoundationProbe` | Acceptance-only infrastructure probe executable. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Secrets.AzureKeyVault`, `Agentweaver.ObjectStore.AzureBlob`, `Agentweaver.Telemetry.AzureMonitor` |

The Events & Sessions project owns the native PostgreSQL journal and addressed-message
delivery store. The Orchestrator validates owner outbox messages and session bindings
before admission, presentation, and acknowledgment. Mapped spawn validates the latest
confirmed root decision under the owner lock; runtime-owner-context returns mapped
agent/model/turn metadata only while the child row, decision, and current Projects
authority are unchanged. Delivery is request-driven: there is no
background delivery relay or automatic AgentHost scheduler. Their
contracts are described in the [journal service reference](events-sessions.md).

### Workspace-volume lifecycle

Environment owns each volume's status and separate transition, resource, and data generations. Azure Files
claims are scoped by Environment identity and resource generation; the owner pins the adapter/options
snapshot, Kubernetes target identity, and exact claim/PV release descriptor. Replace pins the verified target and records cleanup for the exact old resource in one owner
transaction. A current-fence lease retries that cleanup with the original binding. Retained resources and
shared Delete work without an authoritative reference registry remain blocked; a `Released` receipt requires
the exact claim and saved PV to be absent. This confirms Kubernetes control-plane removal, not Azure Files
data erasure. Release is rejected while the volume is bound or attached. Known preflight rejections leave the
owner record safely retryable; uncertain provider effects remain reconcilable. Azure Files does not claim
Sandbox mounting or durable flush.

## Trade-offs and limits

The [AgentHost](agenthost.md) image definition and authenticated runtime routes
do not establish live scheduling or paid model execution. The browser uses
Gateway/BFF and Identity Broker; see the [web client guide](../guide/web-client).
Work dispatch remains explicit rather than an automatic scheduler.

Service-owned transactions keep authority and state transitions together, but
remote effects require durable intents, idempotency and reconciliation. Shared
libraries reduce duplicated plumbing without becoming shared domain databases.

The Knowledge source candidate supports project-wide discovery through existing,
authorized run-provider bindings. Project-wide reads require current effective
`AccessPrivateKnowledge`; run-scoped provider resolution uses
`ReadRunSelection`. Its native Memory boundary and provider rules are described
in the [Knowledge and Memory reference](knowledge-memory.md).

## Use these views

- [Runtime and recovery](../deep-dive/orchestration.md) explains how owners
  cooperate after a decision.
- [Deployment](../deep-dive/infra-deployment.md) maps software to infrastructure
  without claiming that the topology is running.
- [Quality requirements and risks](../deep-dive/00-system-overview.md#10-quality-requirements)
  distinguish bounded source contracts from unmeasured operational goals.
- The [platform architecture ADR](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0001-platform-architecture.md)
  remains **Proposed**. Source implementation does not change its status.
