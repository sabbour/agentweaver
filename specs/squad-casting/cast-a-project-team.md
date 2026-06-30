# Cast a project team from a goal or template

**Issue:** [#7](https://github.com/sabbour/agentweaver/issues/7)  
**Area:** Squad casting

## User story

As a project owner, I want to generate and confirm a named team of specialist agents, so that multi-agent work starts from clear roles, stable names, and reviewable charters.

## Context / problem

Agentweaver treats agents as a project squad rather than ad hoc prompts. Casting turns goals, scenario templates, or project analysis into a proposal that the user can review before committing.

## Scope

### In
- free-text, template, analysis, and role-based casting paths
- proposal review before confirmation
- augmenting or recasting an existing team
- stable team names within one universe

### Out
- unreviewed automatic roster replacement
- model-internal casting rules
- workflow execution after casting

## Acceptance criteria

- [ ] Users can start casting when a project has no team or needs a new roster.
- [ ] The system presents a proposal with names, roles, charters, and warnings before commitment.
- [ ] Users can confirm, cancel, augment, or recast according to the existing team state.
- [ ] Confirmed teams appear on the Agents page with active members and system agents.
- [ ] Team names remain stable identifiers for history and memory.

## Notable edge cases

- Invalid or empty role selections are blocked before confirmation.
- Existing teams require an explicit augment-versus-recast choice.
- System agents are present but protected from ordinary removal or re-role actions.
