# Projects

Projects group configuration and accepted run selections. Open `/projects` to
see projects visible to your current identity and tenant context.

## Choose a project

1. Sign in through the Broker.
2. Read the **Visible projects** list.
3. Select a project name.

Each row shows the stable project ID, project revision, configuration revision,
and lifecycle state. **Refresh** reloads the list.

The overview shows the project's current state and links to
[Configuration](./configuration.md) and [Knowledge](./knowledge.md).

## Create a project

1. Enter a **Project name**. The form accepts up to 120 characters.
2. Select **Create project**.
3. Read the returned project ID and refresh result.

Creation does not assign an Owner. If the new project is not visible, a tenant
administrator must grant current project access. The browser does not include
role-management controls.

## Open an existing run

1. On the overview, enter the **Exact run ID**.
2. Select **Open run**.
3. Complete [run-bound sign-in](./onboarding-auth.md#open-a-run).

The opener navigates to `/projects/:projectId/runs/:runId`; it does not create a
run or enumerate run history. See [Runs and sessions](./runs-sessions.md).

## States and errors

- A spinner appears while the Gateway list or project request is pending.
- An empty list means no projects were returned for the current identity and
  role assignments.
- Failed requests show the HTTP status and structured error code when present.
- A project response for a different route is rejected.
- Project access and configuration access are separate owner checks. Listing a
  project does not grant permission to change it.

There are no v1 browser controls for project deletion, a kanban board, repository
cloning, or automatic project setup.

## Requests

| Step | Gateway request | Owning service |
| --- | --- | --- |
| List or refresh | `GET /api/v1/projects` | Projects & Config |
| Create | `POST /api/v1/projects` with `{ "name": "..." }` | Projects & Config |
| Open overview | `GET /api/v1/projects/{projectId}` | Projects & Config |
| Open a run | Browser navigation only; the run page then loads its own APIs | Orchestrator, Events & Sessions, Projects & Config |

Project requests forward the confirmed tenant selector. See the
[API reference](../reference/api.md) and
[Projects & Config architecture](../architecture/projects-config.md).
