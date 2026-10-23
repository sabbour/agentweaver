# Knowledge and memory

Use this page to understand how Agentweaver turns Knowledge records into
bounded context for an agent and how it records the history behind memories,
decisions, and accepted proposals. Knowledge is not the run journal.

The service owns authorization, context composition, and the immutable
run-provider binding. The selected Memory provider owns record heads,
revisions, idempotency receipts, and accepted-effect delivery state. Changing
the provider does not move Projects authority or the Events journal into that
backend.

## Find the implementation

All listed files are under `services/knowledge/Agentweaver.Knowledge/`.

| Source | Responsibility |
| --- | --- |
| `Program.cs` and `KnowledgeEndpoints.cs` | Service composition and authenticated routes |
| `KnowledgeApplicationService.cs` and `ProjectsConfigClient.cs` | Scoped operations and current authority |
| `KnowledgeProviderBindingService.cs` | Exclusive provider resolution and immutable binding |
| `NativePostgresMemoryProvider.cs` | Native PostgreSQL records and delivery transactions |
| `KnowledgeDocumentMemoryProviderCore.cs` | Shared document-provider behavior |
| `CosmosMemoryProvider.cs` and `CosmosMemoryDocumentStore.cs` | Cosmos negotiation and document storage |
| `RedisMemoryProvider.cs`, `RedisMemoryDocumentStore.cs`, and `RedisMemoryCommands.cs` | Redis negotiation and atomic operations |
| `MemoryContextCompiler.cs` | Visibility, ranking, and context budget |
| `AcceptedEffectRelay.cs` | Receipt delivery to Events |
| `KnowledgeTransferValidator.cs` and `KnowledgeMigrator.cs` | Transfer validation and explicit schema setup |

## Check access before using records

An agent ID selects a row; it does not prove caller identity. Knowledge obtains
fresh `AccessPrivateKnowledge` permission from Projects & Config for the
project. `ReadProjects` allows metadata access, not private records. The caller
also needs `ReadRunSelection` before Knowledge resolves the run's Memory
provider.

The service forwards the original bearer and optional tenant selector; it
keeps no membership or role cache. Signed project/run bindings must match the
request and owner response. Project-wide search uses an unbound bearer with
current project `AccessPrivateKnowledge` and direct project `ReadRunSelection`.
It searches all agents through existing run-provider bindings, rechecks those
bindings before returning results, and never creates a provider pin. Scoped
search filters by project and agent before counting or returning results.
Direct reads return the same not-found result
for a different requested agent as for an absent record. Revision reads first
verify that the record and agent match.

Project-wide results from distinct provider resources use one stable global
order before paging. Each result's `accessRunId` identifies the existing run
binding that authorizes its history read. The record's `sourceRunId`, when
present, remains separate provenance.

## Keep record history append-only

Updates use an expected revision and idempotency key. Edits, archive,
supersession, approval, and restore append revisions. Restore creates an
Active, Pending-trust head; it does not rewind history. Proposals change
through explicit promotion or rejection, not ordinary record editing.

Versioned Memory and Decision transfers preserve history. Import is bounded,
scoped to a project and agent, and non-merging. It cannot overwrite an
existing record head.

## Select a Memory provider

Before using Knowledge, supply current Projects authority and an accepted
selection that names the run's provider candidate.

1. Read current Projects authority and the immutable run selection.
2. Require exactly one exclusive Memory candidate and match it to the
   configured catalog.
3. Negotiate the live provider resource and supported capabilities.
4. Persist the first immutable project/run binding or verify the existing
   binding.
5. Perform the scoped operation with authority and binding rechecks.

`postgres.native-memory` is the default. `cosmos.memory` and `redis.memory`
implement the same provider contract when configured and selected. Cosmos
verifies container identity, project partition key, indexing, and retention
policy. Redis verifies persistence and namespace compatibility. These
adapters do not replace the PostgreSQL service binding.

Later requests verify provider, options revision, resource generation,
capabilities, and accepted configuration revisions. A changed backend does not
trigger fallback. See the [provider configuration guide](../guide/configure-providers.md)
and [runtime configuration reference](../reference/runtime-configuration.md)
for supported settings.

## Compile context for an agent

`MemoryContextCompiler` handles three inputs:

- Active, approved architectural and scope Decisions form mandatory
  project-wide context.
- Active Memory stays agent-local unless it is approved and tagged
  `cross-team`. Eligible pending local memories remain explicitly untrusted.
- SessionContext uses the latest active, approved record for the requested
  agent and run.

The compiler ranks memories by relevance, importance, time, and stable
identity. It serializes the result as untrusted context and returns revision
references and omission reasons.

The budget uses four characters per approximate token. Mandatory decisions
must fit; overflow raises an error rather than silently dropping them.
Optional memories stop at the item or character limit. SessionContext is
omitted when it does not fit.

The service applies its `DefaultContextTokens` ceiling and the lower accepted
prompt-token cap when present. This retrieval budget is not a hard limit on
every native model request.

## Deliver an accepted project fact

The selected provider commits an accepted-effect receipt and delivery intent
with the local change. `AcceptedEffectRelay` claims the receipt, sends its ID
and contract versions to Events, and validates the returned fact
acknowledgment. Events retrieves the authoritative receipt and admits a
separate project fact. Only then does Knowledge acknowledge provider-owned
delivery state with the lease token.

A transport failure, owner denial, or invalid acknowledgment leaves delivery
pending. Retrying delivery does not recreate the accepted local change.

## Diagnose Knowledge failures

Revoked authority blocks record access. A changed selection or provider pin
blocks an operation rather than moving data to another backend. Revision and
idempotency conflicts remain explicit. Context containing a foreign project
fails instead of being filtered into a successful response.

## Related content

- [Knowledge contracts and provider configuration](../architecture/knowledge-memory.md)
- [Projects and configuration](./projects-config.md)
- [Events and sessions](./events-sessions.md)
- [Persistence](./persistence.md)
