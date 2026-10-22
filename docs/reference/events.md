# Events reference

Use this reference to read v1 session-journal events. It describes the envelope,
payload kinds, replay routes, live delivery, and transactional outbox. An
**envelope** is the owner-written record that identifies an event and contains
its typed payload.

For provider setup, see [Configure providers](../guide/configure-providers.md).
For runtime settings, see
[Runtime configuration](./runtime-configuration.md). For provider binding,
receipt admission, storage, and recovery, see
[Events & Sessions architecture](../architecture/events-sessions.md).

## Journal envelope

The `SessionEventEnvelope` contract is defined in
[`SessionsContracts.cs`](https://github.com/sabbour/agentweaver/blob/v1/packages/Agentweaver.Abstractions/SessionsContracts.cs).

| JSON field | Meaning |
| --- | --- |
| `schemaVersion` | Envelope schema version; current value is `1`. |
| `eventVersion` | Payload version; current value is `2`. |
| `eventId` | Stable GUID for idempotent append and client deduplication. |
| `identity` | Exact `projectId`, `runId`, and `sessionId`. |
| `position` | Owner-assigned, increasing project/run journal position. |
| `occurredAt` | Owner-assigned time. Do not use it as the ordering key. |
| `kind` | Event enum value derived from the payload type. |
| `payload` | Typed payload with its own `kind` discriminator. |
| `objectReferences` | Stored references and owner-controlled retention metadata. |

The HTTP serializer uses camel-case enum names. For example, envelope `kind`
is `toolCall`, while `payload.kind` is `tool_call`. Outbox event-type strings
use the C# enum spelling shown below, not the HTTP spelling.

Provider selection uses the shared `SessionsCapabilities` identifiers in
`Agentweaver.Abstractions`. The current Events & Sessions host binds the native
PostgreSQL journal as its default and does not route runs to the optional
AgentSessions implementation.

## Event kinds

All fields listed here are inside `payload`, after the discriminator.
Object-reference fields hold metadata, not inline object bytes.

| C# kind | `payload.kind` | Fields and purpose |
| --- | --- | --- |
| `Turn` | `turn` | `role`, `content`: turn content reference. Roles are `user`, `assistant`, `system`, or `tool`. |
| `ToolCall` | `tool_call` | `callId`, `toolName`, `state`, `arguments`, `result`: tool lifecycle and optional references. States are `requested`, `accepted`, `completed`, or `failed`. |
| `PolicyEvaluation` | `policy_evaluation` | `actorId`, `tenantId`, `stepId`, `grantId`, `grantRevision`, `purpose`, `actionId`, `outcome`, `reasonCode`, `fence`, `providerId`, `adapterVersion`, `optionsSchemaVersion`, `optionsRevision`: redacted policy evidence. |
| `DecisionAccepted` | `decision_accepted` | `decisionId`, `decisionType`, `selectedOption`, `rationale`, `acceptedEffectIds`: accepted decision and referenced effects. |
| `EffectAccepted` | `effect_accepted` | `effectId`, `effectType`, `receipt`: accepted effect and optional receipt reference. |
| `ArtifactReference` | `artifact_reference` | `artifact`: stored artifact reference. |
| `CacheReference` | `cache_reference` | `cache`, `runtimeVersion`, `bindingId`: SDK cache and compatibility binding. |
| `AddressedMessage` | `addressed_message` | `messageId`, `sender`, `recipient`, `threadId`, `threadSequence`, `purpose`: link to a routed message. |
| `ProducedRunCapture` | `produced_run_capture` | `capture`, `package`: Source Control output proof and exact package reference. |
| `RuntimeConfiguration` | `runtime_configuration` | `configuration`: private accepted configuration reference; public projection returns `null`. |

Event version `1` supports the kinds above except `PolicyEvaluation`,
`ProducedRunCapture`, and `RuntimeConfiguration`. Version `2` supports all ten.
An unsupported schema or event version returns `422 unsupported_contract_version`.

`PolicyEvaluationOutcome` values are `Allow`, `Deny`, and `Error`. Reason codes
are `Allowed`, `DefaultDeny`, `NoEffectiveGrant`, `PlatformRuleDenied`,
`ProjectRuleNarrowed`, `StaleFence`, `ProviderUnavailable`, and `EvaluationFailed`.
The record is evidence, not an action grant.

`ProducedRunCaptureProof` has its own contract version, currently `2`. It pins
identity, actor and tenant, selection hash, Source Control pin, Workspace
incarnation and generation, Git object IDs, package/manifest/patch hashes and
lengths, and capture time. The event version and capture contract version are
separate fields.

## Object references and public projection

`SessionObjectReference` contains an opaque `key`, `purpose`, optional
`byteLength`, and optional typed `material`. Stored references also include
`retention.ownerId` and `retention.retainUntil`.

Ordinary journal reads do not disclose private runtime configuration or reviewed
remote tool result references:

- Runtime configuration events retain identity and position, but return
  `configuration: null` and no object references.
- Admitted private remote tool results become completed `ToolCall` events with
  `toolName: "external-tool"`, an event-derived call ID, null arguments/result,
  and no object references.
- Invalid private reference combinations fail integrity validation instead of
  passing through.

The browser cannot recover transcript or private result bytes from these
projections. Private material has separate authenticated owner routes.

## Replay and live delivery

| Surface | Route | Format |
| --- | --- | --- |
| Gateway replay | `GET /api/v1/projects/{projectId}/runs/{runId}/events` | `{ events, nextCursor, hasMore }` |
| Gateway live | `GET /api/v1/projects/{projectId}/runs/{runId}/events/live` | `text/event-stream` |
| Native session replay | `GET /internal/sessions/{sessionId}/events` | Same event-page DTO |
| Native run replay | `GET /internal/projects/{projectId}/runs/{runId}/events` | Same event-page DTO |
| Native session live | `GET /internal/sessions/{sessionId}/events/live` | `application/x-ndjson` deliveries |
| Native run live | `GET /internal/projects/{projectId}/runs/{runId}/events/live` | `application/x-ndjson` deliveries |

Replay accepts `cursor` and `limit`. The default limit is 100; the journal
accepts 1 through 200. Cursors are opaque and scope-bound. Do not construct or
reuse a cursor for another session or run.

Gateway server-sent events (SSE) use the journal cursor as the `id` and
`session-event` as the event name. Reconnect with `Last-Event-ID` or `cursor`.
The Gateway reads committed owner replay pages and rechecks current access
before delivery.

Native live routes accept `cursor`, `maximumEvents`, and
`maximumDurationSeconds`. Defaults are 1,000 events and 300 seconds; maximums
are 10,000 and 300. Each NDJSON delivery contains `event` and `nextCursor`.

Read all replay pages, then follow live delivery from the final cursor.
Deduplicate overlap by `eventId`, and order by `position`. The browser implements
this in `useRunJournal.ts`; a timestamp does not resolve ordering.

## Addressed messages

Addressed-message records have their own thread order and lifecycle:

| Enum | Values |
| --- | --- |
| `AddressedMessageDeliveryMode` | `Immediate`, `Enqueue` |
| `AddressedMessagePurpose` | `Progress`, `Handoff`, `NeedsInput`, `Error`, `Steering`, `Question`, `ApprovalRequest`, `Proposal` |
| `AddressedMessageKind` | `Text`, `Steering`, `Question`, `Approval`, `Proposal` |
| `AddressedMessageStatus` | `Accepted`, `Claimed`, `Delivered`, `Acknowledged`, `Expired`, `Undeliverable` |
| `AddressedMessageFailureReason` | `StaleFence`, `TargetCancelled`, `TargetCompleted`, `RecipientUnavailable` |

A message's `threadSequence` is not a journal `position` or an outbox
`sequence`. Acceptance does not report presentation or acknowledgment.

## Transactional outbox

The PostgreSQL library defines `OutboxEvent`, not a fixed domain event taxonomy.
It stores `Id`, `StreamId`, `IdempotencyKey`, `EventType`, `EventVersion`,
`Payload`, and `OccurredAt`. `StoredOutboxEvent` adds a monotonic stream
`Sequence`. Claiming adds `LeaseToken`, `WorkerId`, and `LeasedUntil`.

The Events owner writes these concrete event types:

| Event type | Stream | Payload |
| --- | --- | --- |
| `sessions.event.Turn` | `sessions/{projectId}/{runId}` | Journal envelope |
| `sessions.event.ToolCall` | Same journal stream | Journal envelope |
| `sessions.event.PolicyEvaluation` | Same journal stream | Journal envelope |
| `sessions.event.DecisionAccepted` | Same journal stream | Journal envelope |
| `sessions.event.EffectAccepted` | Same journal stream | Journal envelope |
| `sessions.event.ArtifactReference` | Same journal stream | Journal envelope |
| `sessions.event.CacheReference` | Same journal stream | Journal envelope |
| `sessions.event.AddressedMessage` | Same journal stream | Journal envelope |
| `sessions.event.ProducedRunCapture` | Same journal stream | Journal envelope |
| `sessions.event.RuntimeConfiguration` | Same journal stream | Journal envelope |
| `sessions.addressed_message` | `addressed-messages/{projectId}/{runId}` | Addressed-message record |

Journal and outbox insertion share the owner transaction. Outbox delivery is
at least once. Consumers use inbox admission in the same transaction as their
effects. Relay outcomes distinguish `Acknowledged`, `AcknowledgmentFenced`, and
`PublishFailed`; a failed or fenced acknowledgment permits redelivery.

## Source

- [Sessions contracts](https://github.com/sabbour/agentweaver/blob/v1/packages/Agentweaver.Abstractions/SessionsContracts.cs)
- [Produced capture contracts](https://github.com/sabbour/agentweaver/blob/v1/packages/Agentweaver.Abstractions/ProducedRunCaptureContracts.cs)
- [Addressed-message contracts](https://github.com/sabbour/agentweaver/blob/v1/packages/Agentweaver.Abstractions/AddressedMessageContracts.cs)
- [Events owner](https://github.com/sabbour/agentweaver/tree/v1/services/events-and-sessions/Agentweaver.EventsAndSessions)
- [PostgreSQL outbox](https://github.com/sabbour/agentweaver/blob/v1/packages/Agentweaver.Persistence.Postgres/OutboxContracts.cs)
