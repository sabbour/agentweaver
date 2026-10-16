# Providers and models

## The mental model

A provider seam separates a platform contract from its backend implementation. Selecting a provider answers which adapter is eligible. Negotiating and pinning answer which actual resource the run uses.

These are separate steps. A catalog descriptor does not provision a resource, authorize a caller, or prove that a backend is healthy. Model references add another layer: they name an accepted model choice, not a provider-seam enum member.

## Where this lives

- `packages/Agentweaver.Abstractions/ProviderContracts.cs` — seam cardinality, descriptors, candidates, negotiations, and pins.
- `packages/Agentweaver.Abstractions/NetworkPolicyContracts.cs` — narrowing intent and layered network bindings.
- `packages/Agentweaver.Abstractions/ModelSourceMode.cs` and `RuntimeModelBindingPin.cs` — hosted/BYOK model identity.
- `packages/Agentweaver.Providers/ProviderCatalogConfiguration.cs` — configuration loading.
- `packages/Agentweaver.Providers/ProviderCatalog.cs` — validated registration and selection maps.
- `packages/Agentweaver.Providers/ProviderResolver.cs` — resolution, negotiation checks, and binding verification.
- `packages/Agentweaver.Identity/RuntimeModelBindingsResolver.cs` and `RuntimeModelBinding.cs` — shared concrete model resolution.

## Core concepts and invariants

### The catalog owns eligibility

`ProviderDescriptor` names the seam, provider ID, adapter version, options-schema version, hosting pattern, and advertised capabilities. `ProviderRegistration` adds enabled state and an options revision. Neither contains secret values.

`ProviderCatalog.Create` checks registration identities and selection maps. A project selects only permitted overrides. It cannot replace a platform-singleton provider.

The current `ProviderSeam` enum contains 15 seams. Canvas appears in the architecture plan, not in this enum or resolver.

### Cardinality selects the resolver path

| Form | Seams | Resolution |
| --- | --- | --- |
| Exclusive | Sessions, Snapshots, Sandbox, Storage, Memory, Source Control | One candidate through `Resolve`. Snapshots supports explicit None. |
| Platform singleton | Policy, Secrets, Messaging, Object Store | One platform choice through `Resolve`; no project replacement. |
| Ordered composite | Guardrails, Telemetry | `ResolveOrdered` preserves order. |
| Layered | Network Policy | `ResolveNetworkPolicy` requires L3/L4 and resolves L7 when required. |
| Meter keyed | Cost | `ResolveCost` selects by the explicit meter source. |
| Per application | Application Hosting | Not implemented by the current resolver. |

Calling the single-provider resolver for another cardinality returns `UnsupportedCardinality`. It does not pick the first registration.

## Resolution and pinning flow

1. Load and validate the platform catalog.
2. Resolve the default or permitted project selection using required adapter version, options schema, and capabilities.
3. Give the candidate to the backend owner for provisioning or negotiation.
4. Receive `ResourceNegotiation` with the actual resource identity, generation, and negotiated capabilities.
5. Pin the run binding after checking that resource and candidate metadata match.
6. Verify the saved binding on later operations rather than silently selecting a new default.

An advertised capability is an adapter claim. A negotiated capability belongs to the selected resource. The pin records provider identity, options revision, resource generation, and negotiated capabilities. It is not an authorization decision.

Network pinning also requires the confirmed applied intent generation. Cost pinning preserves the exact meter mapping. Ordered pinning preserves the chain order. Each owner persists its actual pin; Projects & Config persists accepted candidates.

## From a model reference to native execution

Projects & Config selects a project model reference or the platform default from the accepted available references. `RuntimeModelBindingsResolver` then maps that exact reference to a concrete enabled model and source mode.

The shared resolver reads `AgentHost:ModelBindings` and `AgentHost:ModelBindingsRevision`. It freezes a startup snapshot and hashes the canonical map, including explicit provider settings and optional prompt capacities. The pin includes the reference, concrete model ID, source mode, configuration revision, hash, and BYOK provider type when applicable.

`HostedCopilot` and `Byok` are explicit modes. BYOK pins admit `azure`, `openai`, or `anthropic` provider types. Resolution rejects a missing reference, disabled model, source-mode mismatch, or changed accepted pin. It does not choose a nearby model.

AgentHost uses the accepted concrete model with the Copilot SDK harness. Hosted prompt capacity comes from the native catalog, narrowed by an available server limit. BYOK uses server-owned capacity and explicit provider configuration. The resulting native prompt setting controls compaction; it is not a guarantee that every internal provider request stays below a hard admission threshold.

## Failure behavior

Catalog and resolver failures use structured codes for invalid configuration, missing or disabled providers, denied overrides, incompatible versions, missing capabilities, and pin mismatches. Owners keep those failures explicit.

Changing the catalog or model map does not update an accepted resource or model pin in place. Recovery requires the saved binding to remain valid; backend failure does not authorize fallback.

## Related pages

- [Provider and model contracts](../architecture/providers-models.md)
- [Provider seam design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md)
- [Contracts and configuration](../reference/contracts.md)
- [Projects and configuration](./projects-config.md)
- [Agent runtime](./agent-runtime.md)
