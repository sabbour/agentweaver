# PostgreSQL and Blob Storage

Use this reference to understand how Agentweaver separates durable event
delivery from large-object storage. `Agentweaver.Persistence.Postgres` stores
outbox events and consumer inbox receipts in a service-owned schema. An outbox
records work with the producer's database change; an inbox records a consumer's
admission of that work. The library does not create domain tables or start a
relay service. For provider selection, see the planned
[provider configuration guide](../guide/configure-providers.md).

The separate `Agentweaver.EventsAndSessions` service owns its `events_sessions` schema
by default, including the journal, session/provider bindings, and object-reference
retention records. Its explicit migration composes the outbox/inbox schema with those
domain tables; ordinary service startup verifies the schema and does not migrate it.
The journal reference describes append, replay, live cursors, and the migration
boundary in detail.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.png'" alt="Independent caller-owned producer and consumer transactions that use PostgresOutbox. EnqueueAsync stores the event with producer state; AdmitAsync applies effects only for Admitted, skips duplicates, and acknowledges consumer delivery after commit. ClaimOneAsync leases a selected event only at its stream head." />
  </a>
  <figcaption>Separate producer and consumer transactions couple local state to outbox writes and inbox admission. The host-driven relay is shown separately; transport delivery remains at least once.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.drawio'">Open editable draw.io source</a></p>

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-relay.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-outbox-relay.png'" alt="The Orchestrator leases one selected stream-head event through ClaimOneAsync for synchronous admission. Separately, a caller invokes OutboxRelay.RelayOnceAsync to claim batches, publish through IOutboxPublisher, and acknowledge confirmed publishes with the current lease token." />
  </a>
  <figcaption>The library does not start a daemon. The targeted owner claim and caller-driven batch relay use the same fenced lease; relay acknowledgment follows confirmed publication and is distinct from a consumer acknowledging delivery after its transaction commits.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-relay.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-relay.drawio'">Open editable draw.io source</a></p>

Call `EnqueueAsync` with the same open connection and transaction as the domain state change. PostgreSQL assigns stream sequence numbers and enforces stable event IDs and idempotency keys.

`ClaimAsync` leases the earliest undelivered event per stream. `ClaimOneAsync` leases a
requested event only when it is next in its stream and its prior lease is absent or
expired; it returns `null` when the event cannot be claimed. Both return a lease token,
and `AcknowledgeAsync` accepts only a current, unexpired token. The Orchestrator uses
the single-event claim for synchronous admission of its selected owner-outbox message;
it does not add a background relay.

`OutboxRelay` publishes through an injected `IOutboxPublisher`, then
acknowledges only confirmed publishes with the current lease token. A failed,
cancelled, or fenced publish remains unacknowledged and can be reclaimed; the
transport can finish a publish externally before a failure. The host calls
`RelayOnceAsync`. The library does not start a daemon or provide exactly-once
delivery.

Call `AdmitAsync` with the same transaction as consumer state and any new outbox event. Apply effects only for `InboxAdmission.Admitted`. Skip effects for `InboxAdmission.Duplicate`. Acknowledge delivery after commit.

Delivery remains at least once. The inbox prevents duplicate committed local effects when the consumer gates all effects on the receipt and transaction.

`Agentweaver.ObjectStore.AzureBlob` implements `IObjectStore` for opaque platform
objects. It does not provide agent workspace storage, create a container, authorize
callers, or fall back to disk. The container must exist before the adapter is used.
Blob writes do not commit PostgreSQL references.

The Events & Sessions host composes the adapter with its typed session-material
owner when an explicit HTTPS container is configured. The host's workload identity
receives Blob access for that container; AgentHost and runtime clients use the
owner's authenticated routes and do not receive Blob credentials or direct
container access. The foundation Bicep creates the private `session-material`
container and assigns its role at container scope. These source definitions do not
prove that the container or role assignment has been deployed.

Session event payloads store typed opaque `ObjectKey` references with purpose and
optional byte length. PostgreSQL stores the ordered event, reference, and retention
metadata; Blob stores only the referenced bytes. The trusted Events owner controls
authorization and access to those bytes. See [Events & Sessions](events-sessions.md).

## Events and Blob have different jobs

**Events & Sessions is the journal service. Azure Blob is the large-object backend.**
They are not competing event stores.

| Location | Stores | Authority |
| --- | --- | --- |
| Events PostgreSQL schema | Ordered events, session identity, usage, object keys, purpose, length, and retention references | Events owns journal order and run artifact references. |
| Azure Blob through `IObjectStore` | Opaque conversation caches, large logs, and run artifacts | Blob stores bytes. The trusted owner controls access and lifetime. |
| Orchestrator PostgreSQL schema | MAF checkpoint identity, workflow position, and optional cache references | Orchestrator owns workflow recovery, not the Events journal. |
| Azure Files through Storage | Agent-visible repository and workspace files | Environment owns volume bindings. These workspace files are not platform object blobs. |

For example, a journal entry can record that a turn completed and refer to an object
containing its large output. PostgreSQL answers what happened and in which order.
Blob supplies the referenced bytes when an authorized caller requests them.
A normal small event does not require a Blob object.

## Production session-material composition

The existing foundations are the PostgreSQL journal and reference records, the
`IObjectStore` contract, and the create-only Azure Blob adapter. The Events &
Sessions host composes them through its typed owner when the explicit container
setting is present. This is source composition, not evidence of a deployed route
or Azure resource.

The general runtime material route accepts only typed `TurnContent` and `SdkCache`
bytes with bound event, session, runtime revision, and execution-fence identities.
Events generates the object key and records the kind, digest, length, revisions,
and SDK/model binding. A caller-supplied key is never proof that bytes were stored
or that the caller may read them. Other protected material routes remain separate
owner contracts; this boundary does not authorize a generic raw-content or Azure
proxy API.

Writes require current authenticated project/run authority and the Orchestrator's
current runtime registration, source receipt, and writer fence. Events checks that
authority before storage, again after storage, and before the journal transaction
commits. An Observe-purpose credential is insufficient. An identical retry returns
the original acknowledgment; changed material for the same event conflicts.

The write order is deliberate: Events first creates the immutable Blob object, then
commits the ordered event, object reference, and retention metadata in PostgreSQL.
The caller receives a success acknowledgment only after the journal commit. A Blob
failure is returned explicitly and creates no journal reference. A journal failure
is also returned explicitly, but a Blob object may remain unreferenced. PostgreSQL
and Blob do not share an atomic transaction, and an uncertain commit must not be
treated as proof that no reference exists.

```mermaid
sequenceDiagram
    participant Runtime as AgentHost or runtime client
    participant Events as Events & Sessions owner
    participant Projects as Projects owner
    participant Core as Orchestrator owner
    participant Objects as IObjectStore
    participant Blob as Azure Blob
    participant PG as Events PostgreSQL
    Runtime->>Events: Typed bytes with event, session, and fence
    Events->>Projects: Check current actor and project/run authority
    Events->>Core: Check current registration, source, and writer fence
    Events->>Objects: Create immutable object with owner-generated key
    Objects->>Blob: Write opaque bytes to configured container
    alt Blob write fails
        Blob-->>Objects: Storage error
        Objects-->>Events: Write failure
        Events-->>Runtime: Explicit failure; no journal reference
    else Blob write succeeds
        Blob-->>Objects: Write completed
        Objects-->>Events: Stored object
        Events->>Projects: Recheck current authority after storage
        Events->>Core: Recheck registration and fence
        Events->>PG: Commit ordered event, reference, and retention
        alt Journal commit fails
            PG-->>Events: Commit failure or uncertain outcome
            Events-->>Runtime: Explicit failure; no success acknowledgment
            Note over Events,Blob: Bytes may remain; no cross-store rollback
        else Journal commit succeeds
            PG-->>Events: Committed position and reference
            Events-->>Runtime: Typed acknowledgment; no Blob credential
        end
    end
    Runtime->>Events: Read by session, event, and kind
    Events->>Projects: Check current read authority
    Events->>PG: Verify pinned provider; resolve reference and retention owner
    Events->>Objects: Read exact owner-recorded key
    Objects->>Blob: Read opaque bytes
    Blob-->>Objects: Bytes
    Objects-->>Events: Bytes and length
    Events->>Events: Verify digest and length; recheck authority
    Events-->>Runtime: Authorized material bytes
```

Reads resolve committed material by session, event, and kind. The caller cannot
choose an object key, path, URL, or container. Events checks current read authority
and the pinned Sessions provider, retrieves the exact reference, verifies its
length and digest, and checks authority again before returning bytes. Historical
turn reads and execution-bound cache reads use their respective current authority
rules. Cache restore also requires compatible runtime and model bindings.

PostgreSQL owns each reference's retention owner and expiry. The current source
records that metadata but does not run Blob retention deletion. Any later cleanup
must re-read the current Events-owned reference and retention state, confirm the
object is no longer retained or referenced, and delete only that exact key under
the owner boundary. Blob does not choose which run data to delete.

The current source also has no durable inventory or enumeration path for unreferenced
uploads. `IObjectStore` can create, read, and delete a known key, but it cannot list
candidate objects; Events has no persistent orphan-reconciliation record. Therefore
the service cannot discover every orphan after restart. Do not sweep the container
or delete a key based on caller input. Reconciliation must remain Events-owned and
must prove, from current PostgreSQL state, that an exact candidate has no committed
reference before deletion. This limitation is unresolved in the current contract;
it does not authorize a new public API or broader Blob access.

A cache must come from actual SDK-supported serialization through the runtime's
custom filesystem. Do not rewrite an opaque SDK payload to fabricate safety or
claim portable restore without SDK support. Missing, incompatible, or unpersistable
cache requires actual journal-context rebuilding, without model or external-effect
replay. A missing required artifact is an explicit failure, not an empty successful
read.
