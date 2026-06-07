---
description: "Show alignment between your spec, tasks, and Squad agents"
---

# Squad Bridge: Status

Cross-reference the Speckit spec, task list, and Squad team to surface
coverage gaps, unrouted work, and idle agents. Useful as a health check
before starting a session or after a spec change.

## User Input

$ARGUMENTS

## Steps

1. **Load all artifacts**:
   - Spec: `specs/<id>/spec.md`
   - Tasks: `specs/<id>/tasks.md` (if exists)
   - Squad agents: `.squad/agents/` — each agent in `.squad/agents/{name}/charter.md` (if exists)
   - Squad routing: `.squad/routing.md` (if exists)
   - Bridge config: `.specify/extensions/squad/squad-config.yml`

2. **Check: Spec domains vs. agent coverage**

   For each technology domain and concern identified in the spec, determine
   whether an active Squad agent covers it:

   ```
   Domain Coverage
   ──────────────────────────────────────────────
   Domain              Agent                Status
   ──────────────────────────────────────────────
   REST API / Node.js  backend-engineer     ✅ covered
   React / TypeScript  frontend-engineer    ✅ covered
   PostgreSQL          data-engineer        ✅ covered
   CI/CD / DevOps      (none)               ⚠️ gap
   Mobile (iOS)        mobile-engineer      ⚠️ inactive
   ──────────────────────────────────────────────
   ```

3. **Check: Tasks vs. routing**

   For each task (if `specs/<id>/tasks.md` exists), verify it can be routed
   to an active agent via the current routing rules:

   ```
   Task Routing Coverage
   ─────────────────────────────────────────────────────
   Total tasks        : 12
   Routable tasks     : 10  ✅
   Unroutable tasks   :  2  ⚠️  (run /speckit.squad.route)
   ─────────────────────────────────────────────────────
   ```

4. **Check: Agent utilization**

   List active agents and flag any with no tasks mapped to them:

   ```
   Agent Utilization
   ────────────────────────────────────────
   Agent               Tasks  Status
   ────────────────────────────────────────
   backend-engineer    5      ✅ active
   frontend-engineer   4      ✅ active
   qa-engineer         3      ✅ active
   data-engineer       0      ⚠️ no tasks assigned
   ────────────────────────────────────────
   ```

5. **Check: Squad CLI health**

   Run `squad doctor` and include the output summary.

6. **Print recommended actions** based on the checks above. Examples:
   - "Run `/speckit.squad.generate` — CI/CD domain not covered by any agent"
   - "Run `/speckit.squad.route` — 2 tasks have no agent assignment"
   - "Consider activating `mobile-engineer` — mobile domain present in spec"

## Notes

- If `$ARGUMENTS` is `--brief`, print only the summary counts and recommended
  actions (skip the detailed tables).
- If no `.squad/` directory is found, recommend running
  `/speckit.squad.init` and stop.
