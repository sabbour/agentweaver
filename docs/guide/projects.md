---
title: Working with Projects
---

# Working with Projects

A **Project** is the top-level container in Agentweaver. It pairs a local git working directory with an AI configuration, and it is the home for all runs, agent teams, and team memory.

## The Project Gallery

When you open Agentweaver, the first screen is the **Project Gallery** — a grid of cards, one per project.

![Project Gallery](/guide/images/project-gallery.png)

Each card shows:

- **Project name**
- **Source repository** (if created from GitHub)
- **Working directory** path on the server
- **Availability badge** — green **Available** or amber **Unavailable**

::: tip Unavailable projects
A project is marked **Unavailable** when its working directory has moved or is no longer accessible on the server. Open the project's Settings to relink it to the correct path.
:::

Click **Open** on any card to enter a project.

If no projects exist yet, the page shows an empty state with the same two creation actions.

## Creating a project

Two creation paths are available from the Project Gallery toolbar:

### Create blank project

1. Click **Create blank project**.
2. Enter a **Name** for the project.
3. Enter a **Repository folder** name. If the server has a configured data directory, the field shows it as a prefix — you only need to type the folder name inside it. Otherwise, enter a full absolute path to a git repository on the machine running the Agentweaver server.
4. Optionally select a **Blueprint** to pre-equip the project with a team, workflow, and policies. See [Blueprints](./teams) for details.
5. Click **Create**.

Agentweaver initializes the chosen directory as an empty git repository and records the project.

![Create blank project dialog](/guide/images/create-blank-project.png)

::: warning Directory must be empty or new
The chosen directory must be empty or not yet exist. Agentweaver will not overwrite or adopt an existing non-empty directory.
:::

### Create from GitHub

1. Click **Create from GitHub**.
2. Enter a **Name** for the project.
3. In the **Source repository** field, search your connected GitHub repositories or type `owner/repo` manually.
4. Enter a **Repository folder** name (same rules as blank project).
5. Optionally select a **Blueprint**.
6. Click **Create**.

Agentweaver clones the repository into the chosen directory and records the project with its GitHub origin.

![Create from GitHub dialog](/guide/images/create-from-github.png)

::: tip GitHub not connected?
If the repository list shows a "Connect GitHub" prompt, click it to authorize Agentweaver to access your GitHub repositories. You can also type `owner/repo` manually without connecting GitHub.
:::

## Project settings

Open a project, then navigate to **Settings** (accessible from the project's navigation) to configure it.

Settings are organized into four sections, accessible from the left rail:

### General

- **Project name** — rename the project.
- **Working directory** — relink the project to a different path (useful when the directory has moved).
- **Default model** — set the AI model used by default for this project's runs.

### Sandbox policy

Controls how agent commands execute and what they can reach. Options include:

- Allowed/blocked shell commands
- Network access rules
- Destructive command gating

### Review policy

Choose which review steps gate the project's work. Review policies are file-native YAML in `.agentweaver/review-policies/`; the shipped default is RAI plus human review.

| Step kind | Description |
|---|---|
| `rai` | Responsible AI content-safety check (on by default) |
| `human-review` | Your explicit approve / decline / request-changes gate (on by default) |
| `rubberduck` | Optional request-changes-to-producer review loop (off by default) |

::: warning Human review is always available
You can always inspect and act on a run's output regardless of review policy settings. The platform guarantees a human approval gate before anything merges.
:::

### Danger Zone

Irreversible actions:

- **Delete project** — removes the project record only. The working directory and all files on disk are always preserved.

::: warning In-flight runs
If the project has active runs, Agentweaver will cancel them before deleting the project record.
:::

## Project availability

A project is **Available** when its working directory is accessible on the server. If the directory is moved or deleted:

- The project card shows **Unavailable**.
- Runs are blocked until the project is relinked.
- Go to **Settings → General** to update the working directory path.
