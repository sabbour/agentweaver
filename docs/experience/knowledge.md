# Knowledge

Use this page to search and manage memories for a project, run, and agent.
The page is named **Memories** in the app. The run sets the authorization
context and the Memory provider. Records belong to the project and agent.

## Prerequisites

- Open the run once in this browser so it appears in **Recent runs in this
  browser**.
- Have current owner-granted access for the project and run.
- Use a Broker token bound to the project and run.

The v1 Memories page is backed by the unpublished `Agentweaver.Knowledge`
source candidate. Its route names describe source contracts, not deployed
service availability.

## Select scope and search

1. Open **Memories** from a project.
2. Under **Show memories for**, choose a **Run**. The list contains the runs
   you opened in this browser.
3. Choose an **Agent**. The list contains the agents in the project
   configuration.
4. Select **Show memories**.
5. If prompted, select **Sign in for this project and run**.
6. Choose a kind tab: **All**, **Memories**, **Decisions**, **Proposals**, or
   **Session context**.
7. Enter **Search text** and select **Search**. **Include inactive records**
   is selected by default; clear it to omit historical states.
8. Use **Previous page** and **Next page** to read more results. Select
   **Refresh** to read the results again.

If you haven't opened a run in this browser, the page shows **No runs yet**
with a link to **Orchestrations**. You don't type a run ID.

The page accepts `runId` and `agentId` query parameters. The **Memories for
this session** link in **Topology** and the **View memories** link on
**Agents** fill them in. Search pages contain up to 50 records. Each result
shows kind, type, revision, lifecycle state, trust state, content, rationale,
and tags.

**Expected result:** The page shows results for the selected project and
agent. The owner checks current access and run binding for every request.

## Create and review records

Use **Create a Knowledge record** to add a Memory, Proposal, or Session
context record. Type and content are required. Choose an importance. Title,
rationale, and tags are optional. Select **Create record**.

| Action | When you can use it |
| --- | --- |
| Correct a record | Correct an active Memory or Session context record. Enter a reason and save the new revision. |
| Archive a record | Archive an active Memory or Session context record. |
| Review a Proposal | Promote or reject a pending Proposal. |
| Approve a Decision | Approve an active Decision with Pending or Legacy trust. |
| Supersede a Decision | Select another active Decision from the search results and supersede the current one with it. |

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
