# PostgreSQL and Blob

`Agentweaver.Persistence.Postgres` stores outbox events and consumer inbox receipts in a service-owned schema. It does not create domain tables or a relay service.

<figure class="aw-diagram">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.png'" alt="PostgreSQL delivery flow. A caller writes domain state and an outbox event in one transaction. A relay publishes leased events and acknowledges with a fencing token. Consumers admit a message receipt with domain effects in one transaction." />
  </a>
  <figcaption>PostgreSQL transactions couple local state to outbox writes and inbox admission. Transport delivery remains at least once.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-outbox-inbox.drawio'">Open editable draw.io source</a></p>

Call `EnqueueAsync` with the same open connection and transaction as the domain state change. PostgreSQL assigns stream sequence numbers and enforces stable event IDs and idempotency keys.

`ClaimAsync` leases the earliest undelivered event per stream. `AcknowledgeAsync` accepts only a current, unexpired lease token.

`OutboxRelay` publishes through an injected `IOutboxPublisher`, then acknowledges the lease. The host calls `RelayOnceAsync`. The library does not start a daemon or provide exactly-once delivery.

Call `AdmitAsync` with the same transaction as consumer state and any new outbox event. Apply effects only for `InboxAdmission.Admitted`. Skip effects for `InboxAdmission.Duplicate`. Acknowledge delivery after commit.

Delivery remains at least once. The inbox prevents duplicate committed local effects when the consumer gates all effects on the receipt and transaction.

`Agentweaver.ObjectStore.AzureBlob` implements `IObjectStore` for opaque platform objects. It does not provide agent workspace storage, create a container, authorize callers, or fall back to disk.

The Blob container exists before adapter use. The caller owns authorization, authoritative references, retention, and upload-stream lifetime. Blob writes do not commit PostgreSQL references.
