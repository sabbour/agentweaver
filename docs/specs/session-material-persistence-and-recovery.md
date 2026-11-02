# Specification: Session material persistence and recovery

**Status:** Existing owner/runtime contract implemented in source. Controlled
coverage does not establish a deployed persistence or recovery result.

For Events, Orchestrator, and native runtime contributors. This specification
collects the existing turn/cache boundary; it does not approve a generic
raw-content API or broaden other material kinds.
[ADR 0003](../architecture/decisions/0003-events-owned-session-material.md)
records the narrower Accepted ownership decision; implementation and live
acceptance remain separate.

## Problem and goals

The journal must retain ordered conversation history while object storage holds
the actual bytes. A replacement runtime needs verified history and an optional
compatible SDK cache without direct Blob authority or replaying external effects.
Interrupted writes and expired references must not become successful durable
history or unauthorized disclosure.

## Non-goals

- Replace the native PostgreSQL journal with Blob, a shared workspace, or an SDK
  cache.
- Give AgentHost account keys, SAS values, unrestricted container access, or a new
  authority source.
- Provide arbitrary-key, URL, path, or container reads.
- Make Blob and PostgreSQL one atomic transaction.
- Reconstruct missing history, rewrite opaque SDK bytes to fabricate safety, or
  rerun model/tool effects during recovery.
- Add another service, provider seam, deletion endpoint, or live acceptance gate.

## Intended behavior and requirements

### Write through the Events owner

The native runtime produces actual `TurnContent` and `SdkCache` bytes using the
supported SDK boundary. Requests carry event/session identity, current runtime
registration revision, execution fence, and kind-specific data, not a storage key.
Current Projects authorization and the Orchestrator execution binding authorize
the write. An observe credential is insufficient.

The existing material contract uses version `1`, nonempty bytes of at most
`1024 * 1024`, a positive registration revision and execution fence, and a
nonempty event/runtime identity. Turn roles are exactly `user` or `assistant`;
non-turn material cannot carry a role.

Events records an inventory candidate, writes create-only bytes under its own
stable scope/event/content identity, verifies them, and only then commits the
journal reference. It rechecks current authority after asynchronous work and
before acknowledgment. A lost acknowledgment can be retried with the same event
and bytes to recover the original position. Changed content or owner binding is
not the same retry.

### Read recorded material under current authority

Resolve material from the recorded event and exact session, not a caller-selected
object key. Verify scope, digest, byte length, current authority, and retention
before disclosure. Read authority is kind-specific: historical turn-content
access does not grant SDK-cache or other private material access.

Retention belongs to each event reference. The deadline is expired when it is
equal to or earlier than PostgreSQL time. Check it before storage retrieval and
again after retrieval and integrity checks. Another live reference may preserve
the object bytes without extending this reference's read permission.

Expired required turns fail explicitly. Missing or expired optional SDK cache
can use the existing not-found recovery response. Both responses are `no-store`;
authorization and integrity failures must not become optional-cache absence.

### Recover without replay

Use a cache only after checking session/tenant scope and its accepted selection,
SDK/runtime versions, model identity and bindings, reasoning effort, and prompt
capacity against the current recovery context. The runtime still requires a
current registration and execution fence.

Absent, expired, or incompatible optional cache may rebuild context from the
owner's ordered journal and verified required turns. A cache that claims
compatibility but is corrupt or unsafe fails explicitly. Bounded archive/path and
known-credential checks are not a general secret scanner; reject unsafe opaque
bytes rather than edit them.

Rebuilt context is marked as prior conversation history. It sends no model turn,
dispatches no tool, and repeats no external effect. Manifest-based recovery hashes
only through the recorded checkpoint boundary; a missing boundary is a failure,
not evidence for a partial journal.

## Contracts and boundaries

Events owns journal order, references, retention, object identity, and material
acknowledgments. Orchestrator owns current execution, selection, registration,
and checkpoint authority. The native runtime owns SDK capture/restore and
compatibility checks. Object Store holds opaque bytes and supplies no independent
authorization.

The deployment uses Events' workload identity and private container-scoped access.
Its separate migration identity applies the existing inventory schema and runtime
grants. Readiness and provider catalog entries do not prove a typed storage
operation. See [deployment composition](../architecture/design/session-material-deployment-validation.md).

## Failure and recovery

An object without a committed journal reference is not durable session history.
Revocation after storage work prevents a later commit or disclosure. Old
execution fences and changed payloads cannot revive a failed operation.

Cleanup uses bounded inventory batches, row locks, and current reference
deadlines. A live reference preserves bytes. Record a deletion tombstone only
after the delete returns; after response/commit loss, retry the same known key.
An already-absent object can be tombstoned safely. The lock order prevents a new
reference from committing after completed deletion.

Historical objects without inventory remain outside automatic cleanup. The
Object Store contract has no listing operation; do not scan a container or guess
keys.

## Acceptance and validation

| Criterion | How to verify | Evidence boundary |
| --- | --- | --- |
| Retries recover the original acknowledgment; changed payloads, stale fences, and revocation fail | Events material PostgreSQL/failure-recovery tests with controlled storage and replacement owners | Disposable PostgreSQL and fake storage are not live Blob acceptance |
| Inclusive expiry, delayed-read expiry, independent references, and cleanup/write races preserve authority | Owner retention and lock-order tests | Object existence alone is not read permission |
| SDK recovery uses compatible cache or verified turns without sending another turn | Native SDK filesystem/recovery tests and owner integration cases | Controlled SDK transport does not prove paid model or deployed recovery |
| The acceptance invocation is joined to its actual material and replacement runtime | [Integrated scenario](../architecture/design/session-material-acceptance.md) | Blocked while its supported dispatch/readback and fault boundaries are missing |

## Rollout and compatibility

Retain the existing typed owner routes, material version, migration ownership,
and SDK-supported capture/restore path. Do not weaken existing authorization to
complete an acceptance scenario. Database maintenance, workload rollout, live
storage effects, and cleanup execution require their own authorized operations.

Detailed contracts remain in [owner retention](../architecture/design/session-material-owner-retention.md)
and [runtime recovery](../architecture/design/session-material-runtime-recovery.md).
