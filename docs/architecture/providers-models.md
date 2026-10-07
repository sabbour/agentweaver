# Providers and models

`Agentweaver.Abstractions` names 15 provider seams. A seam defines a contract boundary. It does not prove that an adapter or running service exists.

`ProviderDescriptor` records the seam, provider ID, adapter version, options-schema version, hosting pattern, and advertised capabilities. It contains no option values or credentials.

`ProviderCatalog.Create` validates registrations, defaults, allowed overrides, ordered sets, network-policy layers, and meter-keyed Cost selections. `ProviderResolver` checks cardinality, versions, options schemas, and capabilities.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-provider-resolution.png'" alt="Provider resolution flow: the Foundation Probe supplies existing target IDs and ResourceNegotiation; Knowledge resolves and negotiates one selected Memory candidate per run. ProviderResolver pins immutable bindings; no resources are provisioned and no model AgentHost is shown." />
  </a>
  <figcaption>The Probe supplies existing target IDs, generation, and ResourceNegotiation; Knowledge negotiates its selected Memory provider. ProviderResolver pins both bindings but does not provision resources.</figcaption>
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

Explicit-event forks require the pinned provider's `sessions.events.fork` capability
and a committed source event matched to its session-bound cursor. Events rechecks the
Orchestrator's admission receipt inside the fork transaction, then atomically persists
the target session and lineage with the provider-binding hash. Exact retries return the
stored lineage; reusing an idempotency key for a different fork conflicts. The
Orchestrator adds the lineage to its session tree only after its final owner-state
recheck, so an unregistered target is not a usable child. Session tree and status
routes report durable owner state; they do not imply a running AgentHost.

Event version 2 defines a typed PolicyEvaluation payload, but the native journal does not
advertise `sessions.policy.evaluations` or accept these events from generic run-scoped
callers. Actor equality does not prove Orchestrator Core writer provenance. The capability
must remain unavailable until that trusted writer path is implemented.

The Orchestrator Core also contains the source-only `agt.dotnet-yaml` Policy provider.
It requires a deny-by-default platform policy, rejects unsupported AGT actions, and
requires every document in the platform and project sets to allow. It uses the existing
platform-singleton catalog/resolver/pinning path; this adapter does not authorize a
protected action or imply that runtime call sites are wired.

Knowledge uses the same exclusive Memory seam. Its run-selection request must contain
exactly one Memory candidate; the service resolves that candidate through
`ProviderCatalog` and `ProviderResolver`, negotiates the live database identity, then
persists an immutable project/run binding with the project, configuration, and context
revisions. A missing adapter, capability mismatch, or different later binding fails
closed without falling back to another provider. The current `postgres.native-memory`
adapter implements the read, write, search, revision, proposal-promotion, and context
composition capabilities. See [Knowledge and Memory](knowledge-memory.md) for the
service and its integration limits.

The host also registers `postgres.native-messaging` as the platform-singleton Messaging
provider, with no project override. It negotiates the configured PostgreSQL resource
and persists an immutable Messaging provider binding per run. The Orchestrator uses the
internal addressed-message routes to validate durable owner outbox records and request
delivery at explicit turn boundaries. There is no background delivery relay or
automatic AgentHost scheduler. These are source-host integrations, not a deployed
cross-service messaging endpoint.

The Foundation Probe registers `azure-key-vault`, `azure-blob`, and `azure-monitor` descriptors for its checks. These registrations do not form a product provider catalog.

Model is not a provider seam. The v1 source contains no AgentHost, model resolver, or model adapter. It does not declare support for a model vendor.

A Cost source key and an opaque model-selection reference are not trusted SDK
provenance. The native Projects run-selection routes retain the source key,
adapter/options versions, options revision and capabilities without claiming
pricing or admitting usage writes.
