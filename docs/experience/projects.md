# Projects experience

Projects are the front door to Agentweaver. A project names the work, anchors it to a repository workspace, and carries the defaults that shape every run in that repository.

Scope: this page covers creating, switching, summarizing, configuring, and deleting projects across the web UI and current MCP tools.

See also: [Overview](./00-overview.md), [Runs & board](./runs-board-watch.md), [Team & casting](./team-casting-memory.md), [Working with Projects](../guide/projects.md), and [Projects & Workspaces](../deep-dive/projects.md).

## Mental model

An Agentweaver **project** is a durable container around a repository and Agentweaver's work against it. It answers five user-facing questions:

- **What am I working on?** The project has a user-facing name and stable project id.
- **Where is the repository?** The project points at a working directory on the Agentweaver server, or at a server-managed workspace.
- **Where did it come from?** The origin is **blank** or **GitHub**.
- **Which defaults apply?** The project carries model provider settings plus blueprint-applied team, workflow, review, and sandbox choices.
- **Can Agentweaver use it now?** The `available` state reflects whether the workspace can be reached.

The project record and the workspace are separate. Renaming changes the record, not the repository path. Deleting removes the Agentweaver project record and run history from the app experience; repository files are treated as workspace state, not casually destroyed from the gallery.

## Project Gallery

The **Project Gallery** is the landing page. The page title is **Projects** with the subtitle **Your Agentweaver projects.** It is where users scan work, create projects, and switch projects.

![Project Gallery page with project cards and creation buttons](/screenshots/projects-gallery.png)

> 📸 **Screenshot — `projects-gallery.png`**
> *Shows:* the Projects page titled "Projects" / "Your Agentweaver projects." with at least one project card (each card shows an **Available** or **Unavailable** status badge and an **Open** button), highlighting the **Create blank project** and **Create from GitHub** buttons in the header.
> *Path:* Sign in → navigate to `/projects`.

When projects exist, the toolbar shows:

- **Create blank project**
- **Create from GitHub**

Each project card shows:

- Project name
- Source repository, when present
- Working directory path
- Availability badge
- **Open** action

The availability badge is direct:

- **Available** means Agentweaver can access the working directory.
- **Unavailable** means the project record exists, but the local directory or mounted workspace is missing or inaccessible.

Unavailable projects remain visible so users can inspect the record and delete it if the workspace is no longer available.

### Switching projects

Click **Open** on a card to enter that project. The user switches by choosing the visible card; Agentweaver routes by stable project id behind the scenes, so later renames do not change project identity.

### Empty and auth states

If no projects exist, the gallery says:

> No projects yet. Create one to get started.

The same **Create blank project** and **Create from GitHub** actions appear in the empty state, so first-run setup uses the normal creation flow.

If GitHub sign-in is required to list projects, the gallery says:

> Sign in with GitHub to see your projects.

The action is **Sign in with GitHub**. This is not the same as an unavailable project: sign-in controls whether projects can be listed; availability controls whether a listed workspace can be used.

## Creating a project in the web UI

Creation starts from the gallery. Users choose a blank repository or a GitHub-backed repository, then optionally apply a blueprint. Both redesigned dialogs use the same shape: project and repository fields on the left, blueprint selection and generation on the right (`apps/web/src/pages/ProjectGalleryPage.tsx:330`, `:516`).

```mermaid
flowchart TD
  Start[Project Gallery] --> Choice{Create path}
  Choice --> Blank[Create blank project]
  Choice --> GitHub[Create from GitHub]

  Blank --> BlankName[Enter Name]
  BlankName --> BlankWorkspace{Workspace auto-assigned?}
  BlankWorkspace -->|No| BlankFolder[Enter Repository folder]
  BlankWorkspace -->|Yes| ManagedBlank[No folder field; server manages workspace]

  GitHub --> GitName[Enter or autofill Name]
  GitName --> Account{GitHub connected?}
  Account -->|Yes| Org[Choose Organization]
  Account -->|No| Manual[Connect GitHub or type manually]
  Org --> Repo[Search/select Source repository]
  Manual --> Repo
  Repo --> GitWorkspace{Workspace auto-assigned?}
  GitWorkspace -->|No| GitFolder[Enter or autofill Repository folder]
  GitWorkspace -->|Yes| ManagedGit[No folder field; server manages workspace]

  BlankFolder --> Blueprint{Blueprint optional}
  ManagedBlank --> Blueprint
  GitFolder --> Blueprint
  ManagedGit --> Blueprint

  Blueprint -->|No blueprint| Create[Create]
  Blueprint -->|Predefined| BlueprintId[Apply blueprint_id]
  Blueprint -->|Generated/custom| Inline[Apply inline blueprint]
  BlueprintId --> Create
  Inline --> Create
  Create --> Project[Project + repository workspace]
  Project --> Board[Project board]
```

### Create blank project

**Create blank project** starts a new Git repository under Agentweaver's control. The dialog title is **Create blank project**.

![Create blank project dialog with Name and Repository folder fields](/screenshots/create-blank-project-dialog.png)

> 📸 **Screenshot — `create-blank-project-dialog.png`**
> *Shows:* the **Create blank project** dialog with the **Name** field (placeholder "My project") and the **Repository folder** field auto-filled (slugified) from the project name, plus the **Cancel** and **Create** buttons (the **Create** button reads **Creating** with a spinner while submitting).
> *Path:* `/projects` → click **Create blank project**.

Required fields:

- **Name** with placeholder **My project**
- **Repository folder**, unless the workspace is auto-assigned

The **Repository folder** hint adapts to server mode. If the server has a data directory, the field asks for a folder name inside that directory and displays the directory prefix. If not, it asks for an absolute path to a Git repository on the machine running the Agentweaver server. If workspaces are auto-assigned, the field is hidden because the server controls the final workspace path.

The **Create** button enables only when required values are present. During submission it reads **Creating** and shows a spinner.

Creation is for a new controlled workspace. The target directory must be empty or not yet exist.

### Create from GitHub

**Create from GitHub** clones a repository and records its GitHub origin. The dialog title is **Create project from GitHub**.

![Create project from GitHub dialog with Organization and Source repository pickers](/screenshots/create-from-github-dialog.png)

> 📸 **Screenshot — `create-from-github-dialog.png`**
> *Shows:* the **Create project from GitHub** dialog with the **Name** field, the **Organization** picker (`aria-label="Organization"`, showing avatars and an **Org** badge), the searchable **Source repository** field (`aria-label="Repository"`), and the auto-filled **Repository folder** field, plus **Cancel** / **Create**. If GitHub is not connected, the **Connect GitHub** action appears instead of the repository list.
> *Path:* `/projects` → click **Create from GitHub**.

Required fields:

- **Name**
- **Organization**, when GitHub accounts are loaded
- **Source repository**
- **Repository folder**, unless the workspace is auto-assigned

The redesigned repository column supports three ways to choose a source (`apps/web/src/pages/ProjectGalleryPage.tsx:567`):

- **Search repositories** — a freeform combobox that accepts any `owner/repo` or GitHub URL and also lists repositories from the selected account (`ProjectGalleryPage.tsx:590`).
- **Recent** — the first three loaded repositories, with **Clear** and per-row **Use** actions (`ProjectGalleryPage.tsx:619`).
- **My organizations** — organization accounts with avatars; **Show more** expands beyond the first four and selecting an organization reloads its repositories (`ProjectGalleryPage.tsx:628`).
- **Or paste any repository** — a direct `owner/repo` field and **Go** button (`ProjectGalleryPage.tsx:643`).

If GitHub is not connected, the dialog says:

> Connect your GitHub account to list repositories, or paste any public owner/repo.

The action is **Connect GitHub**. Users can still paste a public repository manually.

### Autofilled but overridable fields

Agentweaver speeds up setup without locking the user in.

For a blank project:

- Typing **Name** slugifies the name into the repository folder.
- If the user edits **Repository folder**, Agentweaver stops replacing it from the name.
- In auto-assigned workspace mode, the folder field is hidden; the server owns the final path.

For a GitHub project:

- Selecting a repository fills **Source repository**.
- If **Name** is empty, the repository slug becomes a title-cased project name.
- The repository slug fills **Repository folder** unless the user already edited that field.
- In auto-assigned workspace mode, the folder field is hidden and the final path is server-managed.

This makes the common path fast while preserving explicit control before **Create**.

## Blueprints as the starting point

A **blueprint** is the fastest way to turn a repository into a working Agentweaver environment. It bundles:

- **Team roster** — the roles available in the project
- **Workflow** — the run flow Agentweaver should use
- **Review policy** — the gates that review or approve work
- **Sandbox profile** — the command and network posture for agent execution

Both creation dialogs now share the same blueprint components (`apps/web/src/components/BlueprintPicker.tsx:74`). The blank-project dialog starts on **Generated** and also shows **Templates** plus **No blueprint** so users can either describe the work, choose a starter template, or create an empty project (`apps/web/src/pages/ProjectGalleryPage.tsx:330`). The GitHub dialog starts on **Suggested** and adds **Templates** and **Generate** tabs beside it (`ProjectGalleryPage.tsx:516`, `:660`).

### Suggested

**Suggested** appears in **Create project from GitHub** after a repository is selected. Agentweaver analyzes GitHub repository metadata, topics, languages, root files, and issues-enabled, then recommends a catalog blueprint. The card shows rationale, roster chips, confidence, expandable signals, **Use this blueprint**, **Other blueprints**, and **Generate** for a custom alternative (`apps/web/src/components/BlueprintPicker.tsx:278`, `:337`, `:350`). If analysis fails or the service returns `fallback: true`, the tab shows a warning and a compact **Templates** list instead (`BlueprintPicker.tsx:323`). See [Repository blueprint suggestions](./repo-blueprint-suggestions.md).

### Templates

**Templates** lists predefined catalog blueprints with name, description, role chips, workflow, review policy, and sandbox profile. It is available in both blank and GitHub flows via the same `StarterTemplatesSection` (`apps/web/src/components/BlueprintPicker.tsx:212`).

### Generate

**Generate** asks what Agentweaver should accomplish and calls `POST /api/blueprints/generate`. In the GitHub flow, the selected repository is passed as `target_repository` for grounding (`apps/web/src/components/BlueprintPicker.tsx:92`, `apps/web/src/api/client.ts:177`). Generated blueprints configure agents, workflow, review policy, and sandbox posture; when generation succeeds, Agentweaver auto-selects the generated blueprint and shows a preview (`BlueprintPicker.tsx:252`).

The same model exists in MCP: predefined blueprints are applied by `blueprint_id`; generated or custom blueprints are applied inline. A create request must provide `blueprint_id` **or** inline `blueprint`, not both.

## MCP project creation and management

Use MCP when an agent or script needs to manage projects without the web UI.

Current scope note: the web UI is the complete GitHub repository-linking create flow; current MCP `project_create` accepts origin and workspace fields plus blueprint fields, but does not expose a `source_repository` argument.

### Project tools

| Tool | User outcome |
|---|---|
| `project_list` | List all Agentweaver projects. |
| `project_get` | Get one project by id, including name, origin, working directory, provider settings, state, and availability. |
| `project_create` | Create a project with name, working directory, optional origin, and optional blueprint. |
| `project_rename` | Rename the project display name. |
| `project_delete` | Delete the project record. |
| `project_configure` | Configure default model provider settings. |
| `project_list_runs` | List all runs for a project. |

### Blueprint and catalog tools

| Tool | User outcome |
|---|---|
| `list_blueprints` | List predefined blueprints, each with team roster, workflow, review policy, and sandbox profile. |
| `validate_blueprint` | Validate an inline blueprint object against schema and role constraints. |
| `blueprint_generate` | Generate a blueprint from a natural-language description of team and goals. |
| `catalog_list_roles` | List available agent roles from the catalog. |
| `catalog_list_scenarios` | List available casting scenario templates. |

### MCP create patterns

For a predefined blueprint:

1. Call `list_blueprints`.
2. Pick a blueprint id.
3. Call `project_create` with `name`, `working_directory`, `origin`, and `blueprint_id`.

For a generated blueprint:

1. Call `blueprint_generate` with a natural-language description.
2. Inspect the returned blueprint and generated workflow YAML.
3. Call `project_create` with `name`, `working_directory`, `origin`, inline `blueprint`, and `generated_workflow_yaml`.

For a custom inline blueprint:

1. Use `catalog_list_roles` and `catalog_list_scenarios` to understand available roles and patterns.
2. Build the inline blueprint.
3. Call `validate_blueprint`.
4. Call `project_create` with the inline `blueprint`.

The mutual exclusivity rule is intentional. If both `blueprint_id` and `blueprint` are supplied, Agentweaver rejects the request rather than guessing which starting point should win.

## Project board home

Clicking **Open** lands on the project board home. This is the day-to-day work surface, not the metrics dashboard.

The page title is the project name. The subtitle is:

> Backlog, Ready, and in-flight work.

If the project is unavailable, the page warns:

> This project is unavailable. The working directory may have moved or become inaccessible.

The warning links to project Settings.

The main content is the board, followed by **Runs**. The runs list shows status, task text, start time, and navigation into execution details. Normal runs have **Workflow**. Coordinator orchestrations have **Topology**. Non-terminal runs can show **Abandon**. Terminal runs can be deleted from the list.

When there are no runs, the page says:

> No runs yet. Start one above.

The board home answers, "What is happening in this project right now?" The dashboard answers, "How is this project performing?"

## Project Dashboard

The project Dashboard summarizes delivery metrics. The title is **Dashboard** and the subtitle is:

> Delivery metrics and the agent leaderboard.

![Project Dashboard with throughput chart and agent leaderboard](/screenshots/project-dashboard.png)

> 📸 **Screenshot — `project-dashboard.png`**
> *Shows:* the **Dashboard** page titled "Dashboard" / "Delivery metrics and the agent leaderboard.", the **Refresh** button with last-updated time, the **Throughput (last 30 days)** section, and the **Agent leaderboard** table (`aria-label="Agent leaderboard"`).
> *Path:* `/projects` → open a project → land on `/projects/:projectId`.

It refreshes every 30 seconds and includes **Refresh**. When data is loaded, the header shows the last updated time and a refresh countdown.

Summary cards show:

- **Runs this week**
- **Active agents**
- **Active runs**
- **Runs total**
- **Tasks done (7d)**

The **Throughput (last 30 days)** chart has **Created** and **Done** series. If there is no data, it says:

> No throughput data yet.

The **Agent leaderboard** shows agent, role, runs this week, runs total, success rate, and average duration. The UI defines success rate as:

> Success rate = successful terminal runs / terminal runs (queued, waiting-review, and in-progress excluded).

If no agent activity exists, it says:

> No agent activity yet.

The Agent name links into a filtered project flow view, so the dashboard works as both a summary and a path to investigation.

## Project Settings

Project Settings changes the project record and project policies. The title is **Project settings** with subtitle:

> Project configuration and pickup behavior.

![Project Settings page with General, Sandbox policy, Review policy, and Danger Zone sections](/screenshots/project-settings.png)

> 📸 **Screenshot — `project-settings.png`**
> *Shows:* the **Project settings** page with the left rail sections **General** (project name, default model), **Sandbox policy**, **Review policy**, and **Danger Zone**, with the **General** section selected (the active section is deep-linked through the URL query).
> *Path:* open a project → click **Settings** in the left rail → `/projects/:projectId/settings`.

The left rail sections are:

- **General** — project name and default model
- **Sandbox policy** — command execution and reachability
- **Review policy** — review gates for project work
- **Danger Zone** — irreversible project action

The selected section is deep-linked through the URL query.

### General

#### Rename project

**Rename project** changes the display name only. It does not move the workspace, change the project id, or rewrite the repository. The field is **Name**, the action is **Save**, and success shows **Project renamed.**

MCP equivalent: `project_rename`.


#### Default model

**Default model** sets the model used by default for future runs. In the web UI, the field is **GitHub Copilot model** with placeholder **e.g. gpt-4o**. Success shows **Model settings saved.**

MCP equivalent: `project_configure`.

The MCP tool can set `default_provider`, `default_model_github_copilot`, and `default_model_microsoft_foundry`. The user-facing meaning is simple: future runs inherit these defaults unless a run chooses another model.

### Sandbox policy

**Sandbox policy** controls how agent commands execute and what they may reach. The section shows:

- **Shell execution**
- **Sandbox enabled**
- **Outbound network**
- **Allowed repository roots**
- **Blocked command patterns**

The action is **Save** and success shows **Sandbox policy saved.** Blueprint selection can set the initial sandbox posture; Settings is where users inspect and adjust it later.

### Review policy

**Review policy** chooses which review steps gate project work. The section includes **Sync**, the active policy summary, and policy cards.

Cards can show **Active**, **Built-in**, **Custom**, **Valid**, and **Invalid** badges, plus policy description, review step chips such as **Rubberduck**, **RAI**, and **Human review**, source, validation errors, and **Set as active** for valid inactive policies.

If none are found, the page says:

> No review policies found. Sync to load from .agentweaver/review-policies/.

Blueprints can choose the initial review policy. Settings controls the active policy after creation.

### Danger Zone

**Danger Zone** contains **Delete project**. The text says:

> This action cannot be undone. The project and all its run history will be permanently removed.

The user must check **I understand this is permanent** before **Delete project** enables. While the action runs, the button reads **Deleting**.

MCP equivalent: `project_delete`.

Use delete for an unwanted or unavailable project record.

## Edge cases

### Project is unavailable

A project shows **Unavailable** when the workspace cannot be reached. On the board, Agentweaver tells the user the working directory may have moved or become inaccessible.

Recovery path: create a new project for the replacement workspace, or delete the unavailable project record if it is no longer needed. MCP path: call `project_get` to inspect the record and `project_delete` if it should be removed.

### Repository moved after creation

Create a new project for the replacement workspace. The removed relink feature no longer changes a project record to point at a new server path.

### Autofill chose the wrong folder

Before creation, edit **Repository folder**; Agentweaver stops overwriting it. After creation, create a new project if a different workspace is required.

### GitHub repositories do not load

The dialog shows **Retry** for load failures. If GitHub is not connected, it shows **Connect GitHub** and still permits manual repository entry.

### Blueprint load or generation fails

Creation can continue with **No blueprint** if predefined blueprints do not load. Generation errors appear inside the blueprint column. In MCP, use `validate_blueprint` before `project_create` for hand-built or modified inline blueprints.

### Delete confirmation blocks the button

This is expected. **Delete project** stays disabled until **I understand this is permanent** is checked.

## Choosing the right action

| User intent | Web action | MCP tool |
|---|---|---|
| Start empty | **Create blank project** | `project_create` |
| Start from GitHub | **Create from GitHub** | Use web UI for current full repository-linking flow |
| Apply a ready operating model | Select predefined **Blueprint** | `list_blueprints`, then `project_create` with `blueprint_id` |
| Generate an operating model | **Generate blueprint** | `blueprint_generate`, then `project_create` with inline `blueprint` |
| See projects | Project Gallery | `project_list` |
| Inspect one project | **Open** / project pages | `project_get` |
| Rename | Settings → **Rename project** | `project_rename` |
| Change model defaults | Settings → **Default model** | `project_configure` |
| See runs | Board **Runs** | `project_list_runs` |
| Remove project record | Danger Zone → **Delete project** | `project_delete` |

## Experience principles

Projects work when Agentweaver keeps three promises:

1. **Make the repository boundary visible.** Cards, settings, and MCP all surface the working directory or repository identity.
2. **Expose unavailable records clearly.** Unavailable projects stay visible so users can decide whether to recreate or delete them.
3. **Start with an operating model.** Blueprints make team roster, workflow, review policy, sandbox, and model defaults part of project setup instead of scattered follow-up tasks.

The result is a concrete project experience: named repositories with visible status, quick creation paths, visible workspace status, and repeatable defaults for every run.

## See also

- [Agent definition (User Guide)](./agent-definition.md) — the GitHub Copilot agent file that lands in every project you create.
