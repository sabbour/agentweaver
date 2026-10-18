# Projects

Use this page to list projects visible to your current identity, create a
project, or open an existing run. Project access and configuration are owned
by Projects & Config; the app does not grant access or create runs.

## Choose a project

**Prerequisites:** Sign in through the Identity Broker. Your current identity
and role assignments determine which projects are visible.

1. Open `/projects`.
2. Read the **Visible projects** list.
3. Select a project name.

Each project row shows its stable project ID, project revision, configuration
revision, and lifecycle state. Select **Refresh** to reload the list. The
overview shows the current project state and links to
[Configuration](./configuration.md) and [Knowledge](./knowledge.md).

**Expected result:** The selected project's overview displays the lifecycle,
record revision, and configuration revision returned by the owner.

## Create a project

**Prerequisites:** Sign in with an identity allowed to create a project in the
current tenant.

1. On `/projects`, enter a name in **Project name**. The field accepts up to
   120 characters.
2. Select **Create project**.
3. Read the returned project ID and refresh the visible project list.

**Expected result:** The owner returns a project summary and the app displays
the new project ID. Project creation does not assign an Owner. If the project
is not visible afterward, a tenant administrator must grant current access.
The browser does not provide role-management controls.

## Open an existing run

**Prerequisites:** Open the project overview and obtain the exact ID of an
existing run.

1. Enter the run ID in **Exact run ID**.
2. Select **Open run**.
3. Complete [run-bound sign-in](./onboarding-auth.md#open-an-existing-run).

The app navigates to `/projects/:projectId/runs/:runId`. This action does not
create a run or enumerate run history. See [Runs and sessions](./runs-sessions.md)
for the data the run page reads.

## Understand project states and errors

- A loading indicator appears while a project request is pending.
- An empty list means the owner returned no projects for the current identity
  and role assignments.
- Failed requests show the HTTP status and structured error code when present.
- The app rejects a project response that does not match the route's project ID.
- Project access and configuration access are separate owner checks. Listing a
  project does not grant permission to change it.

The v1 browser has no controls for project deletion, a kanban board, repository
cloning, or automatic project setup.

## Requests

The following versioned Gateway routes are owned by Projects & Config in the
checked-in source. This service is a source candidate; the route list does not
confirm deployment.

| Operation | Gateway request | Result |
| --- | --- | --- |
| List or refresh | `GET /api/v1/projects` | Returns projects visible to the current caller. |
| Create | `POST /api/v1/projects` with `{ "name": "..." }` | Creates a project in the selected tenant; it does not grant project roles. |
| Read overview | `GET /api/v1/projects/{projectId}` | Returns the project summary. |
| Open run | Browser navigation only | The run page then reads its own APIs. |

The app includes the tenant selector only when its current authorization
context confirms one. See the
[API reference](../reference/api.md) and
[Projects & Config architecture](../architecture/projects-config.md).
