# ADR 0003: Events-owned typed session material

**Status:** Accepted for the minimum turn-content and SDK-cache owner boundary.

This records the existing source-integration decision. It does not accept a
generic raw-content API or attest implementation completeness, deployment, or live
recovery.

## Context

PostgreSQL owns the ordered session journal, while Object Store holds large,
opaque bytes. A journal reference alone cannot prove that the corresponding turn
content or native SDK cache was persisted, authorized, or consumed by a replacement
runtime.

The existing Events owner already controls event identity, references, and
retention. The runtime produces actual conversation content and SDK-supported
cache material; Orchestrator controls current execution and checkpoints. The
minimum integration must join those responsibilities without another service,
provider seam, authority source, or set of Blob credentials in AgentHost.

## Options considered

| Approach | Disposition and trade-off |
| --- | --- |
| Expose generic raw Blob/content access | Not approved. Arbitrary keys, paths, URLs, and container access exceed the bounded session-material contract and do not establish current owner authority. |
| Give runtimes direct container credentials | Excluded. Storage access would bypass the owner-recorded event/reference and current-authority boundary. |
| Extend Events with typed, execution-bound turn/cache operations | Chosen for the minimum integration. Reuses current owners and Object Store, but requires write-before-reference ordering, authority rechecks, interrupted-write reconciliation, and explicit recovery failures. |

## Decision

Events owns persistence and recorded-material reads for the minimum
`TurnContent`/`SdkCache` contract. AgentHost produces actual bytes through the
supported SDK boundary. Orchestrator supplies current execution and checkpoint
authority. Object Store remains an opaque-byte adapter, not an event bus,
workspace, journal, or permission authority.

Accept bounded typed bytes and exact event/session/runtime-revision/fence
identity, never a caller-selected storage key. Validate fresh authenticated
Projects authority and the current Orchestrator runtime binding. Observe-purpose
credentials do not authorize writes.

Write and verify bytes before committing their journal reference. Recheck
authority after asynchronous work and before acknowledgment. An exact retry
recovers the original acknowledgment; changed material is not an identical retry.
There is no atomic Blob/PostgreSQL transaction.

Read only owner-recorded material, rechecking current authorization, reference
retention, scope, digest, and length before disclosure. Optional compatible SDK
cache supports recovery; missing required turn content fails explicitly. Journal
context rebuilding must not invoke a model turn or replay an external effect.

The [session-material specification](../../specs/session-material-persistence-and-recovery.md)
defines the detailed contract. The
[owner retention](../design/session-material-owner-retention.md),
[runtime recovery](../design/session-material-runtime-recovery.md), and
[deployment composition](../design/session-material-deployment-validation.md)
records expand its component boundaries.

## Consequences

- Events remains the authority for journal references and retention. A runtime
  has no unrestricted Blob access and cannot turn an object key into permission.
- Bytes can exist without a committed reference after an interrupted write.
  Durable owner inventory and bounded cleanup reconcile those objects; they are
  not successful session history.
- Objects written before durable inventory cannot be discovered through the
  existing Object Store contract. Historical listing/scanning remains an
  unapproved authority gap, not an implied cleanup permission.
- SDK capture must use supported serialization and compatibility checks. A
  session ID, shared filesystem, or fabricated cache format does not prove
  recovery.
- Controlled owner/runtime tests and the
  [integrated acceptance scenario](../design/session-material-acceptance.md)
  establish different facts. Missing authorized dispatch/readback or fault
  boundaries remain blocked; this decision does not waive them.

## Related decisions

[ADR 0001](0001-platform-architecture.md) remains Proposed. This narrower Accepted
owner-boundary decision does not accept or supersede the platform proposal.
