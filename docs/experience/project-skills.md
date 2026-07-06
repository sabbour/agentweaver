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

1. **Add Skill**: write a command slug, description, and instructions. Agentweaver saves a standard
   `SKILL.md` with `name` and `description` frontmatter.
2. **Generate Skill**: describe the skill. The server generates a draft, then you review and edit it
   before creating the catalog skill.
3. **Import Skill**: upload `.md` skill files, upload a folder with `SKILL.md`, or paste a raw
   `SKILL.md` URL, `owner/repo`, GitHub repo URL, GitHub tree/blob folder URL, or `git@` SSH URL.
4. **Sync connected repo**: scans `.github/skills`, `.copilot/skills`, `.claude/skills`, and
   `.agents/skills` in the project repository.

Imports accept a single `SKILL.md`, a folder of `<name>/SKILL.md` directories, or recognized repo
folders under `.github/skills`, `.copilot/skills`, `.claude/skills`, and `.agents/skills`. Only import
skills from sources you trust because imported instructions can change how an agent behaves.

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
