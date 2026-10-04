# Providers and models

`Agentweaver.Abstractions` names 15 provider seams. A seam defines a contract boundary. It does not prove that an adapter or running service exists.

`ProviderDescriptor` records the seam, provider ID, adapter version, options-schema version, hosting pattern, and advertised capabilities. It contains no option values or credentials.

`ProviderCatalog.Create` validates registrations, defaults, allowed overrides, ordered sets, and network-policy layers. `ProviderResolver` checks cardinality, versions, options schemas, and capabilities.

<figure class="aw-diagram">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.png'" alt="Provider flow from catalog registrations to a resolution candidate, provisioned resource negotiation, and immutable run binding. Ordered providers and network policy layers use separate resolution and pinning methods." />
  </a>
  <figcaption>Resolution returns a candidate. The caller provisions a resource and supplies negotiated capabilities before pinning.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.drawio'">Open editable draw.io source</a></p>

| Cardinality | Seams | Resolver status |
| --- | --- | --- |
| Exclusive | Sessions, Snapshots, Sandbox, Storage, Memory, Source Control | `Resolve` and `Pin` support this form. Snapshots can resolve to explicit `None`. |
| Platform singleton | Policy, Secrets, Messaging, Object Store | `Resolve` and `Pin` support this form. Projects cannot replace the provider. |
| Ordered composite | Guardrails, Telemetry | `ResolveOrdered` and `PinOrdered` preserve the selected order. |
| Layered | Network Policy | `ResolveNetworkPolicy` and `PinNetworkPolicy` support required L3/L4 and optional L7 layers. Pinning requires a matching applied intent generation. |
| Keyed by meter source | Cost | Not implemented. |
| Per application | Application Hosting | Not implemented. |

Pinning records provider identity, adapter version, options revision, resource generation, and negotiated capabilities. It does not provision a resource or enforce policy.

The Foundation Probe registers `azure-key-vault`, `azure-blob`, and `azure-monitor` descriptors for its checks. These registrations do not form a product provider catalog.

Model is not a provider seam. The v1 source contains no AgentHost, model resolver, or model adapter. It does not declare support for a model vendor.
