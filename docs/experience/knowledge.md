# Knowledge

Use this page to search and manage records for an exact project, run, and
agent. The run identifies the authorization context and selected Memory
provider; records belong to the project and agent.

## Prerequisites

- Know the exact run ID and agent ID.
- Have current owner-granted access for the project and run.
- Use a Broker token bound to the exact project and run.

The v1 Knowledge page is backed by the unpublished `Agentweaver.Knowledge`
source candidate. Its route names describe source contracts, not deployed
service availability.

## Select scope and search

1. Open **Knowledge** from a project.
2. Enter **Exact run ID** and **Exact agent ID**.
3. Select **Apply scope**.
4. If prompted, select **Sign in for this project and run**.
5. Search by text or kind. **Include inactive records** is selected by
   default; clear it to omit historical states.
6. Use **Previous page** and **Next page** to read additional results.

The page accepts `runId` and `agentId` query parameters. A Topology link for a
child session supplies that exact session ID as the agent ID; it does not
perform an agent-directory lookup. Search pages contain up to 50 records.
Each result shows kind, type, revision, lifecycle state, trust state, content,
rationale, and tags.

**Expected result:** The page shows results for the exact project and agent
scope. The owner checks current access and run binding for every request.

## Create and review records

The create form supports Memory, Proposal, and Session context records. It
requires a type and content, and includes an importance selection. Title,
rationale, and tags are optional.

| Action | When you can use it |
| --- | --- |
| Correct a record | Correct an active Memory or Session context record. Enter a reason and save the new revision. |
| Archive a record | Archive an active Memory or Session context record. |
| Review a Proposal | Promote or reject a pending Proposal. |
| Approve a Decision | Approve an active Decision with Pending or Legacy trust. |
| Supersede a Decision | Select another active Decision from the exact-scope search results and supersede the current one with it. |

The owner validates current state, revision, and permission for each
operation. The app refreshes results after a write.

## Inspect and restore revisions

1. Select **View revision history** on a record.
2. Use the revision-history paging controls to read more revisions.
3. Select **Restore revision** on a historical revision.

**Expected result:** Restore appends a new revision; it does not overwrite the
historical revision. The restored head is Active with Pending trust. Approve
a restored Decision separately.

## Transfer Knowledge

1. Select **Export scoped Knowledge**.
2. Inspect the versioned JSON and select **Download versioned bundle**.
3. To import, paste a bundle into **Versioned Knowledge bundle JSON**.
4. Confirm that the bundle matches the current project and agent.
5. Select **Import scoped Knowledge** and inspect the refreshed results.

Version 1 bundles contain Memory and Decision records with their revision
chains. A bundle is limited to 25 records, 500 revisions, and 1 MiB. An import
must match the same project and agent, rejects record or revision ID
collisions, and creates Active records with Pending trust. Import does not
approve a Decision.

**Expected result:** The owner imports records atomically and the page refreshes
to show their current state. Existing record histories are not merged or
overwritten.

## Understand states and errors

- Missing IDs or a Broker binding for a different project/run prevent scoped
  requests.
- Search and revision history show independent loading and empty states.
- Changing scope clears stale results and transfer state.
- Invalid bundle JSON, unsupported scope, stale revisions, and owner failures
  appear as errors. A failed write does not appear as a successful local state
  change.
- The browser rejects records and mutation responses outside the selected
  project and agent.

## Requests

All routes use the Gateway. The common prefix is
`/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}`.

| Action | Method and path after the prefix |
| --- | --- |
| Search or create | `GET /records` or `POST /records` |
| Correct, archive, or supersede | `PUT /records/{recordId:guid}` |
| Read revisions | `GET /records/{recordId:guid}/revisions` |
| Restore | `POST /records/{recordId:guid}/restore` |
| Approve a Decision | `POST /records/{recordId:guid}/approve` |
| Promote or reject a Proposal | `POST /proposals/{proposalId:guid}/promote` or `/reject` |
| Export or import | `GET /records/export` or `POST /records/import` |

Writes include `Idempotency-Key`; revision-dependent operations also include
the current expected revision. See [Knowledge and Memory](../architecture/knowledge-memory.md)
for record semantics and [Gateway](../guide/gateway.md) for request limits.
