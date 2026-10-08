# Providers and models

`Agentweaver.Abstractions` names 15 provider seams. A seam defines a contract boundary. It does not prove that an adapter or running service exists.

The [platform design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0001-platform-architecture.md#provider-seams)
now plans 16 seams. Canvas is new P2 work, not a current enum member or implemented
resolver path. It separates A2UI and GitHub Canvas compatibility from Application Hosting.

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
| Keyed by meter source | Cost | `ResolveCost` selects one enabled provider per explicit source. `PinCost` and `VerifyCost` validate immutable Cost bindings. Projects persists candidates only. |
| Per application | Application Hosting | Not implemented. |
| Per canvas | Canvas (planned P2) | Not implemented in the current enum or resolver. |

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

Event version 2 defines a typed PolicyEvaluation payload. The native journal advertises
`sessions.policy.evaluations` for new run pins, but generic run-scoped callers cannot append
these events because actor equality does not prove Orchestrator Core writer provenance.
Each run keeps its exact persisted capability set across provider upgrades: older pins
remain usable for ordinary append, replay, subscribe, and additional session registration,
but the new receipt route returns `409` for a pin without this capability and does not
advance the journal. New runs negotiate the expanded set.

The receipt route accepts only an immutable Orchestrator receipt ID. Owner writes and
Events admission require current Core writer authority, accepted selection, and a matching
active owner session/run, actor, and fence; each rechecks authority after owner-row lock
waits, including duplicate attempts. Allow also requires its exact active grant. Deny and
Error can reference an issued inactive grant, but remain non-authorizing facts.

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

P2 adds Cosmos and Redis adapters behind that same exclusive Memory contract.
Native PostgreSQL remains the default, and the selected adapter must preserve durable
records, revisions, scope isolation, idempotency, and proposal semantics.
These are Memory backends, not replacements for the PostgreSQL Sessions journal.
The current source contains neither adapter.

Application Hosting currently plans only the built-in AKS web runtime.
Canvas owns A2UI and GitHub Canvas compatibility work. Elastic SAN is outside P2;
Container Apps Sandboxes is a later Sandbox evaluation, not an application host.

## GitHub Canvas research boundary

Research [#1901](https://github.com/sabbour/agentweaver/issues/1901) inspected the
experimental public SDK at commit
[`2023ed29af3cae890b26f9e2a269a04d8ac7ba60`](https://github.com/github/copilot-sdk/tree/2023ed29af3cae890b26f9e2a269a04d8ac7ba60)
and permitted local declarations. The source snapshot reports SDK `0.0.0-dev`,
CLI `1.0.93-3`, and global wire protocol `3`; observed app discovery used
runtime `1.0.93-1`. These are evidence baselines, not Canvas-format versions or
a tested interoperable pair.

The bounded target is declaration/action/lifecycle compatibility with trusted owned
assets in Agentweaver's retained panel. No portable GitHub content format, rendering
catalog, or generic browser bridge was established. No interoperability was executed;
the historical reverse-engineering artifact is unavailable.

Core retains current actor authorization, operation identity, workflow gates, and
artifact ownership. An instance or URL is not an artifact or grant. Reconnect must
preserve its binding and obtain a fresh location; successful close does not prove cleanup.
The [detailed evidence, unknowns, and subset](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/applications-and-surfaces.md#github-canvas-evidence-and-versions)
and [planned acceptance cases](../guide/testing.md#planned-github-canvas-adapter-acceptance)
inform separate adapter [#1904](https://github.com/sabbour/agentweaver/issues/1904).
Canvas remains unimplemented in the current source enum/resolver; research does not close
that adapter issue or establish deployment/publication acceptance.

## Existing model and provider integrations

The host also registers `postgres.native-messaging` as the platform-singleton Messaging
provider, with no project override. It negotiates the configured PostgreSQL resource
and persists an immutable Messaging provider binding per run. The Orchestrator uses the
internal addressed-message routes to validate durable owner outbox records and request
delivery at explicit turn boundaries. There is no background delivery relay or
automatic AgentHost scheduler. These are source-host integrations, not a deployed
cross-service messaging endpoint.

The Foundation Probe registers `azure-key-vault`, `azure-blob`, and `azure-monitor` descriptors for its checks. These registrations do not form a product provider catalog.

Model is not a provider seam. The v1 source contains no AgentHost executable or
general model resolver. The runtime library maps accepted references to actual SDK
models through a registered connection and SDK catalog.
This source does not declare deployed model-vendor support.

GitHub Copilot and BYOK remain distinct SDK modes. A selected `SecretRef` is not
proof of the credential's format, current ownership, refresh, or SDK purpose.
Copilot requires its supported GitHub credential path; BYOK requires an explicit
SDK `Provider` configuration. A provider key, credential envelope, or GitHub App
installation token must not be passed as an interchangeable Copilot `GitHubToken`.

The approved source repair adds an explicit `SourceMode` to platform/project model
settings. Hosted mode selects a stable Identity-owned `ConnectionId`; BYOK selects
an exact `CredentialReference`. Empty or unknown modes, invalid connection references,
and mixed hosted/BYOK settings fail with standard errors. These contracts are planned
source work, not an implemented or deployed connection lifecycle.

The accepted hosted selection and its hash remain immutable during normal token
rotation. Each ModelSession receipt pins the current connection revision, exact secret
version, grant revision, and credential kind. Identity and the current Core binding
authorize use; a submitted connection ID alone gives no authority. Consumption rechecks
current proof after remote waits and before SDK use. Stale receipts fail closed.
Changes to the connection's owner, kind, scope, or GitHub identity require an authorized
new binding or selection, not silent reassignment of the same ID.

Legacy omitted or null fields, stored JSON, and accepted hashes must remain unchanged.
An accepted secret-pinned hosted selection cannot become a connection reference through
a compatibility default or automatic migration. It returns an explicit migration-required
or unavailable result until a new selection is authorized. BYOK retains its exact secret
reference and SDK Provider configuration, without hosted fallback.

The AgentHost mode repair remains in
[#1856](https://github.com/sabbour/agentweaver/issues/1856). The missing Copilot connection
and refresh lifecycle is tracked in [#1906](https://github.com/sabbour/agentweaver/issues/1906);
repository App credentials are separate [#1907](https://github.com/sabbour/agentweaver/issues/1907).
Platform/project model selection remains the documented scope. GitHub-user account linking
can authenticate a deliberately selected Copilot binding; it does not add personal BYOK
settings or personal model-provider preference or fallback.
Controlled SDK catalog and usage fixtures do not prove live user entitlement.

A Cost source key and an opaque model-selection reference are not trusted SDK
provenance. The native Projects run-selection routes retain the source key,
adapter/options versions, options revision and capabilities without claiming
SDK producer authority or admitting usage writes.

The `copilot.usage-cost` source adapter prices reported `nano_aiu` values in AI
credits (`AIC`). One AIC contains `1_000_000_000` nano-AIU. Reported nano-AIU already
includes model weighting, so usage pricing does not apply the multiplier again.
Pre-flight quotes apply the multiplier only to explicitly unweighted AI credits.
Each binding includes an explicit immutable rate-card version. Missing units,
unsupported sources, and unknown model rates return `Unpriced`, not a zero price.
The adapter and PostgreSQL ledger do not establish SDK producer authority.
The separate runtime pipeline requires a current registration, original validated bearer,
and purpose-bound observe credential before it commits actual SDK measurements.
Events accepts only the resulting immutable source receipt reference.
