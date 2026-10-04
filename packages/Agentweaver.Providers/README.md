# Provider foundation (0.0.0)

`Agentweaver.Abstractions` defines the 15 approved seam identities, cardinalities,
hosting patterns, immutable references, advertised capability descriptors, and
run-pinned bindings. `Agentweaver.Providers` provides an immutable in-memory
catalog and pure resolver. Register descriptors, defaults, and allowed project
overrides with `ProviderCatalog.Create`; resolve an exact adapter/options schema
version and advertised capabilities with `ProviderResolver.Resolve`. After a
resource is provisioned, call `Pin` with its expected resource ID and negotiated
capabilities. The result pins the selected provider, options revision, resource
generation, and **negotiated** (not merely advertised) capabilities for that run.
It contains no credentials or option payloads.

Exclusive and platform-singleton seams use `Resolve` and `Pin`. Configure
Guardrails/Telemetry with `ProviderOrderedSelection` and optional
`ProviderOverridePermission` entries; `ResolveOrdered` takes an optional project
ordering. Projects can add, remove, or reorder only permitted entries; unpermitted
platform entries keep their original relative order. `PinOrdered` accepts one
resource negotiation per selected provider in exactly that order. The pinned
bindings are immutable and retain options revisions and resource generations
after catalog changes.

Configure Network Policy with `ProviderLayerSelection`: L3/L4 is required; L7
is optional. `ResolveNetworkPolicy` selects these platform-owned providers and
checks required capabilities separately per layer. Projects cannot select or
replace layer providers. `PinNetworkPolicy` requires matching layer resources,
positive expected intent generation, and an identical confirmed applied
generation. It records that generation; it does not compile, apply, verify,
or provision egress policy. The owning environment manager must confirm the
generation and enforce required constraints before dispatch.

Keyed Cost selection and per-application Application Hosting are unsupported;
the single-provider `Resolve` also explicitly returns `UnsupportedCardinality`
for ordered and layered seams rather than silently treating them as exclusive.
Sessions capture mirrors are not authoritative providers and are deferred.
Snapshots without a default resolve to explicit `None`; `PinSnapshotNone`
persists that choice without claiming snapshot capabilities or a resource.
No fallback, capability downgrade, runtime provisioning, cross-seam
Sandbox/Snapshot or Sandbox/Storage pairing, policy authorization, or live
provider availability is implemented here. Those checks belong to the owning
core services and subsequent conformance work; a pinned binding does not grant
ongoing authorization.
