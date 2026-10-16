# Knowledge and memory

## The mental model

Knowledge turns stored records into bounded context for an agent. It also records the history behind memories, decisions, and accepted proposals. It is not the run journal.

The service owns authorization, context composition, and the immutable run-provider binding. The selected Memory provider owns record heads, revisions, idempotency receipts, and accepted-effect delivery state. Switching the provider does not move Projects authority or the Events journal into that backend.

## Where this lives

All service paths below are under `services/knowledge/Agentweaver.Knowledge/`.

- `Program.cs` and `KnowledgeEndpoints.cs` — composition and authenticated routes.
- `ProjectsConfigClient.cs` — current authority and accepted selection.
- `KnowledgeApplicationService.cs` — scoped operations and authorization rechecks.
- `KnowledgeProviderBindingService.cs` — exclusive provider resolution and immutable binding.
- `NativePostgresMemoryProvider.cs` — native PostgreSQL record and delivery transactions.
- `KnowledgeDocumentMemoryProviderCore.cs` — common document-provider behavior.
- `CosmosMemoryProvider.cs` and `CosmosMemoryDocumentStore.cs` — Cosmos negotiation and document storage.
- `RedisMemoryProvider.cs`, `RedisMemoryDocumentStore.cs`, and `RedisMemoryCommands.cs` — Redis negotiation and atomic operations.
- `MemoryContextCompiler.cs` — visibility, ranking, and context budget.
- `AcceptedEffectRelay.cs` — receipt delivery to Events.
- `KnowledgeTransferValidator.cs` and `KnowledgeMigrator.cs` — transfer validation and explicit schema setup.

## Core concepts and invariants

### An agent ID is a row selector, not identity

Knowledge obtains fresh `AccessPrivateKnowledge` for the project from Projects & Config. `ReadProjects` permits metadata access, not private records. The caller also needs `ReadRunSelection` before the service resolves the run's Memory provider.

The service forwards the original bearer and optional tenant selector. It keeps no membership or role cache. Signed project/run bindings must match the request and owner response. A route's `agentId` does not prove that the caller is that agent.

Search filters by project and agent before counting or returning results. Direct reads hide records for another requested agent with the same not-found result as an absent record. Revision reads first verify that record/agent match.

### History is append-only

Updates use an expected revision and an idempotency key. Edits, archive, supersession, approval, and restore append revisions. Restore creates an Active, Pending-trust head; it does not rewind history. Proposals change through explicit promotion or rejection, not ordinary record editing.

Versioned Memory/Decision transfers preserve history. Import is bounded, project/agent scoped, and non-merging. It cannot overwrite an existing record head.

## Provider selection and record operations

1. Read current Projects authority and the immutable run selection.
2. Require exactly one exclusive Memory candidate and match it to the configured catalog.
3. Negotiate the live provider resource and supported capabilities.
4. Persist the first immutable project/run binding or verify the existing binding.
5. Perform the scoped operation with authority and binding rechecks.

`postgres.native-memory` is the default. `cosmos.memory` and `redis.memory` implement the same provider contract when configured and selected. Cosmos verifies the container identity, project partition key, indexing, and retention policy. Redis verifies persistence and namespace compatibility. These adapters do not replace the PostgreSQL service binding.

Later requests verify the selected provider, options revision, resource generation, capabilities, and accepted configuration revisions. A changed backend does not trigger fallback.

## Context composition

`MemoryContextCompiler` handles three inputs:

- Active, approved architectural and scope Decisions form mandatory project-wide context.
- Active Memory stays agent-local unless it is approved and tagged `cross-team`. Eligible pending local memories remain explicitly untrusted.
- SessionContext uses the latest active, approved record for the requested agent and run.

The compiler ranks memories by relevance, importance, time, and stable identity. It serializes the result as untrusted context and returns revision references and omission reasons.

The budget uses four characters per approximate token. Mandatory decisions must fit; an overflow raises an error instead of silently dropping them. Optional memories stop at the item or character limit. SessionContext is omitted when it does not fit.

The service applies its `DefaultContextTokens` ceiling and the lower accepted prompt-token cap when present. This retrieval budget is not a hard limit on every native model request.

## From an accepted proposal to a project fact

The selected provider commits the accepted-effect receipt and delivery intent with the local change. `AcceptedEffectRelay` claims that receipt, sends its ID and contract versions to Events, and validates the returned fact acknowledgment. Events retrieves the authoritative receipt and admits a separate project fact.

Only then does Knowledge acknowledge provider-owned delivery state with the lease token. A transport failure, owner denial, or invalid acknowledgment leaves delivery pending. Retrying delivery does not recreate the accepted local change.

## Failure behavior

Revoked authority blocks record access. A changed selection or provider pin blocks the operation rather than moving data to another backend. Revision and idempotency conflicts remain explicit. Context that contains a foreign project fails instead of being filtered into a success.

## Related pages

- [Knowledge contracts and provider configuration](../architecture/knowledge-memory.md)
- [Projects and configuration](./projects-config.md)
- [Events and sessions](./events-sessions.md)
- [Persistence](./persistence.md)
