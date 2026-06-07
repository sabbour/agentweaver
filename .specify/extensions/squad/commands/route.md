---
description: "Route open Speckit tasks to the right Squad agents based on capabilities"
---

# Squad Bridge: Route Tasks

Read open tasks from the Speckit task list and assign each to the most
appropriate Squad agent using capability matching or round-robin (configured
in `squad-config.yml`).

This command is also triggered by the `after_tasks` hook.

## User Input

$ARGUMENTS

## Steps

1. **Verify prerequisites**:
   - `specs/<id>/tasks.md` exists; if not, tell the user to run
     `/speckit.tasks` first and stop.
   - `.squad/` exists; if not, tell the user to run
     `/speckit.squad.init` first and stop.

2. **Load bridge config** from `.specify/extensions/squad/squad-config.yml`
   if it exists, otherwise use extension defaults.

3. **Read open tasks** from `specs/<id>/tasks.md`. Parse each task's title,
   description, and any tags or phase labels.

4. **Load Squad agents** and routing rules from `.squad/` (agents directory
   and `routing.md`). Only consider agents with `status: active`.

5. **Route each task** using the configured `routing_strategy`:

   **`capability-match`** (default):
   - Extract domain keywords from the task title and description
   - Match against agent capabilities using the routing rules in `.squad/routing.md`
   - If multiple agents match, prefer the one with the highest capability level
     for the matched domain
   - If no agent matches, assign to coordinator and flag for manual review

   **`round-robin`**:
   - Assign tasks to active agents in a rotating sequence
   - Track assignment counts to ensure even distribution

6. **Determine model tier** for each task:
   - Tasks involving architecture decisions, complex algorithms, or spec
     interpretation → `full` tier
   - Standard feature implementation → `standard` tier
   - Boilerplate, scaffolding, documentation → `lightweight` tier

7. **Output a routing table**:

   ```
   Task Routing Summary
   ─────────────────────────────────────────────────────────────────────
   Task                                  Agent               Tier
   ─────────────────────────────────────────────────────────────────────
   Design REST API endpoints             backend-engineer    full
   Implement JWT authentication          backend-engineer    standard
   Build login form component            frontend-engineer   standard
   Write unit tests for auth service     qa-engineer         standard
   Set up GitHub Actions CI pipeline     devops-engineer     standard
   Add README and API docs               ⚠️ coordinator      lightweight
   ─────────────────────────────────────────────────────────────────────
   Routed: 5 / 6   Unrouted (needs review): 1
   ```

8. **Update `.squad/routing.md`** with any new routing patterns inferred
   from this task batch that aren't already covered.

9. If `$ARGUMENTS` contains `--update-tasks`, annotate `specs/<id>/tasks.md`
   with agent assignments as metadata comments (e.g.,
   `<!-- squad:agent=backend-engineer tier=standard -->`).

## Notes

- Unrouted tasks (flagged with ⚠️) mean no active agent has a matching
  capability. Consider running `/speckit.squad.generate` if the spec has
  expanded, or manually add an agent with `squad` commands.
- This command does not start or trigger any Squad sessions — it only
  produces routing assignments. Use `squad` directly to begin work.
