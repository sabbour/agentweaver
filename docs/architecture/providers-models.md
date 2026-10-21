# Providers and models

Use this reference to understand how Agentweaver models provider choices and
binds them to runtime resources. A provider seam is a contract boundary for
one capability, such as Memory or Storage. An adapter implements that
contract for a technology. The `Agentweaver.Abstractions` source currently
defines 15 seams; a seam does not prove that its adapter or service is running.
For configuration steps, see the planned
[provider configuration guide](../guide/configure-providers.md) and
[runtime configuration reference](../reference/runtime-configuration.md).

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

The authenticated Events run-binding route returns this actual consumer pin under existing exact-run read authority.
Environment's current placement also exposes its successful consumer pin without option values or recovery metadata.
Neither response converts accepted candidates into provisioned resources.
The native SDK supplies its effective `modelId` separately from the immutable accepted `modelSelectionReference` and `acceptedSelectionHash`.

The shared, SDK-independent resolver reads `AgentHost:ModelBindings` and
`AgentHost:ModelBindingsRevision` in AgentHost and the enabled Events native consumer.
It resolves an exact accepted reference to an enabled concrete model and source mode.
Its pin includes the configuration revision and canonical map hash.
An optional positive `PromptCapacityTokens` is part of that canonical map.
Omitted capacity preserves the historical null hash.
Hosted prompt limits require the concrete native catalog capacity; an optional lower server limit can narrow it.
BYOK prompt limits require the concrete server-owned capacity and explicit SDK provider configuration.
AgentHost uses the narrowest accepted and available capacity, then pins the effective value for resume.
The pinned native SDK uses this setting as a prompt-compaction threshold. It compacts removable
conversation history; an irreducible latest turn can still be sent to the provider during that
compaction attempt and then end with the SDK's capacity-attributed `query` error. This is not a
hard admission check for every internal provider request or a guarantee that no request occurs.
The runtime does not silently truncate input. These are not cumulative token budgets or internal
model-call counters.
For configured hosted credit limits, Events prices that concrete model before root
acceptance or backlog claim, without creating SDK or usage facts.
The Host later checks the accepted pin before SDK startup and verifies actual native model policy.
The model ID must match the accepted model. Any provider ID returned by the SDK or context metadata must match the accepted BYOK provider; when the SDK omits provider attribution, it remains unknown rather than being treated as a mismatch.
BYOK token pricing cannot substitute for hosted Copilot credit admission.

Explicit-event forks require the pinned provider's `sessions.events.fork` capability
and a committed source event matched to its session-bound cursor. Events rechecks the
Orchestrator's admission receipt inside the fork transaction, then atomically persists
the target session and lineage with the provider-binding hash. Exact retries return the
stored lineage; reusing an idempotency key for a different fork conflicts. The
Orchestrator adds the lineage to its session tree only after its final owner-state
recheck, so an unregistered target is not a usable child. Session tree and status
routes report durable owner state; they do not imply a running AgentHost.

Event version 2 defines a typed PolicyEvaluation payload. The native journal
advertises `sessions.policy.evaluations` for new run pins. Generic run-scoped
callers cannot append these events because actor equality does not prove
Orchestrator Core writer provenance. Each run keeps its exact persisted
capability set across provider upgrades. Older pins remain usable for ordinary
append, replay, subscribe, and additional session registration. The new receipt
route returns `409` for a pin without this capability and does not advance the
journal. New runs negotiate the expanded set.

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

Knowledge uses the same exclusive Memory seam. Its run-selection request must
contain exactly one Memory candidate. The service resolves that candidate
through `ProviderCatalog` and `ProviderResolver`. It negotiates the live
database identity, then persists an immutable project/run binding with the
project, configuration, and context revisions. A missing adapter, capability
mismatch, or different later binding fails
closed without falling back to another provider. The current `postgres.native-memory` adapter implements the read, write, search,
revision, proposal-promotion, and context-composition capabilities. The optional
`cosmos.memory` source candidate implements those operations and accepted-effect
delivery with a project-partitioned Cosmos container when registered and configured.
See [Knowledge and Memory](knowledge-memory.md) for the service and its integration
limits.

Native PostgreSQL remains the default. Cosmos negotiation validates the existing
container identity, `/projectId` partition key, required search composite index, and
non-expiring default TTL; it does not provision resources.
The `redis.memory` source candidate requires TLS and negotiates standalone-primary,
AOF-always, noeviction, no-replica/no-cluster, and non-expiring-data constraints.
Neither candidate provisions resources. These are Memory backends, not replacements
for the PostgreSQL Sessions journal.

Application Hosting currently plans only the built-in AKS web runtime.
Canvas owns A2UI and GitHub Canvas compatibility work. Elastic SAN is outside P2;
Container Apps Sandboxes is a later Sandbox evaluation, not an application host.

## GitHub Canvas research boundary

The GitHub Canvas research inspected the
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
inform a separate GitHub Canvas adapter.
Canvas remains unimplemented in the current source enum and resolver. The research does not
implement that adapter or establish deployment or publication acceptance.

## Existing model and provider integrations

The host also registers `postgres.native-messaging` as the platform-singleton Messaging
provider, with no project override. It negotiates the configured PostgreSQL resource
and persists an immutable Messaging provider binding per run. The Orchestrator uses the
internal addressed-message routes to validate durable owner outbox records and request
delivery at explicit turn boundaries. There is no background delivery relay or
automatic AgentHost scheduler. These are source-host integrations, not a deployed
cross-service messaging endpoint.

The Foundation Probe registers `azure-key-vault`, `azure-blob`, and `azure-monitor` descriptors for its checks. These registrations do not form a product provider catalog.

Model is not a provider seam. The unpublished [AgentHost executable](agenthost.md)
uses the runtime library, not a new general model resolver. The library maps accepted references to actual SDK
models through a registered connection and SDK catalog.
This source does not declare deployed model-vendor support.

Platform defaults route planning, review, design, security, creative writing, and
complex work to `gpt-6.1-sol` at High effort. They route code-writing and
straightforward work to `gpt-6-luna` at High effort. Platform and project
configuration can override the model reference and reasoning effort by task,
role, or agent. An explicit project model selection pins every route for that
project, preserving the 0.x run-pin behavior. The accepted run stores the
resolved task/role/agent routes and each distinct model/effort selection.
Every reference must be present in the immutable run context; an unavailable
reference fails run acceptance instead of falling back. Platform configurations
that omit task defaults get the built-in routes. Unless a project pins one
model, a platform that doesn't offer both default models must replace or clear
those routes; see
[Projects configuration](projects-config.md#selection-and-persistence-rules).

The accepted reasoning effort is passed to the native Copilot SDK session and
is recorded in the runtime binding and durable session material. An accepted
`ModelBindingPin`, when present, must match that effort. AgentHost also resolves
a separate server-owned pin for the exact reference, source mode, and effort;
resume and usage evidence must match it. Before session creation or resume,
AgentHost checks the requested effort against the selected model's capabilities.
BYOK model bindings must list supported efforts explicitly. Unsupported or
unknown effort fails closed; the runtime does not downgrade effort.

GitHub Copilot and BYOK remain distinct SDK modes. A selected `SecretRef` is not
proof of the credential's format, current ownership, refresh, or SDK purpose.
Copilot requires its supported GitHub credential path; BYOK requires an explicit
SDK `Provider` configuration. A provider key, credential envelope, or GitHub App
installation token must not be passed as an interchangeable Copilot `GitHubToken`.

The source repair adds an explicit `SourceMode` to platform/project model
settings. Hosted mode selects a stable Identity-owned `ConnectionId`; BYOK selects
an exact `CredentialReference`. Empty or unknown modes, invalid connection references,
and mixed hosted/BYOK settings fail with standard errors.
The Identity-owned connection lifecycle implements these contracts in source, not in a deployed service.

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

An accepted BYOK selection also stores the common `ModelBindingPin` from the server-owned model map.
The pin includes the provider type, model ID, configuration revision, and configuration hash.
The Host compares the pin before SDK use and records the actual effective model in `SdkSessionFacts.ByokProvider`.
Core binds token measurements to these facts, the producer registration, and the dispatch ID.
Events reads the immutable Core receipt by reference. It does not accept caller-supplied provider authority.
Unknown pricing remains `Unpriced`.

Identity owns the Copilot connection and refresh lifecycle.
Repository GitHub App credentials are a separate Identity connection.
Platform/project model selection remains the documented scope. GitHub-user account linking
can authenticate a deliberately selected Copilot binding; it does not add personal BYOK
settings or personal model-provider preference or fallback.
Controlled SDK catalog and usage fixtures do not prove live user entitlement.

A Cost source key and an opaque model-selection reference are not trusted SDK
provenance. The native Projects run-selection routes retain the source key,
adapter/options versions, options revision and capabilities without claiming
SDK producer authority or admitting usage writes.

The `copilot.usage-cost` source adapter prices reported input, cached-input,
cache-write, and output tokens directly in AI credits (`AIC`) using a provider-owned
immutable rate-card snapshot. The operator supplies the versioned JSON document
through `EventsAndSessions:Cost:Copilot:RateCard:Json`; startup validates its strict
schema and hashes the exact document. The cost binding, snapshots, and accounting
receipts retain the card version and content hash, so changing the ConfigMap does
not reprice earlier usage. The default card cites
[GitHub's official Copilot model-pricing documentation](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing),
retrieved 2026-10-17. It contains the 33 models and published token categories listed
below. Complete token measurements can be priced when the SDK omits Nano AIU.
Copilot configuration does not supply scalar multipliers, and prices are not
inferred from legacy multipliers. Missing token measurements, unsupported sources,
unavailable context tiers, and unknown model rates return an explicit `Unpriced`
result with a reason, never a zero price. A valid `Unpriced` quote does not itself block
run admission or dispatch; its recorded usage remains visible but does not count
toward credit limits, while priced usage continues to count. Malformed or tampered
receipts still fail closed.

GitHub publishes the following USD rates per 1 million tokens. Values use the order
input / cached input / cache write / output. A dash means the source marks the rate
or long-context tier as not applicable; it is not a zero price. Long-context rates
apply only above the listed input-token threshold.

| Model ID | Default rates | Long-context threshold | Long-context rates |
| --- | --- | ---: | --- |
| `gpt-5-mini` | $0.25 / $0.025 / — / $2.00 | — | — |
| `gpt-5.3-codex` | $1.75 / $0.175 / — / $14.00 | — | — |
| `gpt-5.4` | $2.50 / $0.25 / — / $15.00 | 272K | $5.00 / $0.50 / — / $22.50 |
| `gpt-5.4-mini` | $0.75 / $0.075 / — / $4.50 | — | — |
| `gpt-5.4-nano` | $0.20 / $0.02 / — / $1.25 | — | — |
| `gpt-5.5` | $5.00 / $0.50 / — / $30.00 | 272K | $10.00 / $1.00 / — / $45.00 |
| `gpt-5.6-luna` | $0.20 / $0.02 / $0.25 / $1.20 | 200K | $0.40 / $0.04 / $0.50 / $1.80 |
| `gpt-5.6-sol` | $4.00 / $0.40 / $5.00 / $20.00 | 272K | $8.00 / $0.80 / $10.00 / $30.00 |
| `gpt-5.6-terra` | $2.00 / $0.20 / $2.50 / $12.00 | 272K | $4.00 / $0.40 / $5.00 / $18.00 |
| `gpt-6-astra` | $10.00 / $1.00 / $12.50 / $50.00 | 272K | $20.00 / $2.00 / $25.00 / $75.00 |
| `gpt-6-luna` | $0.10 / $0.01 / $0.125 / $0.50 | 272K | $0.20 / $0.02 / $0.25 / $0.75 |
| `gpt-6-sol` | $2.00 / $0.20 / $2.50 / $10.00 | 272K | $4.00 / $0.40 / $5.00 / $15.00 |
| `gpt-6.1-sol` | $2.00 / $0.10 / $2.50 / $10.00 | 272K | $4.00 / $0.20 / $5.00 / $15.00 |
| `claude-haiku-4.5` | $1.00 / $0.10 / $1.25 / $5.00 | — | — |
| `claude-haiku-5.5` | $0.10 / $0.01 / $0.125 / $0.50 | 100K | $0.50 / $0.05 / $0.625 / $2.50 |
| `claude-sonnet-4` | $3.00 / $0.30 / $3.75 / $15.00 | — | — |
| `claude-sonnet-4.6` | $3.00 / $0.30 / $3.75 / $15.00 | — | — |
| `claude-opus-4.8` | $5.00 / $0.50 / $6.25 / $25.00 | — | — |
| `claude-opus-5` | $5.00 / $0.50 / $6.25 / $25.00 | — | — |
| `claude-opus-5.5` | $4.00 / $0.20 / $5.00 / $20.00 | — | — |
| `claude-sonnet-5` | $2.00 / $0.20 / $2.50 / $10.00 | — | — |
| `claude-sonnet-5.5` | $2.00 / $0.10 / $2.50 / $10.00 | — | — |
| `claude-opus-4.8-fast` | $10.00 / $1.00 / $12.50 / $50.00 | — | — |
| `claude-fable-5` | $10.00 / $1.00 / $12.50 / $50.00 | — | — |
| `claude-fable-5.1` | $10.00 / $0.25 / $12.50 / $50.00 | — | — |
| `gemini-3.7-flash` | $0.75 / $0.075 / — / $3.75 | — | — |
| `gemini-3.8-flash` | $0.75 / $0.075 / — / $3.75 | — | — |
| `gemini-4-argon` | $2.00 / $0.10 / — / $10.00 | — | — |
| `mai-code-1.1-flash` | $0.20 / $0.02 / — / $1.20 | — | — |
| `grok-4.5` | $2.00 / $0.50 / — / $6.00 | 200K | $4.00 / $1.00 / — / $12.00 |
| `grok-4.6` | $2.00 / $0.50 / — / $6.00 | 200K | $4.00 / $1.00 / — / $12.00 |
| `grok-4.7` | $2.00 / $0.50 / — / $6.00 | 200K | $4.00 / $1.00 / — / $12.00 |
| `kimi-k3` | $3.00 / $0.30 / — / $15.00 | — | — |

The model IDs above are the rate-card keys. Pricing applies only when a key exactly
matches the model ID returned by the Copilot SDK. The source defines one AI credit
as $0.01 USD. The snapshot records the source URL and retrieval date so earlier
usage is not repriced when GitHub changes rates. Keep the adapter options revision
aligned with the provider catalog; the rate-card version identifies each pricing
snapshot. The adapter and PostgreSQL ledger do not establish SDK producer authority.
The separate runtime pipeline requires a current registration, original validated bearer,
and purpose-bound observe credential before it commits actual SDK measurements.
Events accepts only the resulting immutable source receipt reference.
