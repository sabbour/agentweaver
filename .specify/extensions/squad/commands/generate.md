---
description: "Re-generate Squad agent definitions as your plan evolves"
---

# Squad Bridge: Generate

Re-read the implementation plan and regenerate Squad agent definitions,
capabilities, and routing rules to stay in sync with plan changes. Safe to
run repeatedly — existing agents are updated in place; agents no longer
supported by the plan are flagged (not deleted).

This command is also triggered by the `after_plan` hook.

## User Input

$ARGUMENTS

## Steps

1. **Verify `.squad/` exists** — if not, tell the user to run
   `/speckit.squad.init` first and stop.

2. **Read the plan** from the active spec directory under `specs/` (e.g.,
   `specs/001-<name>/plan.md`). Also read `spec.md` for supplementary
   context (goals, constraints).

3. **Load bridge config** from `.specify/extensions/squad/squad-config.yml`
   if it exists, otherwise use extension defaults.

4. **Read existing agents** from `.squad/agents/` (each agent lives in
   `.squad/agents/{name}/charter.md`) so changes can be diffed rather than
   blindly overwritten. Preserve `history.md` files (accumulated project
   knowledge — never touch these).

5. **Analyze the plan** to extract technology stack, architecture layers,
   implementation phases, and cross-cutting concerns (same logic as `init`).
   If `$ARGUMENTS` names a specific domain, limit regeneration to that
   domain's agent.

6. **Diff against existing agents**:
   - **New domains** found in plan but no matching agent → create new agent
   - **Changed domains** (different prominence or tech stack) → update agent
     capabilities and model tier
   - **Removed domains** (in existing agents but absent from new plan) →
     set `status: inactive` and note in output (do NOT delete)

7. **Update `squad.config.ts`** at the project root (using `defineSquad()` with
   `defineTeam()`, `defineAgent()`, and `defineRouting()`) to reflect the new
   agent set, routing rules, and model tier assignments.

8. **Update `.squad/routing.md`** to add routing rules for any new agents and
   update patterns for changed agents.

9. **Print a diff summary**:

   ```
   Squad agents updated from plan
     ✅ Added   : data-engineer (PostgreSQL/migrations — proficient)
     ✏️  Updated : backend-engineer (added GraphQL capability)
     ⚠️  Inactive: mobile-engineer (no longer in plan — set to inactive)
   
   Routing rules updated: 8 total (2 added, 1 modified)
   ```

## Notes

- `inactive` agents remain in `.squad/` and can be reactivated manually with
  `squad` commands if needed.
- If the plan has not changed since the last run, this command reports
  "No changes detected" and exits cleanly.
- Agent `history.md` files are never modified by this command — they contain
  accumulated project knowledge written by agents during work sessions.
