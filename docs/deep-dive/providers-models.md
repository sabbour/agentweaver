# Providers and models

## 1–4. Goals, constraints, context and strategy

Use this page to understand how Agentweaver selects a provider adapter and
binds a run to a concrete resource and model. Selecting a provider identifies
an eligible adapter; it does not provision a resource, authorize a caller, or
prove backend health.

The common resolver enforces contract shape, while resource owners retain
negotiated facts. This avoids backend-specific assumptions in callers at the
cost of rejecting fallback when a pinned resource changes.

## 5. Building blocks and seams

### Understand provider selection

A provider seam separates a platform contract from its backend implementation.
A catalog descriptor advertises eligibility. The resource owner negotiates and
pins the actual resource separately. A model reference is another layer: it
names an accepted model choice, not a provider-seam enum value.

| Source | Responsibility |
| --- | --- |
| `ProviderContracts.cs` | Seam cardinality, descriptors, candidates, negotiations, and pins |
| `NetworkPolicyContracts.cs` | Narrowing intent and layered network bindings |
| `ModelSourceMode.cs` and `RuntimeModelBindingPin.cs` | Hosted/BYOK model identity |
| `ProviderCatalogConfiguration.cs` and `ProviderCatalog.cs` | Catalog loading, validation, and selection maps |
| `ProviderResolver.cs` | Resolution, negotiation checks, and binding verification |
| `RuntimeModelBindingsResolver.cs` and `RuntimeModelBinding.cs` | Concrete model resolution |

The first five paths are under `packages/Agentweaver.Abstractions/` or
`packages/Agentweaver.Providers/`; model resolver paths are under
`packages/Agentweaver.Identity/`.

`ProviderDescriptor` identifies the seam, provider, adapter version,
options-schema version, hosting pattern, and advertised capabilities.
`ProviderRegistration` adds enabled state and an options revision. Neither
stores secret values.

The current `ProviderSeam` enum contains 15 seams. Canvas appears in the
architecture plan, not in this enum or the resolver.

### Match resolution to seam cardinality

| Form | Seams | Resolution behavior |
| --- | --- | --- |
| Exclusive | Sessions, Snapshots, Sandbox, Storage, Memory, Source Control | `Resolve` selects one candidate. Snapshots also supports explicit None. |
| Platform singleton | Policy, Secrets, Messaging, Object Store | `Resolve` uses one platform choice; projects cannot replace it. |
| Ordered composite | Guardrails, Telemetry | `ResolveOrdered` preserves order. |
| Layered | Network Policy | `ResolveNetworkPolicy` requires layer 3 and layer 4 rules, and resolves layer 7 when required. |
| Meter keyed | Cost | `ResolveCost` selects by explicit meter source. |
| Per application | Application Hosting | Not implemented by the current resolver. |

Calling a single-provider resolver for another cardinality returns
`UnsupportedCardinality`; it does not pick the first registration.

Sessions is exclusive for each run. The native PostgreSQL journal is the default,
and the optional `agentsessions.records` provider in
`Agentweaver.Providers.Sessions.AgentSessions` is a second, read-only
implementation. Both advertise the provider-neutral `SessionsCapabilities`
names from `Agentweaver.Abstractions`. The resolver matches the adapter
version exactly, so both report Sessions adapter contract `1.0.0`.

## 6. Runtime view

### Resolve and pin a resource

Before resolving, load the enabled platform catalog and provide the accepted
project selection and required adapter, options-schema, and capability
requirements.

1. Validate the platform catalog.
2. Resolve the default or permitted project selection.
3. Give the candidate to the backend owner for provisioning or negotiation.
4. Receive a `ResourceNegotiation` containing actual resource identity,
   generation, and negotiated capabilities.
5. Pin the run binding after checking that resource and candidate metadata
   match.
6. Verify the saved binding on later operations; do not silently select a new
   default.

An advertised capability is an adapter claim. A negotiated capability belongs
to the selected resource. A pin records provider identity, options revision,
resource generation, and negotiated capabilities; it is not an authorization
decision.

For `KnowledgeRecordQuery`, a null agent ID means project-wide search across
all agents. Each Memory provider still filters by project, so this query does
not widen results across projects or change the run's exclusive provider pin.

A Source Control pull request result also carries the provider title when the
provider returns one. After a run creates or reuses a pull request, the
Orchestrator records it for the project pull request list. Recording begins
only when migration 032 is applied, and the title
doesn't change merge-intent hashes.

Network pinning also requires the confirmed applied-intent generation. Cost
pinning preserves the exact meter mapping. Ordered pinning preserves the chain
order. Each owner persists its actual pin; Projects & Config persists accepted
candidates.

For supported catalog settings and provider setup, see the
[provider configuration guide](../guide/configure-providers.md) and the
[runtime configuration reference](../reference/runtime-configuration.md).

### Bind a model to native execution

Projects & Config selects a project model reference or platform default from
the accepted available references. `RuntimeModelBindingsResolver` maps that
reference to a concrete enabled model and source mode.

The resolver reads `AgentHost:ModelBindings` and
`AgentHost:ModelBindingsRevision`. It freezes a startup snapshot and hashes the
canonical map, including explicit provider settings and optional prompt
capacities. The pin includes the reference, concrete model ID, source mode,
configuration revision, hash, and BYOK provider type when applicable.

`HostedCopilot` and `Byok` are explicit modes. Bring your own key (BYOK) pins
accept `azure`, `openai`, or `anthropic`. Resolution rejects a missing
reference, disabled model, source-mode mismatch, or changed accepted pin. It
does not choose a nearby model.

AgentHost uses the accepted concrete model with the Copilot SDK harness.
Hosted prompt capacity comes from the native catalog, narrowed by an available
server limit. BYOK uses server-owned capacity and explicit provider
configuration. The resulting native prompt setting controls compaction; it does
not guarantee that every internal provider request stays below a hard
admission threshold.

### Diagnose resolution failures

Catalog and resolver failures identify invalid configuration, missing or
disabled providers, denied overrides, incompatible versions, missing
capabilities, or pin mismatches. Owners keep these failures explicit.

Changing the catalog or model map does not update an accepted resource or model
pin in place. A backend failure does not authorize fallback.

## 7–9. Deployment, shared concepts and decisions

The catalog/resolver is a library, not a resource provisioner. Owner hosts load
their registered catalogs and options; adapters use separately prepared
backend resources. [Deployment](./infra-deployment.md) maps those resources,
while [AgentHost](./agent-runtime.md) applies the accepted concrete model pin.

Current authority remains independent of capability negotiation and pinning.
The [provider seam design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md)
describes the broader model; the
[canonical ADR view](./00-system-overview.md#9-architecture-decisions) retains
decision status. Planned Canvas is not a current enum/resolver capability.

## 10–12. Quality, risks and glossary

| Context and stimulus | Response and measure | Verification state |
| --- | --- | --- |
| Caller uses a single-provider resolver on a composite seam | Return `UnsupportedCardinality`, not the first registration | Resolver tests |
| Catalog/options/resource generation differs from a saved pin | Fail verification; do not select a new default | Resolver and owner binding tests |
| Accepted model reference is missing, disabled or changes mode | Reject resolution rather than choose a nearby model | Model binding tests; not paid-provider validation |

Advertised capabilities can differ from negotiated resource capabilities.
Owners must validate both before saving a pin and keep unavailable pricing or
unknown SDK provider attribution explicit.

**Terms:** seam is a contract boundary; candidate is eligible metadata;
negotiation reports actual resource facts; pin retains those facts; model
reference names a separate execution choice.

## Related content

- [Provider and model contracts](../architecture/providers-models.md)
- [Provider seam design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/provider-seams.md)
- [Contracts and configuration](../reference/contracts.md)
- [Platform components](../guide/platform-components.md)
- [Projects and configuration](./projects-config.md)
- [Agent runtime](./agent-runtime.md)
