---
title: Workflows
---

# Workflows

**Workflows** define the scenario. They are YAML-described multi-role pipelines that tell Agentweaver which agents are involved, what each one does, and how work flows between them. The scenario is defined by the workflow, not the platform — this is what makes Agentweaver work for software delivery, content authoring, PM discovery, incident response, and anything else your team needs.

## Built-in workflow library

Agentweaver ships seven built-in workflows:

| Workflow | What it does |
|---|---|
| `software-delivery` | Code changes, new features, refactors, and migrations. Full delivery pipeline from spec to merged code. |
| `bug-fix` | Targeted investigation and fix for a specific bug report or regression. Includes root-cause analysis. |
| `code-review` | Automated review of a diff, branch, or pull request. Produces a structured review with categorized findings. |
| `content-authoring` | Drafting and editing docs, blog posts, READMEs, release notes, and other written content. |
| `pm-discovery` | Product discovery — user research synthesis, spec drafting, requirements analysis, and opportunity framing. |
| `incident-response` | Live incident investigation, mitigation guidance, and postmortem drafting with full run tracing. |
| `agent-evaluation` | Testing and evaluating agent outputs against criteria. Useful for validating agent behavior and quality. |

::: tip Automatic workflow matching
When you submit a task, an LLM pass automatically selects the best-fit workflow from the library. You don't have to pick a workflow manually for most tasks.
:::

## How workflow matching works

When you start an orchestration, Agentweaver reads your task description and runs a matching pass that considers:

1. The semantic intent of your description
2. The project's configured default workflow (if set)
3. The built-in library's workflow metadata and use-case descriptions

After the confirmed outcome is decomposed, Agentweaver validates that code-producing work uses a
workflow with a **Build & Test** stage. If an automatic match such as `pm-discovery` cannot express
that gate, the coordinator re-selects from compatible workflows. Explicit overrides remain pinned;
when an override lacks Build & Test for code work, the work plan surfaces a warning instead of
silently changing the user's choice.

The matched workflow is shown in the run detail. If the auto-match picks the wrong one, you can
override it at submission time.

## Workflows in your project

Each project stores its active workflows in `.agentweaver/workflows/` inside the project's working directory. The **Workflows** page in the web UI shows all workflows discovered from that directory, their validation status, and which one is the project default.

![Workflows page](/guide/images/workflows-page.png)

### Viewing workflows

From a project, navigate to **Workflows** in the sidebar. Each workflow card shows:

- The workflow name and its source file
- Validation status: **Valid**, **Invalid** (with an error), or **Warning**
- Whether it is the project's **default** workflow

Click a workflow to expand it and see its full YAML definition, a visual graph of the roles and steps, or the raw step-by-step pipeline.

### Setting the default workflow

Click **Set as default** on any valid workflow to make it the project's default. When a submitted task matches no specific workflow — or when auto-matching is overridden — the default is used.

::: tip Clear the default to restore the built-in
Setting the default to "none" (clearing the selection) reverts to the system's built-in default workflow.
:::

### Syncing workflows

If you edit a workflow YAML file on disk or add a new one, click **Sync** on the Workflows page to re-read the `.agentweaver/workflows/` directory and refresh the list. This is an explicit sync — Agentweaver does not watch the filesystem. In multi-replica deployments, the synced files live in the shared project workspace; other API replicas detect the changed workflow file set on their next registry read and refresh their local cache.

## Authoring a workflow

### YAML editor

Click **New workflow** to open the visual editor with a YAML-backed template. Use **Edit** on an existing project workflow when you prefer to edit its YAML directly.

### Visual editor

Use **Edit visually** to build a workflow as a node graph. Drag roles onto the canvas, connect them, and configure each step visually. The editor generates the YAML for you.

Click **Add node** to insert a new step. The palette is grouped under **Reviewers & gates**, **Agent steps**, and **Flow control**, and each entry shows an icon and a one-line description. **Build & Test** appears once, as a ready-to-use preset.

For existing project workflows, use **Edit** to open the YAML editor or **Edit visually** to open the graph editor. Built-in workflows are read-only; use **Duplicate to project** to create an editable copy and open it in the visual editor.

## Running and scheduling workflows

Each workflow row shows whether it is **Manual only** or has a schedule, including its UTC cadence. Use **Run now** to queue a Ready task bound to that workflow; it is picked up and shown on the board through the same normal coordinator path as other work.

For project workflows, choose **Add schedule** or **Edit schedule** to run the workflow daily, weekly, or monthly at a UTC time. Removing the schedule returns the workflow to manual-only operation.

## Triggering workflows from GitHub

Each GitHub-connected project can receive repository events through its own webhook. Open the
project's **Settings → Webhooks** page, then select **Generate secret**. Copy the generated value
immediately: it is shown once only. In your GitHub repository, go to **Settings → Webhooks → Add
webhook** and configure:

- **Payload URL:** the project-specific URL displayed in Agentweaver:
  `https://your-agentweaver-host/api/projects/<project-id>/webhooks/github`
- **Content type:** `application/json`
- **Secret:** the generated project secret
- **Events:** choose the GitHub events your workflow needs.

GitHub signs each delivery with the project's secret. Agentweaver rejects unsigned or invalid
deliveries, and rotating a secret invalidates the old value immediately. The old global
`/api/webhooks/github` URL is no longer supported.

An event delivery named by GitHub's `X-GitHub-Event` header fires `github.<event>` (for example,
`github.push` or `github.issues`). When the payload has an `action`, it also fires the more specific
`github.<event>.<action>` name, such as `github.issues.opened` or
`github.pull_request.opened`.

For example, this project workflow starts whenever an issue is opened:

```yaml
id: triage-new-issue
name: Triage newly opened issue
start: triage
nodes:
  - id: triage
    type: prompt
    role: backend-engineer
    prompt: Triage the newly opened GitHub issue.
  - id: done
    type: terminal
edges:
  - from: triage
    to: done
trigger:
  type: event
  event_name: github.issues.opened
```

### Generate from description

Choose **Generate from description**, type what you want the workflow to do in plain language, and Agentweaver generates an initial YAML draft for you to review and edit. If the description includes a recurring cadence such as "Every Monday", the draft uses a `schedule` trigger with a cadence like `weekly:monday`. If the project was created from GitHub — or your prompt includes a GitHub repository or issue URL — generation keeps that target repository in the prompt context so the draft acts against the intended repo.

The generated workflow is preview-first: Agentweaver opens the YAML draft in the editor and does not write it to `.agentweaver/workflows/` until you save. If validation fails after the server's correction pass, the API returns an error instead of saving a broken workflow.

![Generate from description: Describe workflow, LLM generates YAML, Review in editor, Validate, Save to .agentweaver/workflows/](../diagrams/guide-workflows-fig1.png)

<!-- Rendered from ../diagrams/src/guide-workflows-fig1.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

::: warning Workflows affect team composition
A workflow references specific roles by name. If your project's cast doesn't include a role referenced in the workflow, the run will fail validation before it starts. Make sure the workflow's required roles match the agents in your team.
:::

## Workflow lifecycle in a run

When a run executes against a workflow:

1. The workflow is resolved — built-in or project-local
2. The coordinator decomposes the workflow's steps into the WorkPlan
3. Each step is dispatched to the agent whose role matches the step's role binding
4. Steps execute in the order the workflow specifies (parallel where there are no dependencies)
5. Outputs from one step become inputs to the next

The live topology view shows each workflow step as a node, with edges representing the data flow between steps.

## Blueprints bundle workflows

When you save a team as a **Blueprint**, the Blueprint bundles the team's roster, one or more workflows (with a designated default), and the project's review and sandbox policies. Instantiating the Blueprint into a new project automatically materializes the workflow files into the new project's `.agentweaver/workflows/` directory.

→ [Agent Teams & Blueprints](./teams)
