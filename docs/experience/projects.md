# Projects

Use this page to browse projects visible to your current identity, create a
project, and use a project's **Dashboard** and **Orchestrations** pages.
Project access and configuration are owned by Projects & Config. The app
does not grant access or create runs.

## Choose a project

**Prerequisites:** Sign in through the Identity Broker. Your current identity
and role assignments determine which projects are visible.

1. Select **Projects** in the navigation rail, or open `/projects`.
2. Find the project tile.
3. Select the tile or its **Open** button.

Each tile shows the project name, project ID, state, revision, and your
access. Access is **Can edit**, **Can view**, or **No access**. The app reads it
from your current authorization context. **Overview** also lists up to six
recently updated active projects under **Recent projects**.

**Expected result:** The project's **Dashboard** opens.

## Create a project

**Prerequisites:** Sign in with an identity allowed to create a project in the
current tenant.

1. On **Projects**, select **Create project**.
2. In **Project name**, enter up to 120 characters.
3. In **Start from**, choose **Blank project** or **GitHub repository**.
4. For **GitHub repository**, enter the repository as `owner/name` or as its
   `github.com` address.
5. Select **Create project**.

For **GitHub repository**, the app creates the project and then saves the
repository in the project configuration with **GitHub App** sign-in. Saving the
repository needs access to the new project. If you don't have access yet, the
project is still created, and a note says to add the repository in
**Settings > Repository** later.

**Expected result:** A message confirms the new project, and the project list
refreshes. Creating a project does not make you its owner. If you can't open
the project afterward, ask a tenant administrator for access.

## Read the project Dashboard

Open `/projects/:projectId`. The **Dashboard** shows:

- A readiness card based on the saved configuration. It checks the default
  model, repository, team, and skills. Its status is **Ready**, **Setup
  needed**, or **Archived**. The main action opens the next setup step.
- Quick actions: **Open Orchestrations** and **Open Memories**.
- Summary tiles: **Recent runs** opened in this browser, **Needs input**,
  **Agents**, and **Skills** enabled in this project.
- **Recent runs in this browser** for this project.
- A **Team** summary with a link to **Agents**.

Select **Refresh** to reload the project and its configuration. The Dashboard
shows only real configuration and browser run data. Run metrics and activity
charts are not shown, because the service has no project metrics API.

## Open an existing run

**Prerequisites:** You opened the run once in this browser, for example from a
link or a notification.

1. Open **Orchestrations** for the project.
2. Find the run under **In progress** or **Finished**.
3. Select **Open**.
4. The app signs in to the run silently. If that fails, complete
   [run sign-in](./onboarding-auth.md#open-an-existing-run).

**Orchestrations** lists the runs you opened in this browser for this project.
A status summary shows how many runs are in each state. Each row shows the
state when you last opened the run and when you opened it. Select **Remove** to
take a run off the list. Removing doesn't change the run. Select **Refresh** to
reload the list. When the list is empty, it shows **Runs you open appear
here.** Signing out clears the list.

You don't type a run ID. The list can't show every run in the project, because
the service has no run list API.
See [Runs and sessions](./runs-sessions.md) for the data the run page reads.

## Understand project states and errors

- A loading indicator appears while a project request is pending.
- An empty gallery shows **No projects yet**. The owner returned no projects
  for the current identity and role assignments.
- Failed requests show the error message and structured error code when present.
- The app rejects a project response that does not match the route's project ID.
- Project access and configuration access are separate owner checks. Listing a
  project does not grant permission to change it.

You can rename, archive, and restore a project in
[Project settings](./configuration.md). The v1 browser has no controls for
project deletion, a kanban board, or repository cloning.

## Requests

The following versioned Gateway routes are owned by Projects & Config in the
checked-in source. This service is a source candidate; the route list does not
confirm deployment.

| Operation | Gateway request | Result |
| --- | --- | --- |
| List or refresh | `GET /api/v1/projects` | Returns projects visible to the current caller. |
| Create | `POST /api/v1/projects` with `{ "name": "..." }` | Creates a project in the selected tenant; it does not grant project roles. |
| Read the project | `GET /api/v1/projects/{projectId}` | Returns the project summary. |
| Read configuration | `GET /api/v1/projects/{projectId}/configuration` | Returns the configuration that the Dashboard readiness card uses. |
| Save the GitHub repository | `PUT /api/v1/projects/{projectId}/configuration` | Saves the repository for a project created from a GitHub repository. |
| Open a run | Browser navigation only | The run page then reads its own APIs. The recent-runs list is stored only in this browser. |

The app includes the tenant selector only when its current authorization
context confirms one. See the
[API reference](../reference/api.md) and
[Projects & Config architecture](../architecture/projects-config.md).
