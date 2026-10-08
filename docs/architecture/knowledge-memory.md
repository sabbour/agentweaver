# Knowledge and Memory

`Agentweaver.Knowledge` is an unpublished .NET 10 service candidate. It owns the
project memory API, context compiler, and immutable run-provider bindings in its
PostgreSQL `knowledge` schema. Memory records, revisions, idempotency, and accepted-effect
delivery state belong to the selected Memory provider. Source presence does not mean the
service is deployed or that a caller's accepted outcome has reached the native Events
project-fact stream. Project facts are separate from the Sessions run journal.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-knowledge-memory.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-knowledge-memory.png'" alt="Knowledge rechecks current project authority and run selection, commits an accepted proposal receipt and delivery intent through the selected Memory provider, then relays only the receipt ID and versions to Events. Events fetches the receipt from the fixed Knowledge owner, rechecks current authority, and commits a separate project fact with its sequence and inbox receipt. Native PostgreSQL remains the default; the optional Cosmos container is project-partitioned." />
  </a>
  <figcaption>Accepted project facts are durably admitted by Events before Knowledge marks provider-owned delivery state complete. The Knowledge run pin and Sessions journal remain in PostgreSQL; project facts do not append to or change the run-bound journal.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-knowledge-memory.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-knowledge-memory.drawio'">Open editable draw.io source</a></p>

## Authority and ownership

Projects & Config remains the sole owner of caller memberships and project roles.
Before a record operation, Knowledge forwards the validated request's original bearer
token and optional `X-Agentweaver-Tenant` selector to
`GET /api/authorization/context`. Private record reads and writes both require fresh
effective `WriteProjects` for the target project. `ReadProjects` is metadata-only and,
even with `ReadRunSelection`, does not authorize record search, reads, revision
history, or context composition. Projects & Config remains the source of the current
project-admin permission; Knowledge does not infer it from the route's `agentId`.
A caller's existing signed project/run bindings, when present, must exactly match the
requested project/run and the Projects authority response. Knowledge keeps no
membership table, role table, authorization cache, or authorization pin.

Provider resolution also requires current effective project `ReadRunSelection`.
Knowledge fetches the immutable run-selection snapshot from Projects & Config with
that same caller token. It checks the returned project, run, and revisions before
binding a Memory provider. Redirects, owner errors, malformed or mismatched responses,
and missing permissions fail explicitly; Knowledge does not mint a token or infer
authority from path values.

Visibility is project-admin scoped, not agent-owned: a current project Owner has
`WriteProjects` for that project, and a current TenantAdmin has tenant-scoped
`WriteProjects` within the selected tenant. These admins may inspect any agent's
records in an authorized project by explicitly selecting that agent. The route's
`agentId` selects rows; it does not prove the caller is that agent, and an admin
grant does not extend to another project or tenant. Before provider resolution,
Knowledge also requires `ReadRunSelection` for the exact project and reads its
accepted run selection from Projects & Config.

The data paths preserve project and agent/run scope. Search filters by project and
agent before counting or returning records. Direct record reads return the same
not-found result when a record is absent or belongs to a different requested agent;
the revisions endpoint checks that record/agent match before querying revision
history. Context candidates are filtered by project, with active approved decisions
available project-wide, agent memories kept agent-local unless approved and tagged
`cross-team`, and SessionContext limited to the requested agent and run. The current
runtime authority contract has no agent-to-principal visibility grant, so a runtime
caller without current `WriteProjects` fails closed. Knowledge owns the authorization
and data boundaries; the selected Memory provider stores records, revisions,
idempotency receipts, and accepted-effect delivery state. The immutable run-provider
binding and Knowledge service schema remain in PostgreSQL. Knowledge does not read or
write another service's schema.

## Memory provider and run binding

`Agentweaver.Abstractions` defines the provider-neutral `IMemoryProvider` contract and
the read, write, search, revision, proposal-promotion, context-composition, and
accepted-effect-delivery capabilities. `postgres.native-memory` remains the default.
The optional `cosmos.memory` adapter is version `1.0.0` with options schema version `1`.

The accepted Projects run selection must contain exactly one exclusive Memory
candidate. Knowledge compares it with the catalog-owner snapshot loaded from
`ProjectsConfig:ProviderCatalog`, resolves it through `ProviderCatalog` and
`ProviderResolver`, and negotiates the live PostgreSQL database identity and schema.
It stores an immutable project/run binding containing provider ID, adapter version,
options-schema version and revision, resource identity and generation, negotiated
capabilities, project revision, configuration revision, and context revision. On
later operations, a changed run selection or provider binding fails closed; there is
no provider fallback.

### Cosmos Memory source candidate

When the catalog and `Knowledge:CosmosProvider` options register Cosmos, an accepted
run selection can choose it through the same exclusive `IMemoryProvider` boundary.
Native PostgreSQL remains the default. Negotiation verifies the configured database,
container, `/projectId` partition key, required search composite index, non-expiring
default TTL, resource identity, and generation; it does not provision or create Cosmos
resources. The Cosmos SDK uses the host's configured runtime workload identity. Records, immutable revisions, idempotency receipts, proposal
promotion, and accepted-effect receipt/delivery state are stored in project-partitioned
Cosmos batches. Knowledge still stores immutable run-provider bindings and its service
schema in PostgreSQL. A missing, unavailable, or incompatible selected provider fails
closed; a run never falls back to PostgreSQL.

The Cosmos tests use a fake document-store transport and exercise selection metadata,
partition isolation, immutable revisions, conflicts, idempotency, promotion, delivery
leases, and restart behavior. They do not prove live Cosmos permissions, throughput,
availability, or cloud deployment. Redis remains planned P2 work, not part of this
source candidate. Neither Memory adapter replaces the PostgreSQL Sessions journal or
the PostgreSQL state owned by other services.

## Records and revisions

Record content is project/agent scoped. Each successful update appends a full immutable
revision with a previous-revision link while compare-and-swap updates the current
record using its expected revision. Records cannot be physically deleted. Writes
require an `Idempotency-Key` scoped by project and validated actor: identical retries
return the original result; a reused key with different request content conflicts.
Not-found, stale-revision, invalid-state, provider, storage, and budget conditions do
not return success-shaped placeholders.

New proposals are pending and cannot be edited like ordinary memory. Promotion
requires the caller's current effective `WriteProjects` permission, the exact agent
owner, source run, pending state, and expected revision. It marks the proposal
promoted, creates an approved decision, appends both revision histories, and records
an immutable redacted accepted-effect receipt and delivery intent atomically through
the selected Memory provider. Native PostgreSQL uses its Knowledge database transaction;
Cosmos writes the scoped documents in one project-partition batch. The receipt contains identity and authorization bounds,
effect and record IDs, record version, and accepted time. It contains no proposal
content or credentials.

`GET /internal/accepted-effects/{receiptId}` returns that receipt with
`Cache-Control: no-store`. Knowledge requires the original issuer and subject, matching resource
bindings, no purpose-bound token, and a fresh current `WriteProjects` authorization.
It returns receipt metadata only; it does not expose the decision or proposal
contents. The scoped route
`GET /internal/projects/{projectId}/runs/{runId}/accepted-effects/{receiptId}` reads
only from the already-pinned Memory provider and does not create a missing run binding.

After the commit, a caller-driven relay sends only the receipt ID and contract
versions to the fixed HTTPS Events endpoint. It forwards the existing Knowledge
bearer when one token covers both owners. If separate tokens are already available,
the request may provide the same caller's Events-audience bearer in
`X-Agentweaver-Events-Authorization`; both tokens stay in request memory and are
never stored in the outbox. No token is minted or fetched, and there is no background
relay.

Events fetches the committed receipt from its fixed trusted HTTPS Knowledge address,
rechecks the same issuer, subject, bounds, and current project `WriteProjects`
authority, then constructs and commits a native project fact, project sequence, and
inbox receipt in one Events PostgreSQL transaction. Knowledge marks the outbox
delivered only after validating the persisted Events acknowledgment. A transport,
authorization, or persistence failure leaves the accepted decision and receipt
committed and reports delivery as `PENDING`; a fresh authorized caller can retry the
same receipt. For Cosmos Memory, the accepted-effect receipt and delivery intent are
committed with the promoted records in the selected project's partition, and the
relay claims and acknowledges delivery through that same provider. These project
facts are not session events, do not enter the run journal, and do not create or
change a provider pin.

## Context composition

Context lookup first enforces project, agent, and run scope and a configured maximum
candidate count. The compiler includes active approved project decisions of type
`architectural` or `scope`, active memories for the requested agent plus approved
`cross-team` memories, and only the requested agent's approved SessionContext for the
current run. Pending memories may be included as historical data; they are not
promoted to trusted decisions.

The compiler optionally scores and orders memories by token overlap, importance, and
recency. It emits JSON inside explicit untrusted-context delimiters and instructs
consumers never to follow instructions embedded in stored values. Each selected
decision, memory, and SessionContext includes an immutable revision reference.
Configured item and token limits can only be narrowed by a request; the token limit
is also bounded by the accepted run's prompt-token limit. Candidate overflow and
mandatory-decision or output-budget overflow are visible errors, not silent
truncation of mandatory content.

The public routes and configuration keys are listed in
[contracts and endpoints](../reference/contracts.md#knowledge-and-memory). The test
suite uses disposable PostgreSQL and TestServer owner fakes; see the
[testing guide](../guide/testing.md).
