# Knowledge

Use this page to discover memories across a project and manage records for a
selected run and agent. The page is named **Memories** in the app. Records
belong to a project and agent, and the run selection identifies the Memory
provider used for scoped changes.

## Prerequisites

- Have current project access to `AccessPrivateKnowledge` and
  `ReadRunSelection`.
- Use a Broker token that is not bound to a run for project-wide discovery.
- Have existing run-provider bindings in the project to discover their records.

The v1 Memories page is backed by the unpublished `Agentweaver.Knowledge`
source candidate. Its route names describe source contracts, not deployed
service availability.

## Select scope and search

1. Open **Memories** from a project.
2. Use **Project-wide search** to find records across accessible runs and
   agents. The app signs in without a run binding. If needed, select
   **Sign in for project-wide search**.
3. Choose a kind, enter search text, and select **Search project memories**.
   **Include inactive records** is selected by default; clear it to omit
   historical states.
4. Use **Previous page** and **Next page** to read more results. Select
   **Refresh** to read the results again.
5. Select **View revision history** to inspect a record's revisions. Its
   source run is shown with a link to the run.
6. To create or change records, use the run and agent controls below the
   project-wide results. The list contains up to 100 of the project's newest
   runs and the agents in project configuration.

Search returns up to 50 records per page and supports up to 100 pages. It
searches all agents through existing provider bindings; it does not create a
provider binding for a run. If there are more than 5,000 matches, refine the
search to find older results.

The page accepts `runId` and `agentId` query parameters for scoped
management. The **Memories for this session** link in **Topology** and the
**View memories** link on **Agents** fill them in. Each result shows kind,
type, revision, lifecycle state, trust state, content, rationale, tags, and
source run.

**Expected result:** Project-wide discovery shows records the current project
authority can read, regardless of agent. The owner checks current project
access and each existing run binding before returning results.

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

- Project-wide search requires an unbound project token. A run-bound token is
  rejected. Missing or unavailable provider bindings fail explicitly.
- Missing IDs or a Broker binding for a different project/run prevent scoped
  requests.
- Search and revision history show independent loading and empty states.
- Changing scope clears stale results and transfer state.
- Invalid bundle JSON, unsupported scope, stale revisions, and owner failures
  appear as errors. A failed write does not appear as a successful local state
  change.
- The browser rejects records and mutation responses outside the selected
  project and agent.
- Project-wide session history and addressed-message history are not available
  in v1. The exact missing Gateway routes are
  `GET /api/v1/projects/{projectId}/sessions` and
  `GET /api/v1/projects/{projectId}/agent-messages`. The **Recent project
  runs** panel links to the existing per-run session tree instead.

## Requests

All routes use the Gateway. Project-wide discovery uses
`/api/v1/projects/{projectId}/knowledge`. Scoped operations use the prefix
shown below.

| Action | Method and path after the project-wide prefix |
| --- | --- |
| Search across existing run bindings | `GET /records` |

The project-wide route requires current project `AccessPrivateKnowledge` and
`ReadRunSelection` authority with a token that is not bound to a run. It
searches only through existing provider bindings.

Scoped operations use this prefix:
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
