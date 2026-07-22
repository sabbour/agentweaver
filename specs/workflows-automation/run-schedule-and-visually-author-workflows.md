# Run, schedule, and visually author workflows

**Issue:** [#442](https://github.com/sabbour/agentweaver/issues/442)  
**Area:** Workflows & automation

## User story

As a project owner, I want to run and schedule a workflow from its library row and easily find the visual editor, so that I can automate and customize my process without hand-editing YAML.

## Context / problem

Schedule triggers and the visual editor already existed but lacked a discoverable workflow-library surface.

## Scope

### In
- Schedule status, configuration, and removal for project workflows
- Manual workflow-bound runs through the normal Ready backlog path
- Duplicating read-only built-in workflows into visual-editor-ready project copies
- Visual editor as the default new-workflow surface

### Out
- Editing built-in workflows in place
- Arbitrary cron expressions or sub-daily schedules

## Acceptance criteria

- [ ] A workflow row identifies its manual or scheduled state and UTC cadence.
- [ ] Owners can configure daily, weekly, or monthly schedules for project workflows.
- [ ] Run now creates a Ready task bound to the selected workflow.
- [ ] Built-ins can be duplicated to an editable project workflow and opened visually.

## Notable edge cases

- Invalid or unbindable workflows cannot be run.
- Schedule day-of-month is constrained to 1–28.
