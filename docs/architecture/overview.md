# Foundation architecture

The v1 source builds independent .NET components. Contracts separate provider-neutral types from Azure and PostgreSQL adapters.

The Identity broker candidate composes the Identity authorization library, its owned PostgreSQL schema, and the Key Vault adapter. It is not a deployed service.

The Foundation Probe composes the provider resolver, PostgreSQL, Key Vault, Blob, and Azure Monitor adapters for acceptance checks. It is not an agent runtime.

<figure class="aw-diagram">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'" alt="Foundation dependency diagram. Agentweaver abstractions feed provider, identity, Key Vault, and Blob libraries. Azure Monitor extends telemetry. The Identity broker and Foundation Probe depend on the libraries they compose." />
  </a>
  <figcaption>Compiled component dependencies in the v1 source. The diagram shows library references, not deployed service traffic.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-foundation-dependencies.drawio'">Open editable draw.io source</a></p>

| Component | Current boundary |
| --- | --- |
| `Agentweaver.Abstractions` | Provider, secret, and object-store contracts. |
| `Agentweaver.Providers` | In-memory provider catalog and resolver. |
| `Agentweaver.Identity` | Trusted actor and exact run-grant authorization for secret redemption. |
| `Agentweaver.Secrets.AzureKeyVault` | Exact-version Azure Key Vault adapter. |
| `Agentweaver.Persistence.Postgres` | Service-schema outbox and consumer inbox library. |
| `Agentweaver.ObjectStore.AzureBlob` | Opaque-object Azure Blob adapter. |
| `Agentweaver.Telemetry` | In-process OpenTelemetry traces, metrics, and logs. |
| `Agentweaver.Telemetry.AzureMonitor` | Opt-in Azure Monitor exporters. |
| `Agentweaver.Identity.Broker` | Unpublished OAuth and secret-redemption service candidate. |
| `Agentweaver.FoundationProbe` | Acceptance-only infrastructure probe executable. |

The repository does not contain the AgentHost, product API, web UI, product MCP server, or application router. It does not contain a published platform image.

The [proposed platform architecture](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0001-platform-architecture.md) remains a Proposed source document.
