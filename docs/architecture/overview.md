# Foundation architecture

The v1 source builds independent .NET components, including unpublished Knowledge
and Events & Sessions service candidates, Environment and Orchestrator candidates,
and the Orchestrator Core AGT YAML Policy adapter. Environment owns lifecycle,
egress intent, workspace-volume orchestration, and Sandbox leases; its Azure Files
CSI adapter provisions or releases generation-pinned claims, while the selected
Sandbox provider mounts the exact attached PVC. These are source
candidates, not deployed services. The native journal includes typed, redacted
PolicyEvaluation evidence but rejects generic writes without trusted Core-writer
provenance. The event and policy adapter are not action grants. Orchestrator Core's action guard
checks current grants and awaits a durable receipt acknowledgment; Events admits
PolicyEvaluation only from immutable Orchestrator receipts after current owner
validation. Downstream protected-effect call-site wiring is not claimed. Provider,
journal, addressed-message, and Storage contracts stay separate from the Azure,
PostgreSQL, Kubernetes, and AGT adapters and services.

The Identity Broker is the host for caller authentication and secret-redemption authorization. It constructs the Key Vault backend and the authorization wrapper; it is a service host in source, not a claim that a service is deployed.

The Foundation Probe resolves provider descriptors and pins binding evidence for acceptance checks. The IDs `azure-blob`, `azure-key-vault`, and `azure-monitor` identify catalog entries; they do not instantiate adapters. `AzureProbeOperations` separately constructs the Key Vault and Blob classes, while `Program` registers telemetry composition. The probe is not an agent runtime.

Projects & Config loads the catalog owner's meter-source selections and uses the
existing resolver to persist Cost candidates through its native run-selection
routes. Selection alone does not authorize an SDK producer.
The runtime pipeline separately binds actual SDK facts to a current registration
and purpose-bound Identity credential.
Orchestrator commits immutable source receipts before Events fetches and prices them.
Events uses the existing keyed Cost resolver and append-only PostgreSQL accounting.

The runtime library now creates an auth-first native SDK session from a delivered
configure nonce and a current registration. The registration source compares the
confirmed work item and active turn with the current Environment lease and registered
profile. The canonical Environment manager retains its lease transaction during
the internal profile callback. Public write gates still reject run-bound tokens.
The separate read-only path uses current `ReadRunSelection` without new roles or claims.
The local combined harness exercises SDK source persistence and reference-only accounting.
It does not prove deployed AgentHost scheduling or paid model execution.

## Canonical component overview

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'" alt="Structural view of unpublished Knowledge, Events & Sessions, Environment, and Orchestrator candidates; Orchestrator/Core Policy adapters and owner flow; journal evidence and addressed-message delivery; Azure Files/Kubernetes, Azure Blob, and Key Vault adapters. Mapped session context is checked against current owner state and authority, not proof of SDK execution or accounting; public Environment run-bound enrollment remains denied and internal lookup is read-only. The journal rejects untrusted PolicyEvaluation writes. Source structure, not deployment topology." />
  </a>
  <figcaption>Direct project references and adapter/resource relationships in the v1 source; Events & Sessions, Environment, and Orchestrator are unpublished service candidates, not a deployed topology. The Orchestrator owner flow is shown in the <a href="./events-sessions">Sessions journal diagram</a>. Host composition and Foundation Probe registration IDs are listed below.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.drawio'">Open editable draw.io source</a></p>

The figure is a structural component view, not runtime request order or deployment topology.
`Agentweaver.EventsAndSessions` references the shared contracts, provider catalog/resolver,
PostgreSQL outbox/inbox library, and telemetry helper. `Agentweaver.Environment` references the shared
contracts, provider catalog/resolver, Azure Files Storage provider, and Agent Sandbox provider; its egress flow and evidence boundary
are described in [Environment egress](./environment-egress.md), and the Sandbox lease contract is described in
[Environment Sandbox](./environment-sandbox.md).
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
| `Agentweaver.Orchestrator.Core` | Workflow validation and the platform-singleton AGT YAML Policy adapter. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Microsoft.AgentGovernance` |
| `Agentweaver.Identity` | Trusted actor, exact secret grants, separate runtime nonce contracts, and fixed owner HTTP transport. | `Agentweaver.Abstractions` |
| `Agentweaver.AgentRuntime` | Authenticated bootstrap, guarded native turns/tools, actual turn content, accounting acknowledgment, and compatible SDK-cache or journal recovery. | `Agentweaver.Abstractions`, `Agentweaver.Identity`, `GitHub.Copilot.SDK` |
| `Agentweaver.AgentHost` | Unpublished HTTPS runtime executable, exact image/native-runtime verification, priority at idle boundaries, and measured readiness. No automatic scheduler. | `Agentweaver.AgentRuntime`, `Agentweaver.Telemetry` |
| `Agentweaver.Secrets.AzureKeyVault` | Exact-version Azure Key Vault adapter. | `Agentweaver.Abstractions` |
| `Agentweaver.Persistence.Postgres` | Service-schema outbox, consumer inbox, and relay library. | — |
| `Agentweaver.ObjectStore.AzureBlob` | Opaque-object Azure Blob adapter. | `Agentweaver.Abstractions` |
| `Agentweaver.Telemetry` | In-process OpenTelemetry traces, metrics, and logs. | — |
| `Agentweaver.Telemetry.AzureMonitor` | Opt-in Azure Monitor exporters. | `Agentweaver.Telemetry` |
| `Agentweaver.Identity.Broker` | OAuth, secret-redemption, and Identity-owned GitHub App connection/token-mint host; caller-authentication boundary. | `Agentweaver.Identity`, `Agentweaver.Secrets.AzureKeyVault`, `Agentweaver.SourceControl` |
| `Agentweaver.EventsAndSessions` | Native journal, addressed messages, reference-only SDK usage admission, immutable Cost pricing, and exact usage totals. Unpublished host candidate. | `Agentweaver.Abstractions`, `Agentweaver.Identity`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Telemetry` |
| `Agentweaver.Knowledge` | Project-scoped Memory API, context compiler, native PostgreSQL default, and optional Cosmos and Redis adapter candidates. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Telemetry` |
| `Agentweaver.Environment` | Environment-owned egress, workspace volumes, Sandbox leases, registered runtime profiles, and authenticated nonce delivery; not deployed. | `Agentweaver.Abstractions`, `Agentweaver.Identity`, `Agentweaver.Providers`, `Agentweaver.Providers.Storage.AzureFiles`, `Agentweaver.Providers.Sandbox.AgentSandbox` |
| `Agentweaver.Orchestrator` | HTTP owner for session trees, forks, recovery, turn boundaries, runtime registration, immutable SDK source receipts, message outbox, and parent notifications. It checks current Projects authority. | `Agentweaver.Abstractions`, `Agentweaver.Identity`, `Agentweaver.Orchestrator.Core`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres` |
| `Agentweaver.FoundationProbe` | Acceptance-only infrastructure probe executable. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Secrets.AzureKeyVault`, `Agentweaver.ObjectStore.AzureBlob`, `Agentweaver.Telemetry.AzureMonitor` |

The Events & Sessions project owns the native PostgreSQL journal and addressed-message
delivery store. The Orchestrator validates owner outbox messages and session bindings
before admission, presentation, and acknowledgment. Mapped spawn validates the latest
confirmed root decision under the owner lock; runtime-owner-context returns mapped
agent/model/turn metadata only while the child row, decision, and current Projects
authority are unchanged. These source candidates have no
deployment, background delivery relay, or automatic AgentHost scheduler. Their
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

The [AgentHost source candidate](agenthost.md) includes a pinned image definition and authenticated runtime routes.
Its local fixtures and CI image receipt do not prove deployed scheduling or paid model execution.
The repository does not contain a published platform image.

The Knowledge source candidate and its current native Memory boundary are described in
the [Knowledge and Memory reference](knowledge-memory.md).

The [proposed platform architecture](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0001-platform-architecture.md) remains a Proposed source document.
