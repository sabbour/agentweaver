# Agent definition

When creating a project, Agentweaver attempts to add a **GitHub Copilot agent definition**
that knows how to drive the platform. The shipped definition is generated from the MCP
tool source. Materialization is best-effort: a filesystem failure does not fail project
creation, and an existing definition is left unchanged.

This page covers the experience: where the file shows up, how to use it with GitHub Copilot, and why it
won't fight your edits. For the generation mechanics see the
[deep dive](../deep-dive/agent-definition.md); for the exact contract see the
[reference](../reference/agent-definition.md).

## What you get when you create a project

For a blank or GitHub-cloned project, the intended file is:

```
.github/agents/agentweaver.agent.md
```

That file is the **Agentweaver Driver** agent. It contains a mental model of the platform (projects,
blueprints, runs, the Coordinator, backlog, memory), operating principles, a **Tool map** of every
`agentweaver-*` MCP tool grouped by category, and step-by-step playbooks (submit and supervise a run, stand
up a project and team, work the backlog, curate memory and decisions).

It is written **only if the file is not already there** and the workspace is writable.
Check that it exists before selecting the agent. The non-overwrite and non-fatal failure
contracts are implemented in `apps/Agentweaver.Api/Projects/AgentDefinitionTemplate.cs:50`.

## Using it with GitHub Copilot

GitHub Copilot automatically discovers agent files under `.github/agents/`. After your project is created:

1. Open the project's working directory in an editor with GitHub Copilot.
2. In Copilot's agent picker, choose **Agentweaver Driver** (its `description` tells Copilot to use it when
   you mention Agentweaver, "spin up a team", "run a workflow", a project / blueprint / run / coordinator,
   or any `agentweaver-*` tool).
3. Ask it to do platform work in plain language — e.g. *"spin up a software-development project and run the
   software-delivery workflow."* The agent translates that into the right sequence of `agentweaver-*` MCP
   tool calls, supervises the run, and reports back.

The agent file is just markdown — open it any time to read exactly which tools and playbooks the agent
knows.

## It won't overwrite your edits

The file is yours once it lands. If you customize it — tighten the playbooks, add house rules, change the
description — Agentweaver will **not** clobber your version. Materialization only writes when the file is
absent, so your edits are safe across anything that re-touches the project.

If you ever want to reset to the shipped version, delete your copy and recreate the project (or copy the
current definition from the repo's own `.github/agents/agentweaver.agent.md`).

## How the shipped definition stays correct

The shipped template has a generated Tool map:

- The Tool map is **generated** from the MCP server source for that release.
- The copy embedded in the app and the copy in the repo are kept **byte-identical**, and CI fails the build
  if either drifts.
- New materializations use that release's definition; existing project copies are not upgraded.

An older project copy can therefore lag a newer server. For the generated tool list, see the
[MCP tool index](../reference/mcp-tools.md); the connected server's `tools/list` describes
the tools actually available to the client.

## What to expect

- **It's best-effort.** No registration step is needed, but check the file after creation.
- **It's per-project.** A materialized copy lives under that project's `.github/agents/`
  and can be committed alongside your code.
- **It's non-destructive.** Existing agent files are never overwritten.
- **It's not a web-UI feature.** There's no button or dialog to manage it — it's a file that appears in your
  project. Manage it like any other file in your repo.

## Related reading

- [Agent definition — Reference](../reference/agent-definition.md) — generation contract, regenerate command,
  `--check` gate, materialization table.
- [Agent definition — Deep Dive](../deep-dive/agent-definition.md) — how generation and materialization work
  end to end.
- [Projects (User Guide)](./projects.md) — creating and managing projects, where this file gets written.
- [MCP tool index](../reference/mcp-tools.md) — the tools the agent drives.
