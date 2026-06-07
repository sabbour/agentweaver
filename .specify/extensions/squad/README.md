# spec-kit-squad

A [Spec Kit](https://github.com/github/spec-kit) extension that bridges
[Squad](https://bradygaster.github.io/squad/) — bootstrapping and
synchronizing an AI agent team from your implementation plan, with
execution support.

**Speckit generates the *what*** (spec → plan → tasks).  
**Squad manages the *who*** (agents with specialized capabilities).  
**This extension connects them and gets the work done.**

---

## How It Works

```mermaid
flowchart TD
    A["/speckit.specify"] --> B["specs/&lt;id&gt;/spec.md"]
    B --> B2["/speckit.plan"]
    B2 --> B3["specs/&lt;id&gt;/plan.md"]
    B3 --> C["/speckit.squad.init"]
    C --> D[".squad/\nagents + routing"]
    E["/speckit.tasks"] --> F["specs/&lt;id&gt;/tasks.md"]
    F --> G["/speckit.squad.route"]
    D --> H["Task → Agent assignments\n+ routing.md updated"]
    G --> H
    H --> I["/speckit.squad.run"]
    I --> J["Agent sessions execute\nbranches + PRs created"]
```

After you create your implementation plan, the extension reads it to extract
your technology stack, then generates a Squad team to match. As your plan
evolves, `generate` keeps the team in sync. When tasks are generated, `route`
distributes them to the right agents, and `run` executes them through Squad
agent sessions.

---

## Requirements

- [Spec Kit](https://github.com/github/spec-kit) `>=0.8.11`
- [Squad](https://bradygaster.github.io/squad/) `>=0.9.4`
- [GitHub CLI](https://cli.github.com) `>=2.0.0` (required for `/speckit.squad.run`)

```bash
npm install -g @bradygaster/squad-cli
```

---

## Installation

```bash
specify extension add squad --from https://github.com/jwill824/spec-kit-squad/archive/refs/tags/v1.3.0.zip
```

Or for local development:

```bash
specify extension add squad --dev /path/to/spec-kit-squad
```

---

## Commands

> **Invoking commands by tool:**
>
> - **Claude Code / VS Code Copilot:** Type `/speckit.squad.<command>` directly
> - **GitHub Copilot CLI:** Type `/agents` → select `speckit.squad.<command>` → enter your prompt

### `/speckit.squad.init`

Bootstrap a Squad team from the **implementation plan**. Run this once after
your initial `/speckit.plan`.

- Reads `specs/<id>/plan.md` (primary) and `specs/<id>/spec.md` (context)
- Extracts technology stack, architecture layers, and phases from the plan
- Runs `squad init` if `.squad/` doesn't exist
- Creates agent definitions in `.squad/agents/`
- Generates routing rules in `.squad/routing.md`
- Writes `squad.config.ts` at the project root

```
/speckit.squad.init
```

---

### `/speckit.squad.generate`

Re-generate agent definitions as the plan evolves. Safe to run repeatedly —
agents are updated in place; removed domains are marked `inactive`, not
deleted. Also triggered by the `after_plan` hook.

```
/speckit.squad.generate
/speckit.squad.generate frontend   # limit to a specific domain
```

---

### `/speckit.squad.route`

Route open Speckit tasks to Squad agents using capability matching. Also
triggered by the `after_tasks` hook.

```
/speckit.squad.route
/speckit.squad.route --update-tasks   # annotate tasks.md with assignments
```

---

### `/speckit.squad.status`

Health check: cross-reference the spec, tasks, and squad to surface coverage
gaps and idle agents.

```
/speckit.squad.status
/speckit.squad.status --brief   # summary only
```

---

### `/speckit.squad.run`

Execute routed tasks through Squad agent sessions. Creates a branch per task,
launches agent sessions (up to `parallel_limit` concurrently), and optionally
creates PRs on completion.

```
/speckit.squad.run                   # run all routed tasks
/speckit.squad.run --dry-run         # preview without executing
/speckit.squad.run --retry           # re-run failed tasks
/speckit.squad.run task-03           # run a specific task
```

---

## Configuration

After installation, copy the config template:

```bash
cp .specify/extensions/squad/squad-config.template.yml \
   .specify/extensions/squad/squad-config.yml
```

Key options:

| Option | Default | Description |
| --- | --- | --- |
| `agent_model` | `claude-sonnet-4` | Model used when generating agents |
| `routing_strategy` | `capability-match` | `capability-match` or `round-robin` |
| `squad_root` | `.squad` | Path to Squad root directory |
| `model_tiers.full` | `claude-opus-4` | Model for complex tasks |
| `model_tiers.standard` | `claude-sonnet-4` | Model for standard tasks |
| `model_tiers.lightweight` | `claude-haiku-4.5` | Model for simple tasks |
| `execution.parallel_limit` | `3` | Max parallel agent sessions |
| `execution.auto_pr` | `false` | Auto-create PRs on task completion |
| `execution.session_timeout_minutes` | `30` | Agent session timeout |
| `execution.branch_pattern` | `squad/{agent}/{task-id}` | Branch naming pattern |
| `execution.copilot_flags` | `--yolo` | Flags passed to agent sessions |

---

## Hooks

| Hook | Command | Default |
| --- | --- | --- |
| `after_plan` | `speckit.squad.generate` | Optional (prompts user) |
| `after_tasks` | `speckit.squad.route` | Optional (prompts user) |

> **Why `after_plan` instead of `after_specify`?** The spec is intentionally
> tech-agnostic — it captures goals and constraints without dictating
> implementation. The plan is where concrete technology decisions live
> (e.g., "React 19", "Go with gin", "PostgreSQL"). Generating agents from
> the plan produces sharper charters with accurate capabilities and routing.

---

## Typical Workflow

```mermaid
flowchart LR
    A["/speckit.specify"] --> B["/speckit.plan"]
    B --> C["/speckit.squad.init"]
    C --> D["/speckit.tasks"]
    D --> E["/speckit.squad.route"]
    E --> F["/speckit.squad.run"]
    F --> G["/speckit.squad.status"]
```

---

## Troubleshooting

**`squad: command not found`**
Squad is not installed. Run `npm install -g @bradygaster/squad-cli` and verify with `squad --version`.

**`/speckit.squad.init` reports no plan found**
Run `/speckit.plan` first — the init command reads `specs/<id>/plan.md`, not
`spec.md`. The spec is tech-agnostic; the plan contains the technology
decisions needed to generate meaningful agents.

**Agents not appearing after init**
Check `.squad/agents/` exists. If the directory is missing, Squad CLI may not have initialized correctly. Try `squad init` manually, then re-run `/speckit.squad.init`.

**Hook fires unexpectedly**
Both hooks (`after_plan`, `after_tasks`) are optional and will prompt before running. If you want to disable them, remove the `hooks:` section from your local copy of `extension.yml` or set the hook's `optional: false` to always skip the prompt.

---

## License

MIT
