# Chief of Staff

The Chief of Staff gives each user a private place to talk to Agentweaver and a
personal list of outcomes. It has two parts:

- **Conversations**: private text chats. Each conversation is a run that the
  user started in one project they selected.
- **Priorities**: an ordered list of outcomes. Each Priority has notes, a
  manual checklist, and links to runs, conversations, issues, pull requests,
  and artifacts across projects.

The Chief of Staff doesn't run work on its own. It starts work only through the
existing run routes, and it reads gates and run state from their owners.

## How a conversation works

1. The user selects one project and writes a first message.
2. The client calls `POST /api/v1/projects/{projectId}/runs` with an
   `Idempotency-Key`. The Orchestrator creates the run. A retry with the same
   key returns the same run, so a double submit creates one request.
3. The client calls `POST /api/v1/chief-of-staff/conversations` with the
   `projectId` and `runId`. The Orchestrator saves a private conversation entry
   for the caller. Repeating the call returns the same entry.
4. After a reload or reconnect, the client lists
   `GET /api/v1/chief-of-staff/conversations` and reopens the conversation.

The conversation entry stores only the project, run, and owner. Messages,
gates, and state stay with the run's owners.

Only the user who created the run can bind it. Other users can't list or read
the entry, even in the same project.

## How Priorities work

A Priority records an outcome the user cares about. Its `state` is `active`,
`achieved`, or `dropped`, and only the user changes it. Editing a Priority
never starts, cancels, or changes a run, session, or gate. A Priority stays in
the list after its linked runs end.

Each link names one item in a project the user can read:

| Link kind | Identifies |
| --- | --- |
| `run` | A run in the project. |
| `conversation` | One of the user's Chief of Staff conversations. |
| `issue` | An issue number. |
| `pull_request` | A pull request number. |
| `artifact` | An artifact in a run. |

Reads show the current state of run and conversation links. Issue, pull
request, and artifact links show `unavailable`, because their owners don't
offer a current-state read. A missing pull request state is never shown as open
or merged.

## Handle conflicts

Two tabs or devices can edit the same Priority. Each write sends the revision
it read:

- A Priority write with an old `expectedRevision` returns
  `409 priority_revision_conflict`.
- A reorder with an old `expectedOrderRevision` returns
  `409 priority_order_conflict`.

On a conflict, reread the Priority or list, show the current values, and let
the user apply the change again. The service never merges edits.

## Lose access to a project

Every call reads the caller's current project permissions. When the user can no
longer read a project:

- Conversations in that project return `access: unavailable` with only their
  identifier and creation time.
- Links to that project return `access: unavailable` with only their
  identifier and kind.
- The Priority outcome, notes, and checklist stay visible, because they
  belong to the user.

The user can still remove a link to a project they no longer read.

## Flow and attention

Flow groups the user's work into **Needs input**, **Open**, **Working**, and
**Done**. It has no store of its own:

- **Needs input** reads pending gates from
  `GET /api/v1/notifications?projectId=...&includeDismissed=true`. Dismissing a
  notification doesn't hide a gate from Flow.
- **Open**, **Working**, and **Done** read runs from
  `GET /api/v1/projects/{projectId}/runs`. Completed, failed, cancelled, and
  indeterminate runs stay distinct.
- Answering a gate rereads the gate by project, run, session, request, and
  state version first. A stale gate fails without changing state.

## Deployment

Orchestrator migration 024 creates the Chief of Staff tables, and
`infra/bicep/postgres-runtime-grants.sql` grants the runtime role access. Apply
both through the reviewed PostgreSQL plan. For steps, see
[Deploy Agentweaver to Azure](./deploy-to-azure.md).

## Related content

- [API reference: Chief of Staff](../reference/api.md#chief-of-staff)
- [MCP tools](../reference/mcp-tools.md)
- [Gateway/BFF](./gateway.md)
