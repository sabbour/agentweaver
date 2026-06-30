# Assign skills to project agents

**Issue:** Draft (not opened)  
**Area:** Agent configuration

## User story

As a project owner, I want to install reusable skills and assign them to specific agents, so that each agent receives relevant specialized guidance without bloating every agent's context.

## Context / problem

Agentweaver agents already have roles, charters, and project memory, but they do not yet have a project-level way to reuse standards-based skill modules. Skills should follow the common Agent Skills and GitHub Copilot convention: a named, described instruction module with optional bundled resources, made available only to agents that are assigned to use it.

## Scope

### In
- standards-compatible skills with name, description, instructions, and optional resources
- role-default skills that ship with selected Agentweaver roles
- project-level installation of additional skills
- assignment of one skill to one or more project agents
- per-agent prompt visibility scoped to that agent's assigned skills
- blueprint-bundled skills that install and assign with blueprint-cast roles
- progressive disclosure: skill metadata first, full instructions only when relevant

### Out
- curating or embedding a starter catalog of favorite skills
- making every installed skill available to every agent by default
- eagerly injecting full skill bodies into every prompt
- replacing charters, memory, or review policies with skills
- executing skill scripts without the existing tool and approval boundaries

## Acceptance criteria

- [ ] Users can install additional standards-compatible skills into a project.
- [ ] Users can assign each installed skill to one or more agents in that project.
- [ ] Selected roles can declare default skills that are assigned automatically when agents are cast for those roles.
- [ ] Blueprints that include roles also bring the role-appropriate skills and assignments when the team is cast.
- [ ] An agent sees only the skill catalog entries for skills assigned to that agent.
- [ ] The prompt exposes skill names and descriptions up front, not the full body of every assigned skill.
- [ ] Full skill instructions and bundled resources become available on demand only when the agent needs the skill.
- [ ] Skill assignments remain project-scoped and do not leak to unrelated projects or unassigned agents.

## Notable edge cases

- If two installed skills share a name, the project has a deterministic way to choose or reject the duplicate.
- If a skill is malformed or missing required metadata, it is not silently assigned to agents.
- If an assigned skill is removed, affected agents no longer see it in their available skill catalog.
- If a blueprint references an unavailable skill, casting reports the gap instead of creating hidden prompt drift.
