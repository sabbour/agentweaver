---
title: Project skills
---

# Project skills

Use project skills when a team member needs specialized reusable instructions, checklists, templates,
or domain playbooks. Skills are added to the project catalog first, then assigned to individual
agents.

## Open the skills page

From a project, go to **Skills** or open `/projects/:projectId/skills`. The page has two tabs:

- **Catalog**: add, inspect, and delete skills.
- **Assignments**: choose which agents receive each skill.

## Add skills

The Catalog toolbar supports three acquisition paths:

1. **Sync connected repo**: scans `.github/skills`, `.copilot/skills`, `.claude/skills`, and
   `.agents/skills` in the project repository.
2. **Import from repo**: enter a Git repository URL, preview valid candidates, select locations, then
   import them into the catalog.
3. **Upload**: upload one or more files, a folder selection, or a `.zip` archive. Each uploaded skill
   must include a `SKILL.md`.

Each catalog card shows status, provenance, updated time, source location, assigned agents, and a
**View** action for the full instructions. Repeating an acquisition is safe: unchanged content is
reported as unchanged, changed content updates the existing skill, and invalid content is rejected
with validation errors.

## Assign skills to agents

Open the **Assignments** tab after the project has a cast team. Each skill row shows a checkbox per
agent. Check an agent to assign the skill; clear it to unassign.

Only assigned and active skills appear for that agent at run time. The prompt shows the skill name,
description, and path to the materialized `SKILL.md`; the full skill body is available lazily in the
run worktree only if the agent needs it.

## See also

- [Project skills deep dive](../deep-dive/project-skills.md)
- [Project skills reference](../reference/project-skills.md)
- [Agent Teams & Blueprints](../guide/teams.md)
