---
title: Agent Teams & Blueprints
---

# Agent Teams & Blueprints

Every Agentweaver project has a **Squad** — a group of named specialist agents, each with a role, a charter, and a model tier. Squads are defined by **Blueprints** and brought to life through **casting**. Named agents are drawn from thematic universes — for example, Matrix characters or Star Wars characters — so your team has a consistent, memorable identity across runs.

## What is a Blueprint?

A Blueprint is a reusable, versioned definition of how a team works. It bundles everything a project needs to run:

- **Roster** — role definitions (role name, responsibilities, optional model preference). No concrete persona names — those are assigned at instantiation by the casting algorithm.
- **Workflows** — one or more YAML workflow definitions, with one designated as the default
- **Review policy** — which automated checks gate the team's output
- **Sandbox policy** — what shell commands, network access, and destructive operations agents are permitted to perform

A Blueprint is **universe-agnostic**. It says "we need a Software Engineer role and a QA Engineer role." The casting algorithm then says "let's call them Neo and Trinity."

## The five predefined Blueprints

Agentweaver ships five predefined Blueprints. Pick the one that most closely matches your team's work:

### Software Development

A team for software engineering tasks: coding, testing, debugging, review, and architecture. Roles cover engineering, quality assurance, and technical direction. The default workflow is `software-delivery`. The catalog also includes `bug-fix` and `infra-ops`.

**Best for:** Software engineers and tech leads shipping code changes, features, and refactors.

### Content Authoring

A team for creating and editing written content — documentation, blog posts, READMEs, release notes, marketing copy, and technical articles. Roles cover writing, editing, fact-checking, and review.

**Best for:** Technical writers, DevRel engineers, and content creators.

### Product Management

A team for product work — discovery, spec writing, roadmap planning, stakeholder communication, user research synthesis, and opportunity framing. Roles cover product management, research, and analysis. The default workflow is `pm-discovery`.

**Best for:** Product managers and researchers turning intent into discovery outcomes.

### Product & Software Delivery

A combined team that spans product and engineering — from spec to shipped code. Roles cover the full delivery lifecycle: product, engineering, QA, and coordination. Useful for small, cross-functional teams that do both product and engineering work.

**Best for:** Full-stack teams that own both spec and delivery.

### AI Agent Engineering

A team for building, evaluating, and hardening AI agents and agentic workflows — prompt and tool design, evaluation, and safety review.

**Best for:** Teams developing agents, prompts, and agent-evaluation harnesses.

::: tip Fork to customize
Start with the closest predefined Blueprint, then fork it to add a role, swap the default workflow, or tighten the sandbox policy. Forked Blueprints are independent — the original is unchanged.
:::

## How casting works

Casting is the process that turns a Blueprint's abstract **roles** into the named **agents** that populate your project's team.

When you instantiate a Blueprint:

1. The casting algorithm reads the Blueprint's roster of role definitions
2. For each role, it selects a persona name from a thematic universe (e.g., Matrix, Star Wars)
3. The named agents are recorded in the project's team files

![How casting works: Blueprint roster, Named squad, Your project team](../diagrams/guide-teams-fig1.png)

<!-- Rendered from ../diagrams/src/guide-teams-fig1.json by docs/diagram-renderer +
     Playwright (Fluent-styled React Flow), replacing a Mermaid flowchart.
     Edit the JSON, then run `npm run docs:render-diagrams` and commit the
     regenerated PNG + .hash.txt. -->

The persona names are not stored in the Blueprint — the Blueprint is universe-agnostic. The same Blueprint instantiated into two different projects may produce different named agents.

## Three ways to cast a team

You can cast a team in three ways from the **Team** page or **Casting Wizard**:

| Method | When to use it |
|---|---|
| **From a scenario** | You know the kind of work you'll do (software delivery, content authoring, etc.). Pick a Blueprint and cast. |
| **From a free-text goal** | Describe what your project is trying to achieve. The casting wizard suggests a roster based on the goal. |
| **From project analysis** | The wizard reads your project's existing files and history to suggest the best-fit team composition. |

## The Coordinator

Every project team includes a built-in **Coordinator** agent. The Coordinator is not one of the Blueprint's roster roles — it is always present.

The Coordinator:

- **Scopes** your goal using team memories and decisions, then drafts an OutcomeSpec
- **Confirms** the spec with you before dispatching any work
- **Plans** — decomposes the confirmed spec into a WorkPlan with a dependency graph
- **Dispatches** — assigns subtasks to roster agents; a run's model pin (explicit `modelId` or the project default) selects the model for every subtask, otherwise each uses its role's default model; runs independent subtasks in parallel
- **Steers** — monitors each agent via a read-only timeline; relays your direction (stop, redirect, amend)
- **Assembles** — collects each agent's output into one combined result
- **Routes review feedback** — if RAI flags an issue or you request changes, the Coordinator dispatches fixes

::: tip The Coordinator always confirms before dispatching
No agent work starts until you confirm the OutcomeSpec. If the spec doesn't match your intent, give feedback — the Coordinator revises as many times as needed.
:::

## The Team page

Navigate to **Team** from a project to see the squad roster.

![Team page](/guide/images/team-page.png)

Filter tabs: **All**, **Active**, **Retired**. Retired members were cast in a previous configuration.

Click any agent card to open a drawer:

| Tab | What it shows |
|---|---|
| **Overview** | Name, role title, model tier, and current status |
| **Charter** | The agent's responsibilities and behavioral guidelines |
| **Capabilities** | The tools and permissions this agent has |

### Adding and re-roling members

- **Add member** — add a new agent and assign them a role
- **Re-role** — change an existing agent's role (opens the re-role panel; the agent gets a new charter for the new role)

## Team Memory

Agents accumulate **memories** and **decisions** across runs. Navigate to **Team Memory** from a project sidebar.

### The four memory layers

Every agent's context for a run is compiled from four layers, in order of priority:

1. **Active Decisions** (highest priority) — finalized architectural and scope decisions. Hard constraints the team must honor.
2. **Core context** — standing project-level context that applies to every run.
3. **Learnings and patterns** — the top high-importance entries from prior runs. These accumulate over time.
4. **Open session** — the current run's working context.

### Decision types

Agents submit entries to the **Decision Inbox** with a type:

| Type | What it captures |
|---|---|
| `learning` | Something learned from this run |
| `pattern` | A recurring approach or anti-pattern |
| `update` | An update to prior knowledge |
| `architectural` | A significant architectural decision |
| `scope` | A scope constraint or boundary decision |
| `process` | A working-process or convention decision |
| `technical` | A concrete technical decision |

::: tip Architectural and scope decisions are coordinator-reviewed
Any agent can submit any inbox type. After a run, the **Scribe** auto-merges only
`learning`, `pattern`, and `update` entries attributed to that completed run. It leaves
ordinary-agent `architectural` and `scope` entries pending. A project owner or verified
Coordinator run must accept those team-wide boundaries; the Coordinator finalization
path may promote architectural and scope entries authored by that same Coordinator run.
:::

### The Decision Inbox

The **Decisions** tab on the Team Memory page shows:

- **Finalized decisions** — entries that have been accepted into the shared ledger
- **Proposed decisions** (dashed border) — entries that agents submitted during recent runs, pending review

For each proposed entry you can:

- **Merge** — accept as-is and add to the finalized ledger
- **Promote** — promote it (with optional edits) to a decision
- **Reject** — discard the proposal

These actions cross the trust boundary. The API accepts them only from a project owner
or a verified Coordinator run. Rejection retains the inbox record for audit rather than
deleting it.

### Agent Memory

The **Agent Memory** tab shows individual memory entries for each agent — learnings, preferences, and context that carry forward to future runs. Each entry has:

- An **importance** level (high / medium / low) — the agent uses this to prioritize what to surface
- A **type** label
- The **content**

You can create entries manually and update existing ones from this tab.

Memory and decisions carry provenance (`human`, `run`, or `legacy`) and a trust state:

| Trust state | Effect |
|---|---|
| `pending` | New memory may inform its named agent, but cannot cross to another agent. |
| `approved` | Eligible for the normal compilation rules; approved cross-team memory may be shared. |
| `legacy` | Migrated record with unknown provenance; excluded from prompt compilation until approved. |

Only a project owner or verified Coordinator run can approve memory. Active
architectural and scope decisions are compiled only when their trust state is
`approved`.

### Scribe

After each completed orchestration, a **Scribe** agent runs automatically. The Scribe:

- Auto-merges run-attributed `learning`, `pattern`, and `update` inbox entries
- Leaves ordinary-agent architectural and scope proposals pending for authorized review
- Writes a session log summarizing what happened
- Updates the cross-agent history
- Archives large history files that have grown past a threshold

You don't need to trigger the Scribe manually — it runs as part of the post-merge pipeline.

## Team state is file-native

Team state is mirrored as human-readable files in the project's working directory
(`.agentweaver/` and `.squad/`) so it can be inspected and version-controlled. The
structured memory database remains authoritative for trust state and review
transitions. Editing a mirror does not make its text trusted; importing an inbox file
creates a proposal that must pass the same authorization and promotion rules.

## Saving a team as a Blueprint

Once you have a team you're happy with, you can save it as a reusable Blueprint. From the Team page, click **Save as Blueprint** (or generate one from a description). The Blueprint bundles:

- The current roster (as role definitions — persona names are stripped)
- The project's active workflows (with the default designated)
- The project's review policy
- The project's sandbox policy

The saved Blueprint appears in the Blueprint catalog and can be instantiated into any new project.