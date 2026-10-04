# Foundation architecture

The v1 source builds independent .NET components. Contracts separate provider-neutral types from Azure and PostgreSQL adapters.

The Identity Broker is the host for caller authentication and secret-redemption authorization. It constructs the Key Vault backend and the authorization wrapper; it is a service host in source, not a claim that a service is deployed.

The Foundation Probe resolves provider descriptors and pins binding evidence for acceptance checks. The IDs `azure-blob`, `azure-key-vault`, and `azure-monitor` identify catalog entries; they do not instantiate adapters. `AzureProbeOperations` separately constructs the Key Vault and Blob classes, while `Program` registers telemetry composition. The probe is not an agent runtime.

## Canonical component overview

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'" alt="Structural view of two adapter chains. IObjectStore is implemented by AzureBlobObjectStore, which accesses an Azure Blob container. ISecretRedemption is implemented by AzureKeyVaultSecretRedemption, which accesses Azure Key Vault." />
  </a>
  <figcaption>Contract, adapter-library, and external-resource relationships in the v1 source. Host composition and Foundation Probe registration IDs are listed below.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.drawio'">Open editable draw.io source</a></p>

The figure is a structural component view, not runtime request order or deployment topology. `AzureBlobObjectStore` implements `IObjectStore` in the `Agentweaver.ObjectStore.AzureBlob` library. `AzureKeyVaultSecretRedemption` implements `ISecretRedemption` in the `Agentweaver.Secrets.AzureKeyVault` library. The table names the concrete types and their external boundaries.

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
| `Agentweaver.Abstractions` | Provider, secret, and object-store contracts. | — |
| `Agentweaver.Providers` | In-memory provider catalog and resolver. | `Agentweaver.Abstractions` |
| `Agentweaver.Identity` | Trusted actor and exact run-grant authorization for secret redemption. | `Agentweaver.Abstractions` |
| `Agentweaver.Secrets.AzureKeyVault` | Exact-version Azure Key Vault adapter. | `Agentweaver.Abstractions` |
| `Agentweaver.Persistence.Postgres` | Service-schema outbox, consumer inbox, and relay library. | — |
| `Agentweaver.ObjectStore.AzureBlob` | Opaque-object Azure Blob adapter. | `Agentweaver.Abstractions` |
| `Agentweaver.Telemetry` | In-process OpenTelemetry traces, metrics, and logs. | — |
| `Agentweaver.Telemetry.AzureMonitor` | Opt-in Azure Monitor exporters. | `Agentweaver.Telemetry` |
| `Agentweaver.Identity.Broker` | OAuth and secret-redemption host; caller-authentication boundary. | `Agentweaver.Identity`, `Agentweaver.Secrets.AzureKeyVault` |
| `Agentweaver.FoundationProbe` | Acceptance-only infrastructure probe executable. | `Agentweaver.Abstractions`, `Agentweaver.Providers`, `Agentweaver.Persistence.Postgres`, `Agentweaver.Secrets.AzureKeyVault`, `Agentweaver.ObjectStore.AzureBlob`, `Agentweaver.Telemetry.AzureMonitor` |

The repository does not contain the AgentHost, product API, web UI, product MCP server, or application router. It does not contain a published platform image.

The [proposed platform architecture](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0001-platform-architecture.md) remains a Proposed source document.
