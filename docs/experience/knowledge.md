# Knowledge

Open **Knowledge** from a project. Records belong to an exact project and agent;
each request also carries the run used to authorize access and select the Memory
provider.

## Select scope and search

1. Enter the **Exact run ID** and **Exact agent ID**.
2. Select **Apply scope**.
3. If prompted, select **Sign in for this project and run**.
4. Search by text or kind. Use **Include inactive records** to include historical
   states.
5. Use **Previous page** and **Next page** to read more results.

The page also accepts `runId` and `agentId` query parameters. A child session's
Topology link supplies its exact session ID as the agent ID; it does not perform
an agent-directory lookup.

Search pages contain up to 50 records. Cards show kind, type, revision, lifecycle,
trust, content, rationale, and tags.

## Create and review records

| Action | Steps and result |
| --- | --- |
| Create | Choose Memory, Proposal, or Session context. Enter type and content, then submit. Title, rationale, importance, and tags are also available. |
| Correct | On an active Memory or Session context record, select **Correct record**, edit fields, enter a reason, and save. |
| Archive | Select **Archive record** on supported active Memory or Session context records. |
| Review a proposal | Select **Promote proposal** or **Reject proposal** on a pending Proposal. |
| Approve a Decision | Select **Approve Decision** on an active Decision with Pending or Legacy trust. |
| Supersede a Decision | Choose another active Decision from the exact-scope search results and select **Supersede with selected Decision**. |

The owner validates state, current revision, and permission for each operation.
The app refreshes results after a write.

## Inspect and restore revisions

Select **View revision history** on a record. History has separate paging controls.
Select **Restore revision** on a historical revision to append a new revision;
the old revision is not overwritten. Restored content is Active with Pending
trust. Approve a restored Decision separately.

## Transfer Knowledge

1. Select **Export scoped Knowledge**.
2. Inspect the versioned JSON and select **Download versioned bundle**.
3. To import, paste a bundle into **Versioned Knowledge bundle JSON**.
4. Confirm that the bundle matches the current project and agent.
5. Select the import control and inspect the refreshed results.

Version 1 bundles contain Memory and Decision records with revision chains.
They are limited to 25 records, 500 revisions, and 1 MiB. Imports require the same
project and agent, reject record collisions, and create Active+Pending records.
Import does not approve a Decision.

## States and errors

- Missing IDs or an unmatched Broker binding prevent scoped requests.
- Search and revision history display independent loading and empty states.
- Scope changes clear stale results and transfer state.
- Invalid bundle JSON, unsupported scope, stale revisions, and owner failures
  appear as errors. A failed write does not change local record state to success.
- The browser rejects records and mutation responses outside the selected
  project and agent.

## Requests

All requests go through the Gateway to Knowledge. The common prefix is
`/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}`.

| Action | Method and path after the prefix |
| --- | --- |
| Search or create | `GET /records` or `POST /records` |
| Correct, archive, or supersede | `PUT /records/{recordId:guid}` |
| Read revisions | `GET /records/{recordId:guid}/revisions` |
| Restore | `POST /records/{recordId:guid}/restore` |
| Approve Decision | `POST /records/{recordId:guid}/approve` |
| Promote or reject Proposal | `POST /proposals/{proposalId:guid}/promote` or `/reject` |
| Export or import | `GET /records/export` or `POST /records/import` |

Writes include `Idempotency-Key`; revision-dependent operations also include
the current expected revision. See [Knowledge and Memory](../architecture/knowledge-memory.md)
for record semantics and [Gateway](../guide/gateway.md) for request limits.
