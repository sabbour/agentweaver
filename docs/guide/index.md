---
title: What is Agentweaver?
---

# What is Agentweaver?

**Describe the work. Generate the team and workflow. Run it on your infrastructure.**

Agentweaver is a platform for running teams of AI agents on infrastructure you control. Describe a software delivery, content, product, operations, or organization-specific process. Agentweaver can generate its roles, skills, and workflow. It then runs the team in isolated sandboxes.

The agents remain probabilistic. Agentweaver makes their path toward the outcome governed and repeatable through persisted workflow state, explicit gates, and human approvals. Use the web interface or connect an assistant, editor, or CLI through MCP.

Start in the **Project Gallery** for project work, or **Sessions** for a personal
Assistant conversation. Project pages expose the board, team, workflows, and run history.

## How it works

### Coordinator orchestration

For a **define-outcome** submission with autopilot off, the coordinator:

1. Drafts an **OutcomeSpec** — goal, desired outcome, scope, assumptions
2. Selects the best-fit **workflow** for your task via an LLM pass over available workflows and team roles — surfacing the choice and rationale. You can override from the **Start task** dialog or by typing `use {workflow-id}` in the coordinator chat.
3. Asks for your confirmation before any work starts
4. Decomposes the confirmed spec into a **WorkPlan** — subtasks arranged in a dependency graph
5. Dispatches child agents in parallel, each in their own sandbox
6. Shows a **live topology graph** of every agent and its status
7. Lets you **steer mid-run** — send a directive, redirect a child, amend the plan, or stop
8. Assembles all results into one combined diff
9. Runs the selected collective gates, including RAI and applicable Build & Test, then human review
10. Runs a **Scribe pass** after merge to record what the team learned

Direct mode skips outcome drafting; explicit autopilot can confirm a define-outcome run
without a manual pause. Neither is permission to bypass tool or human merge approvals.

## Key concepts

### Projects

A **Project** contains a git working directory, project orchestration, its team, and team
memory, with an effective provider and model configuration. Create from scratch or clone
from GitHub. Personal [Assistant sessions](./assistant) are separate conversations, not project-owned runs.

→ [Working with Projects](./projects)

### Blueprints and casting

A **Blueprint** is a reusable team definition: roles, workflows, review policy, and sandbox policy. Start from a predefined Blueprint or generate one from a description of the work. When you instantiate it into a project, the **casting algorithm** assigns named agents to each role.

→ [Agent Teams & Blueprints](./teams)

### Workflows

**Workflows** are YAML-defined multi-role pipelines that define the scenario. Agentweaver ships seven built-in workflows:

| Workflow | Use it for |
|---|---|
| `software-delivery` | Code changes, features, refactors |
| `bug-fix` | Targeted bug investigations and fixes |
| `infra-ops` | Infrastructure, CI/CD, monitoring, and alerting work |
| `content-authoring` | Drafting docs, blog posts, articles |
| `pm-discovery` | Product discovery, research, specs |
| `incident-response` | Live incidents and postmortems |
| `agent-evaluation` | Testing and evaluating agent outputs |

When you submit a task, an LLM pass automatically matches it to the best-fit workflow. You can also author your own or generate one from a description.

→ [Workflows](./workflows)

### The board

Every project has a **Kanban board** with six semantic buckets, presented as a main row
and a separate attention section:

| Column | Owned by |
|---|---|
| **Backlog** | You — capture tasks here |
| **Ready** | You — drag tasks here when ready to run |
| **Problems** | Coordinator — failed runs land here with reason |
| **Human Review** | Coordinator — runs awaiting your approval |
| **Active** | Coordinator — currently running orchestrations |
| **Done** | Coordinator — completed, merged runs |

A **heartbeat** periodically promotes Ready tasks and starts coordinator runs up to a configurable limit.

→ [Board and Backlog](./board)

### Runs

A **Run** is a unit of execution. Project implementation work uses isolated git worktrees
and durable event streams; collective changes require your approval before merge.
Personal Assistant conversations do not require a project checkout.

→ [Submitting and Watching Runs](./runs)

### Review & Merge

Before merging, coordinator orchestrations evaluate the selected workflow's collective
gates over the assembled output, not per child. Built-in software workflows include RAI,
Build & Test, and human approval.

→ [Reviewing and Merging](./review)

### Team Memory

Agents build on prior work through four memory layers compiled into every agent's context:

1. **Active Decisions** — hard constraints (architectural and scope decisions)
2. **Core context** — project-level standing context
3. **Learnings and patterns** — top high-importance entries from prior runs
4. **Open session** — current run context

Agents submit entries to a **Decision Inbox** typed as learning, pattern, update, architectural, or scope.
Every memory and decision carries provenance and trust metadata. The Scribe only
auto-merges low-risk `learning`, `pattern`, and `update` entries attributed to that
completed run. Architectural and scope proposals stay pending unless a project owner
or a verified Coordinator run accepts them (including the Coordinator's own
finalization backstop). Existing memory and decision records migrated as `legacy` are
excluded from prompt compilation until explicitly approved.

→ [Agent Teams & Blueprints — Memory](./teams#team-memory)

### MCP server

The full Agentweaver feature set is available programmatically through an MCP server. Claude Desktop, VS Code, GitHub Copilot CLI, and GitHub Copilot desktop can connect to the hosted `/mcp` endpoint and complete OAuth sign-in without a manually copied bearer token. GitHub Copilot users can install the Agentweaver Driver definition from the public docs URL and select it for tool-aware operation.

→ [Connect an MCP client](./mcp-cli)

## Why Agentweaver

| Other tools | Agentweaver |
|---|---|
| Orchestration primitives you wire up yourself | Generated or reusable teams, skills, and workflows in one platform |
| Optional HITL through workflow patterns | Define-outcome confirmation or direct launch; mandatory human approval before merge |
| State in opaque managed stores | Inspectable file mirrors backed by authoritative structured memory/decision trust records |
| One review gate per agent | Single collective review over all assembled work |
| Vendor-hosted control plane | Run the platform on infrastructure you control |
| Code-only scenarios | Any knowledge-work scenario via the workflow system |

## Next steps

- [Sign in with Microsoft Entra ID](./authentication) to get started
- [Create your first project](./projects)
- [Learn about workflows](./workflows)
- [Submit your first run](./runs)
- [Connect an MCP client](./mcp-cli)

<details id="diagram-context-canonical-coordinator-journey" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>One goal, one collective review</td></tr>
<tr><td>subtitle</td><td>Confirm intent, dispatch bounded work, then integrate and review the whole result.</td></tr>
<tr><td>group-title0</td><td>Plan and execute</td></tr>
<tr><td>group-title1</td><td>Integrate, review, finish</td></tr>
<tr><td>Confirm intent</td><td>Confirm intent</td></tr>
<tr><td>Confirm intent</td><td>Draft the OutcomeSpec</td></tr>
<tr><td>Confirm intent</td><td>human confirmation</td></tr>
<tr><td>Plan the work</td><td>Plan the work</td></tr>
<tr><td>Plan the work</td><td>Persist a WorkPlan DAG</td></tr>
<tr><td>Plan the work</td><td>subtasks + dependencies</td></tr>
<tr><td>Dispatch children</td><td>Dispatch children</td></tr>
<tr><td>Dispatch children</td><td>Run the eligible frontier</td></tr>
<tr><td>Dispatch children</td><td>per-child worktrees</td></tr>
<tr><td>Merge + Scribe</td><td>Merge + Scribe</td></tr>
<tr><td>Merge + Scribe</td><td>Approved integration path</td></tr>
<tr><td>Merge + Scribe</td><td>MergeWorktree → Scribe</td></tr>
<tr><td>Collective review</td><td>Collective review</td></tr>
<tr><td>Collective review</td><td>One human decision</td></tr>
<tr><td>Collective review</td><td>approve / revise / decline</td></tr>
<tr><td>Integrate + gates</td><td>Integrate + gates</td></tr>
<tr><td>Integrate + gates</td><td>Assemble child branches</td></tr>
<tr><td>Integrate + gates</td><td>configured checks / review</td></tr>
<tr><td>e1</td><td>confirm</td></tr>
<tr><td>e2</td><td>dispatch</td></tr>
<tr><td>e3</td><td>settled work</td></tr>
<tr><td>e4</td><td>request review</td></tr>
<tr><td>e5</td><td>approve</td></tr>
<tr><td>assurance-title</td><td>DO NOT CONFUSE ASSEMBLY WITH PUBLICATION</td></tr>
<tr><td>assurance-line1</td><td>The collective workflow reaches MergeWorktree and Scribe; this graphic does not promise PR creation.</td></tr>
<tr><td>assurance-line2</td><td>A blocked assembly can be recovered. Review approval does not itself mark the run complete.</td></tr>
<tr><td>Confirm intent</td><td>Input</td></tr>
<tr><td>Confirm intent</td><td>Human goal</td></tr>
<tr><td>Confirm intent</td><td>Artifact</td></tr>
<tr><td>Confirm intent</td><td>OutcomeSpec</td></tr>
<tr><td>Confirm intent</td><td>Gate</td></tr>
<tr><td>Confirm intent</td><td>Confirm or revise</td></tr>
<tr><td>Confirm intent</td><td>Scope</td></tr>
<tr><td>Confirm intent</td><td>Explicit assumptions</td></tr>
<tr><td>Plan the work</td><td>Select</td></tr>
<tr><td>Plan the work</td><td>Workflow choice</td></tr>
<tr><td>Plan the work</td><td>WorkPlan DAG</td></tr>
<tr><td>Plan the work</td><td>Owners</td></tr>
<tr><td>Plan the work</td><td>Named subtasks</td></tr>
<tr><td>Plan the work</td><td>Store</td></tr>
<tr><td>Plan the work</td><td>Persist dependencies</td></tr>
<tr><td>Dispatch children</td><td>Ready</td></tr>
<tr><td>Dispatch children</td><td>Satisfied dependencies</td></tr>
<tr><td>Dispatch children</td><td>Files</td></tr>
<tr><td>Dispatch children</td><td>Child-owned worktree</td></tr>
<tr><td>Dispatch children</td><td>Observe</td></tr>
<tr><td>Dispatch children</td><td>Child status / results</td></tr>
<tr><td>Dispatch children</td><td>Failure</td></tr>
<tr><td>Dispatch children</td><td>Blocks dependents</td></tr>
<tr><td>Merge + Scribe</td><td>Merge</td></tr>
<tr><td>Merge + Scribe</td><td>Reviewed integration</td></tr>
<tr><td>Merge + Scribe</td><td>Then</td></tr>
<tr><td>Merge + Scribe</td><td>Collective Scribe</td></tr>
<tr><td>Merge + Scribe</td><td>Record</td></tr>
<tr><td>Merge + Scribe</td><td>Promote decisions</td></tr>
<tr><td>Merge + Scribe</td><td>Decline</td></tr>
<tr><td>Merge + Scribe</td><td>Skips Scribe</td></tr>
<tr><td>Collective review</td><td>Approve</td></tr>
<tr><td>Collective review</td><td>Proceed to merge</td></tr>
<tr><td>Collective review</td><td>Revise</td></tr>
<tr><td>Collective review</td><td>Steer / redispatch</td></tr>
<tr><td>Collective review</td><td>No Scribe path</td></tr>
<tr><td>Collective review</td><td>Blocked</td></tr>
<tr><td>Collective review</td><td>Recoverable state</td></tr>
<tr><td>Integrate + gates</td><td>Child branches</td></tr>
<tr><td>Integrate + gates</td><td>Target</td></tr>
<tr><td>Integrate + gates</td><td>Integration branch</td></tr>
<tr><td>Integrate + gates</td><td>Gates</td></tr>
<tr><td>Integrate + gates</td><td>Selected checks</td></tr>
<tr><td>Integrate + gates</td><td>Output</td></tr>
<tr><td>intent</td><td>Scope and assumptions are explicit; Revision reopens the intent gate</td></tr>
<tr><td>plan</td><td>Outcome-complete decomposition; Bounded work with named owners</td></tr>
<tr><td>dispatch</td><td>Observe child status and results; Failure / RAI blocks dependents</td></tr>
<tr><td>finish</td><td>Decline skips Scribe; No automatic PR claim here</td></tr>
<tr><td>review</td><td>Changes can redispatch work; Blocked is recoverable, not terminal</td></tr>
<tr><td>integrate</td><td>Collective—not per-child delivery; Merge failure may still run Scribe</td></tr>
<tr><td>notes</td><td>DO NOT CONFUSE ASSEMBLY WITH PUBLICATION; The collective workflow reaches MergeWorktree and Scribe; this graphic does not promise PR creation.; A blocked assembly can be recovered. Review approval does not itself mark the run complete.</td></tr>
<tr><td>groups</td><td>Plan and execute; Integrate, review, finish</td></tr>
</tbody></table>
</details>
