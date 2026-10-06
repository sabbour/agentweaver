# Foundation architecture

The v1 source builds independent .NET components, including unpublished Knowledge and
Events & Sessions service candidates, an Environment egress adapter, and an Orchestrator
Core AGT YAML Policy adapter. The native journal defines typed, redacted PolicyEvaluation
evidence but rejects generic writes without trusted Core-writer provenance. The Policy
adapter is not an action grant or a wired protected-effect guard. Contracts separate
provider-neutral types from Azure, PostgreSQL, and AGT adapters and services.

The Identity Broker is the host for caller authentication and secret-redemption authorization. It constructs the Key Vault backend and the authorization wrapper; it is a service host in source, not a claim that a service is deployed.

The Foundation Probe resolves provider descriptors and pins binding evidence for acceptance checks. The IDs `azure-blob`, `azure-key-vault`, and `azure-monitor` identify catalog entries; they do not instantiate adapters. `AzureProbeOperations` separately constructs the Key Vault and Blob classes, while `Program` registers telemetry composition. The probe is not an agent runtime.

Projects & Config loads the catalog owner's meter-source selections and uses the
existing resolver to persist Cost candidates through its native run-selection
routes. This is source-keyed selection, not SDK model/producer authority, resource
negotiation, a pricing adapter, or usage ingestion.

## Canonical component overview

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'" alt="Structural view of the unpublished Knowledge and Events & Sessions service candidates, Environment egress and Orchestrator Core Policy adapters, and direct project references, alongside Azure Blob and Key Vault adapter chains." />
  </a>
  <figcaption>Direct project references and adapter/resource relationships in the v1 source; service candidates and adapter libraries are not a deployed topology. Host composition and Foundation Probe registration IDs are listed below.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.drawio'">Open editable draw.io source</a></p>

The figure is a structural component view, not runtime request order or deployment topology.
`Agentweaver.EventsAndSessions` references the shared contracts, provider catalog/resolver,
PostgreSQL outbox/inbox library, and telemetry helper. The Environment egress candidate
references the shared contracts and provider catalog/resolver; its flow and evidence boundary
are described in [Environment egress](./environment-egress.md). `AzureBlobObjectStore` implements
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
| Identity Broker | Constructs `AzureKeyVaultSecretRedemption` as an `ISecretRedemption` backend and constructs `AuthorizedSecretRedemption` with the grant authority and backend. | The wrapper checks actor, project, run, purpose, and exact-version grants before backend redemption. The adapter does not authorize callers. |
| Foundation Probe | `ProbeProviderBindings.ResolveAndPin` records provider IDs in pin evidence. Separately, `AzureProbeOperations` constructs `AzureBlobObjectStore` and `AzureKeyVaultSecretRedemption`; `Program` registers telemetry composition. | ID-based pinning does not construct adapters; the probe is an acceptance executable, not an application service host. |
| `azure-blob` | ObjectStore provider ID recorded in Foundation Probe binding evidence. | Registration ID, not the `AzureBlobObjectStore` class, library, or constructor. |
| `azure-key-vault` | SecretRedemption provider ID recorded in Foundation Probe binding evidence. | Registration ID, not the `AzureKeyVaultSecretRedemption` class, library, or constructor. |
| `azure-monitor` | Telemetry provider ID recorded in Foundation Probe binding evidence. | Registration ID, not an exporter class, library, or model vendor. |

## Component boundaries and project references

| Component | Current boundary | Direct project references |
| --- | --- | --- |
| `Agentweaver.Abstractions` | Provider, secret, object-store, environment/network-policy, authorization-context, run-selection, and Memory contracts. | — |
| `Agentweaver.Providers` | In-memory provider catalog and resolver. | `Agentweaver.Abstractions` |
| `Agentweaver.Orchestrator.Core` | Workflow validation and the platform-singleton AGT YAML Policy adapter. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Microsoft.AgentGovernance` |
| `Agentweaver.Identity` | Trusted actor and exact run-grant authorization for secret redemption. | `Agentweaver.Abstractions` |
| `Agentweaver.Secrets.AzureKeyVault` | Exact-version Azure Key Vault adapter. | `Agentweaver.Abstractions` |
| `Agentweaver.Persistence.Postgres` | Service-schema outbox, consumer inbox, and relay library. | — |
| `Agentweaver.ObjectStore.AzureBlob` | Opaque-object Azure Blob adapter. | `Agentweaver.Abstractions` |
| `Agentweaver.Telemetry` | In-process OpenTelemetry traces, metrics, and logs. | — |
| `Agentweaver.Telemetry.AzureMonitor` | Opt-in Azure Monitor exporters. | `Agentweaver.Telemetry` |
| `Agentweaver.Identity.Broker` | OAuth and secret-redemption host; caller-authentication boundary. | `Agentweaver.Identity`, `Agentweaver.Secrets.AzureKeyVault` |
| `Agentweaver.EventsAndSessions` | PostgreSQL-backed native Sessions journal and HTTP host candidate. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Telemetry` |
| `Agentweaver.Environment` | Environment-owned egress intent compiler and Cilium adapter library; not a running host. | `Agentweaver.Abstractions`, `Agentweaver.Providers` |
| `Agentweaver.Knowledge` | Project-scoped Memory API, context compiler, and native PostgreSQL adapter candidate. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Telemetry` |
| `Agentweaver.FoundationProbe` | Acceptance-only infrastructure probe executable. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Secrets.AzureKeyVault`, `Agentweaver.ObjectStore.AzureBlob`, `Agentweaver.Telemetry.AzureMonitor` |

The Events & Sessions project and its contracts are described in the [journal service
reference](events-sessions.md). The repository does not contain the AgentHost, product
API, web UI, product MCP server, or application router. It does not contain a published
platform image.

The Knowledge source candidate and its current native Memory boundary are described in
the [Knowledge and Memory reference](knowledge-memory.md).

The [proposed platform architecture](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0001-platform-architecture.md) remains a Proposed source document.
