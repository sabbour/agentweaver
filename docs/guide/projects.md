---
title: Working with Projects
---

# Working with Projects

A **Project** contains local agent work, runs, teams, and memory. A GitHub repository is optional.

## The Project Gallery

When you open Agentweaver, the first screen is the **Project Gallery** — a grid of cards, one per project.

![Project Gallery](/guide/images/project-gallery.png)

Each card shows:

- **Project name**
- A **GitHub mark** in the card header when the project was created from GitHub
- **Source repository** (if created from GitHub)
- **Working directory** path on the server
- **Availability badge** — green **Available** or amber **Unavailable**

::: tip Unavailable projects
A project is marked **Unavailable** when its working directory has moved or is no longer accessible on the server. Create a new project for the replacement workspace, or delete the unavailable project record if it is no longer needed.
:::

Click **Open** on any card to enter a project.

If no projects exist yet, the page shows an empty state with the same two creation actions.

## Creating a project

Two creation paths are available from the Project Gallery toolbar:

### Create blank project

1. Select **Create blank project**.
2. Enter a **Name** for the project.
3. Enter a **Repository folder** name. If the server has a configured data directory, the field shows it as a prefix — you only need to type the folder name inside it. Otherwise, enter a full absolute path to a git repository on the machine running the Agentweaver server.
4. Optionally choose a **Blueprint** to pre-equip the project with a team, one or more workflows, a review policy, and a sandbox profile. The blank dialog offers the same blueprint step as the GitHub dialog: a **Templates** tab to pick an existing catalog blueprint and a **Generate** tab to describe a goal and generate a custom one. To start empty, use the **No blueprint** action in the dialog footer. See [Blueprints](./blueprints) for details.
5. Select **Create project**.

Agentweaver creates a local git repository. Local agent work can continue without GitHub repository access.

![Create blank project dialog](/guide/images/create-blank-project.png)

::: warning Directory must be empty or new
The chosen directory must be empty or not yet exist. Agentweaver will not overwrite or adopt an existing non-empty directory.
:::

### Create from GitHub

1. Select **Create from GitHub**.
2. If prompted, select **Authorize repository access**.
3. Enter a **Name** for the project.
4. Search for a repository that the Repo App can access.
5. Enter a **Repository folder** name.
6. Optionally choose a **Blueprint**.
7. Select **Create project**.

Agentweaver clones the repository into the chosen directory and records the project with its GitHub origin. Its card then carries a GitHub mark in the gallery.

![Create from GitHub dialog](/guide/images/create-from-github.png)

::: tip Repository access
If repository access is not ready, select **Authorize repository access**. Agentweaver returns you to the current task after authorization.
:::

## Project settings

Open a project, then navigate to **Settings** (accessible from the project's navigation) to configure it.

Settings are organized in a left rail:

### General

- **Project name** — rename the project.
- **Default model** — set the AI model used by default for this project's runs.

### Access

Manage Agentweaver project members.

### Repository

If a project started blank, local agent work remains available. Open **Repository** only when you want GitHub operations.

Select **Set up repository access** to create or connect a repository. Pull-request publishing requires this access.

### Sandbox policy

Controls how agent commands execute and what they can reach. Options include:

- Allowed/blocked shell commands
- Network access rules
- Destructive command gating
- **Preview approval timeout** — how long an agent-initiated live-preview request waits for
  approval. The default is 30 minutes; project owners can choose 1–1440 minutes. Existing
  projects inherit the 30-minute default.

### Unattended

The **Background** section reports a project-scoped automation readiness status and a fixed
reason code. It never reveals repository names, installation IDs, permission maps, or
credentials. Its **GitHub Copilot account** control shows the effective background AI source:
the verified login bound to this project, the platform-default GitHub Copilot account, or the
deployment's custom-key mode when BYOK is active. A Project Owner can start the separate
Copilot App binding when that is the missing prerequisite. When readiness reports
`repo_app_installation_required` and the deployment configures a Repo App slug, the page also
shows a direct **Install GitHub Repo App** link.

Once the prerequisites above are met, a Project Owner can turn scheduled/event-triggered
automation on or off with the **Activate automation**/**Deactivate automation** control in this
same section. Activating records which model-provider source (GitHub Copilot or BYOK) backs the
automation at that moment; deactivating immediately stops new scheduled/event triggers from
firing without deleting the project's schedules or event subscriptions, so re-activating resumes
them. Only Project Owners can see or use this control — everyone else sees the read-only
readiness status only.

Project Settings does not include legacy account-link controls, webhook provisioning, or
webhook-secret controls. Repository event delivery is configured through the Repo App's
App-level webhook.

After GitHub Copilot authorization, Agentweaver returns to the project's
**Background** settings and shows the selected GitHub login. It does not show authorization data,
repository or installation details, permissions, or credentials.

### Danger Zone

Irreversible actions:

- **Delete project** — removes the project record only. The working directory and all files on disk are always preserved.

::: warning In-flight runs
If the project has active runs, Agentweaver will cancel them before deleting the project record.
:::

## Project availability

A project is **Available** when its working directory is accessible on the server. If the directory is moved or deleted:

- The project card shows **Unavailable**.
- Runs are blocked while the workspace is unavailable.
- Create a new project for the replacement workspace or delete the unavailable record if it is no longer needed.
