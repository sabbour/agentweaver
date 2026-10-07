# PostgreSQL and Blob

`Agentweaver.Persistence.Postgres` stores outbox events and consumer inbox receipts in a service-owned schema. It does not create domain tables or a relay service.

The separate `Agentweaver.EventsAndSessions` service owns its `events_sessions` schema
by default, including the journal, session/provider bindings, and object-reference
retention records. Its explicit migration composes the outbox/inbox schema with those
domain tables; ordinary service startup verifies the schema and does not migrate it.
The journal reference describes append, replay, live cursors, and the migration
boundary in detail.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.png'" alt="Independent caller-owned producer and consumer transactions using PostgresOutbox. EnqueueAsync stores the event with producer state; AdmitAsync applies effects only for Admitted, skips duplicates, and acknowledges consumer delivery after commit. ClaimOneAsync leases a selected event only at its stream head." />
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

`OutboxRelay` publishes through an injected `IOutboxPublisher`, then acknowledges only confirmed publishes with the current lease token. A failed, cancelled, or fenced publish remains unacknowledged and can be reclaimed; a publish may still have completed externally before a failure. The host calls `RelayOnceAsync`. The library does not start a daemon or provide exactly-once delivery.

Call `AdmitAsync` with the same transaction as consumer state and any new outbox event. Apply effects only for `InboxAdmission.Admitted`. Skip effects for `InboxAdmission.Duplicate`. Acknowledge delivery after commit.

Delivery remains at least once. The inbox prevents duplicate committed local effects when the consumer gates all effects on the receipt and transaction.

`Agentweaver.ObjectStore.AzureBlob` implements `IObjectStore` for opaque platform objects. It does not provide agent workspace storage, create a container, authorize callers, or fall back to disk.

The Blob container exists before adapter use. The caller owns authorization, authoritative references, retention, and upload-stream lifetime. Blob writes do not commit PostgreSQL references.

Session event payloads store large content as typed opaque `ObjectKey` references with
purpose and optional byte length; the journal stores and returns references and
retention metadata, not the referenced bytes. Object upload, authorization, and blob
deletion are outside this service slice. See [Events & Sessions](events-sessions.md).
