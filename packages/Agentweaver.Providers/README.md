# Provider foundation (0.1.0)

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

Only exclusive and platform-singleton resolution is implemented. Ordered
composites, layered network policy, keyed cost selection, and per-application
hosting return `UnsupportedCardinality`; they must not be treated as exclusive.
Sessions capture mirrors are not authoritative providers and are deferred.
Snapshots without a default resolve to explicit `None`; `PinSnapshotNone`
persists that choice without claiming snapshot capabilities or a resource.
No fallback, capability downgrade, runtime provisioning, cross-seam
Sandbox/Snapshot or Sandbox/Storage pairing, policy authorization, or live
provider availability is implemented here. Those checks belong to the owning
core services and subsequent conformance work; a pinned binding does not grant
ongoing authorization.
