# Projects

Use this page to browse projects visible to your current identity, create a
project, and use a project's **Dashboard**, **Board**, and **Orchestrations**
pages.
Project access and configuration are owned by Projects & Config. The app
does not grant access. To start a run, see
[Runs and sessions](./runs-sessions.md#start-a-new-chat).

## Choose a project

**Prerequisites:** Sign in through the Identity Broker. Your current identity
and role assignments determine which projects are visible.

1. Select **Projects** in the navigation rail, or open `/projects`.
2. Find the project tile.
3. Select the tile or its **Open** button.

Each tile shows the project name, project ID, state, revision, and your
owner-confirmed role names, such as **TenantAdmin**, **Owner**, or
**Orchestrator**. Each role carries its tenant or project resource scope, and
the same names appear in project headers. These labels are informational;
Projects & Config continues to check current owner authority for every
protected operation. **Overview** also lists up to six recently updated active
projects under **Recent projects**.

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
- Summary tiles: **Active runs**, **Needs input**, **Runs in range**, and
  **Success**. **Success** is succeeded runs out of completed runs in the
  range. With no completed runs, it shows a dash. Each tile opens
  **Orchestrations**.
- **Activity**: a range selector (**Last 7 days**, **Last 30 days**, or
  **Last 90 days**), the **Runs over time** chart of created and completed
  runs per UTC day, and **Run totals**.
- **Model and agent usage**: **Usage by model** and **Usage by agent** tables
  with runs, requests, token counts, average duration, cost, and unpriced
  requests.
- **Recent runs**: the newest runs in this project.
- A **Team** summary with a link to **Agents**.

Costs appear as the service reports them, one line for each price unit and
disposition. The app never adds subtotals together or converts them. A value
that the service didn't record shows a dash, not 0. Metrics cover this
project only.

Select **Refresh** to reload the project, its configuration, the run list,
and the metrics.

## Use the project Board

**Prerequisites:** Permission to view the project. Capturing and editing tasks
need permission to change the project.

1. Select **Board** in the project rail, or open `/projects/:projectId/board`.
   A backlog promotion notification opens the same page.
2. Read the tasks in their columns:
   - **Backlog**: captured tasks that are not queued yet.
   - **Ready**: tasks queued and waiting to be claimed.
   - **Active**: tasks that a run has claimed.
   - **Done**: tasks whose lifecycle is closed.
3. To add a task, enter a title under **Intake** and select **Add**.
4. To change a task, select its edit button, change **Title** or
   **Description**, and select **Save**.

Each card shows the title, description, prerequisites by title, and the
linked repository issue as `owner/name#number` with **Open** or **Closed**.
**Automation running** marks a task that an automation is changing. You
can't edit that task until the automation finishes. Archived tasks are not
shown. The summary strip shows queued, active, and done counts and jumps to
a column. Use the zoom controls or Ctrl and the scroll wheel to zoom.

**Start task** is the floating button at the top of every project page. It
opens **Start a task**. Enter a title and select **Add**. The app adds the
task to the project's Backlog and opens the Board, which confirms the new
task. Start task does not start a run. To start a run, use **New session**.

Each write sends the backlog revision the app last read. If another change
was saved first, a capture reads the backlog again and retries once. An edit
reloads the Board and asks you to try again.

**Expected result:** The new task appears in **Backlog**. A saved edit shows
**Task saved.**

Moving tasks between columns is not available yet, and cards can't be
dragged. The Board also has no archive, reorder, or dependency controls,
and no **Human Review** or **Problems** columns.

## Open an existing run

1. Open **Orchestrations** for the project.
2. Find the run under **Active** or **Recent**.
3. Select **Open**.
4. The app signs in to the run silently. If that fails, complete
   [run sign-in](./onboarding-auth.md#open-an-existing-run).

**Orchestrations** reads the project run list. **Active** holds runs that are
working, open, or waiting for input. **Recent** holds finished runs: done,
failed, cancelled, or with an unknown outcome. Each section shows the newest
runs first and loads more with **Load more**. A status summary shows how many
loaded runs are in each state. Select **Refresh** to read the lists again.
When the project has no runs, the page shows **No orchestrations yet**.

To see runs across all your projects, open **Sessions**.
See [Runs and sessions](./runs-sessions.md) for the data the run page reads.

## Understand project states and errors

- A loading indicator appears while a project request is pending.
- An empty gallery shows **No projects yet**. The owner returned no projects
  for the current identity and role assignments.
- Failed requests show the error message and structured error code when present.
- Without permission to view a project's backlog, the Board shows **No access
  to this board**. Other Board read failures show **Couldn't load the board**
  with **Try again**. An empty backlog shows **No tasks yet**.
- The app rejects a project response that does not match the route's project ID.
- Project access and configuration access are separate owner checks. Listing a
  project does not grant permission to change it.

You can rename, archive, and restore a project in
[Project settings](./configuration.md). The v1 browser has no controls for
project deletion or repository cloning.

## Requests

The following versioned Gateway routes are in the checked-in source. The
**Owner** column names the service that handles each route. These services
are source candidates; the route list does not confirm deployment.

| Operation | Gateway request | Owner | Result |
| --- | --- | --- | --- |
| List or refresh | `GET /api/v1/projects` | Projects & Config | Returns visible projects with the current caller's effective role names and resource scopes. |
| Create | `POST /api/v1/projects` with `{ "name": "..." }` | Projects & Config | Creates a project in the selected tenant; it does not grant project roles. |
| Read the project | `GET /api/v1/projects/{projectId}` | Projects & Config | Returns the project summary with the current caller's effective role names and resource scopes. |
| Read configuration | `GET /api/v1/projects/{projectId}/configuration` | Projects & Config | Returns the configuration that the Dashboard readiness card uses. |
| Save the GitHub repository | `PUT /api/v1/projects/{projectId}/configuration` | Projects & Config | Saves the repository for a project created from a GitHub repository. |
| List runs | `GET /api/v1/projects/{projectId}/runs` | Orchestrator | Returns one page of runs, newest first. The `state` filter and `cursor` select the rows. |
| Read run metrics | `GET /api/v1/projects/{projectId}/metrics` | Orchestrator | Returns run counts and daily buckets for a UTC range of up to 92 days. |
| Read usage | `GET /api/v1/projects/{projectId}/usage` | Events & Sessions | Returns usage and cost subtotals by model and agent for the same range. |
| Read the backlog | `GET /api/v1/projects/{projectId}/backlog` | Orchestrator | Returns the backlog graph: revision, tasks, and dependencies. |
| Capture a task | `POST /api/v1/projects/{projectId}/backlog/tasks` | Orchestrator | Adds a task to Backlog with a client-chosen task ID and the expected graph revision. |
| Edit a task | `PUT /api/v1/projects/{projectId}/backlog/tasks/{taskId}/content` | Orchestrator | Replaces the title and description with the expected graph and task revisions. |
| List synced issues | `GET /api/v1/projects/{projectId}/source-control/issues` | Orchestrator | Returns repository issues synced to backlog tasks. The Board reads every page with `state=all`. |

The app includes the tenant selector only when its current authorization
context confirms one. See the
[API reference](../reference/api.md) and
[Projects & Config architecture](../architecture/projects-config.md).
