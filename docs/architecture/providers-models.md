# Providers and models

`Agentweaver.Abstractions` names 15 provider seams. A seam defines a contract boundary. It does not prove that an adapter or running service exists.

`ProviderDescriptor` records the seam, provider ID, adapter version, options-schema version, hosting pattern, and advertised capabilities. It contains no option values or credentials.

`ProviderCatalog.Create` validates registrations, defaults, allowed overrides, ordered sets, network-policy layers, and meter-keyed Cost selections. `ProviderResolver` checks cardinality, versions, options schemas, and capabilities.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.png'" alt="Foundation Probe flow: ProbeProviderBindings supplies existing target resource IDs and generation. ProviderCatalog.Create builds the catalog; ProviderResolver resolves candidates and pins immutable bindings against supplied ResourceNegotiation. No provisioning, live negotiation, adapter construction, or product AgentHost is shown." />
  </a>
  <figcaption>The Probe supplies existing target IDs, generation, and ResourceNegotiation. The resolver selects and pins against those values; it does not provision or live-negotiate a resource.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.drawio'">Open editable draw.io source</a></p>

| Cardinality | Seams | Resolver status |
| --- | --- | --- |
| Exclusive | Sessions, Snapshots, Sandbox, Storage, Memory, Source Control | `Resolve` and `Pin` support this form. Snapshots can resolve to explicit `None`. |
| Platform singleton | Policy, Secrets, Messaging, Object Store | `Resolve` and `Pin` support this form. Projects cannot replace the provider. |
| Ordered composite | Guardrails, Telemetry | `ResolveOrdered` and `PinOrdered` preserve the selected order. |
| Layered | Network Policy | `ResolveNetworkPolicy` and `PinNetworkPolicy` support required L3/L4 and optional L7 layers. Pinning requires a matching applied intent generation. |
| Keyed by meter source | Cost | `ResolveCost` selects one enabled provider per explicit source. Projects & Config persists candidates, not negotiated resource bindings. |
| Per application | Application Hosting | Not implemented. |

Pinning records provider identity, adapter version, options revision, resource generation, and negotiated capabilities. It does not provision a resource or enforce policy.

The current Events & Sessions host resolves the exclusive native PostgreSQL Sessions
provider through this catalog/resolver, negotiates the configured database, then pins
the effective binding when a run's first session is created. It stores the exact provider
ID, adapter version, options schema and revision, resource ID and generation, and
capabilities against the project/run. Later append, replay, and live-subscription
requests verify that exact binding; a changed or missing provider fails closed instead
of falling back to a new default. The service emits bounded pin evidence through the
existing telemetry helper without recording option values, credentials, or raw resource
IDs. See the [journal service reference](events-sessions.md).

The Foundation Probe registers `azure-key-vault`, `azure-blob`, and `azure-monitor` descriptors for its checks. These registrations do not form a product provider catalog.

Model is not a provider seam. The v1 source contains no AgentHost, model resolver, or model adapter. It does not declare support for a model vendor.

A Cost source key and an opaque model-selection reference are not trusted SDK
provenance. The native Projects run-selection routes retain the source key,
adapter/options versions, options revision and capabilities without claiming
pricing or admitting usage writes.
