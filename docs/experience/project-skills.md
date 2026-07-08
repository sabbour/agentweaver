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

![Skills Catalog tab with catalog toolbar and skill cards](/screenshots/skills-catalog.png)

> 📸 **Screenshot — `skills-catalog.png`**
> *Shows:* the **Skills** page **Catalog** tab with **Add Skill**, **Generate Skill**, **Import Skill**, **Sync connected repo**, catalog status/provenance badges, assigned-agent chips, and **View** / **Delete** actions.
> *Path:* open a project → click **Skills** → `/projects/:projectId/skills`.

## Add skills

The Catalog toolbar supports three acquisition paths:

1. **Add Skill**: write a command slug, description, and instructions. Agentweaver saves a standard
   `SKILL.md` with `name` and `description` frontmatter.
2. **Generate Skill**: describe the skill. The server generates a draft, then you review and edit it
   before creating the catalog skill.
3. **Import Skill**: upload `.md` skill files, upload a folder with `SKILL.md`, or paste one of the
   supported source URLs — `owner/repo`, a `https://github.com` repo URL, a GitHub `tree`/`blob`
   folder URL, or a raw `https://raw.githubusercontent.com/.../SKILL.md` URL. Preview lists every
   candidate skill discovered at the source so you can select which ones to import.
4. **Sync connected repo**: scans `.github/skills`, `.copilot/skills`, `.claude/skills`, and
   `.agents/skills` in the project repository.

Imports accept a single `SKILL.md`, a folder of `<name>/SKILL.md` directories, or recognized repo
folders under `.github/skills`, `.copilot/skills`, `.claude/skills`, and `.agents/skills`.

::: warning Only GitHub sources are accepted
For safety against server-side request forgery, imports are restricted to two hosts:
`github.com` (public HTTPS, default port, no embedded credentials) and
`raw.githubusercontent.com`. Any other host — including `http://`, `git@` SSH URLs, non-default
ports, or `user@host` tricks — is rejected before anything is cloned or fetched. Only import skills
from sources you trust, because imported instructions can change how an agent behaves.
:::

Each catalog card shows status, provenance, updated time, source location, assigned agents, and a
**View** action for the full instructions. Repeating an acquisition is safe: unchanged content is
reported as unchanged, changed content updates the existing skill, and invalid content is rejected
with validation errors.

![Import Skill dialog with dropzones, trusted-source warning, URL field, and candidate preview controls](/screenshots/skill-import-dialog.png)

> 📸 **Screenshot — `skill-import-dialog.png`**
> *Shows:* the **Import Skill** dialog with the trusted-source warning, `.md` file and skill-folder dropzones, GitHub/raw URL field, **Preview candidates**, candidate selection, and **Import**.
> *Path:* `/projects/:projectId/skills` → click **Import Skill**.

## Assign skills to agents

Open the **Assignments** tab after the project has a cast team. Each skill row shows a checkbox per
agent. Check an agent to assign the skill; clear it to unassign. Each agent is labelled with its
**role** alongside its name (for example, *Kai — Backend Engineer*) so you can tell cast members
apart by what they do rather than only by name.

Only assigned and active skills appear for that agent at run time. The prompt shows the skill name,
description, and path to the materialized `SKILL.md`; the full skill body is available lazily in the
run worktree only if the agent needs it.

You can also drop skill files or a **folder** directly onto the Import dropzone. Single files come
through the browser's normal file list, but folders are read through the FileSystem entry API and
recursed so every `<name>/SKILL.md` inside is discovered with its correct relative path — dropping a
whole `skills/` directory imports each skill it contains. Binary and oversized (>1 MiB) files are
skipped automatically.

## See assigned skills on an agent

Assigned skills also appear on the agent itself. Open an agent from the **Team** page and its detail
panel lists its **Assigned skills** — each skill's name and description — so you can review what an
agent is equipped with without switching to the Skills page. If nothing is assigned, the panel says
**No skills assigned**.

## See also

- [Project skills deep dive](../deep-dive/project-skills.md)
- [Project skills reference](../reference/project-skills.md)
- [Agent Teams & Blueprints](../guide/teams.md)
