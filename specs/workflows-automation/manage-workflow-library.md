# Manage reusable workflow definitions

**Issue:** [#11](https://github.com/sabbour/agentweaver/issues/11)  
**Area:** Workflows & automation

## User story

As a project owner, I want to view, validate, select, edit, and sync project workflows, so that work follows the right process for the task instead of a single hard-coded pipeline.

## Context / problem

Workflows define how Agentweaver runs work. Users need to see which workflows are active, available, or invalid and choose safe defaults for future work.

## Scope

### In
- Workflows page
- active/default workflow selection
- built-in and project-authored workflows
- workflow validation status
- workflow graph preview
- explicit sync from project workflow files

### Out
- low-level workflow runtime internals
- editing built-in workflows in place
- running invalid workflows

## Acceptance criteria

- [ ] Users can distinguish active, available, built-in, project-authored, and invalid workflows.
- [ ] Only valid workflows can become the project default.
- [ ] Users can inspect a valid workflow graph before using it.
- [ ] Sync refreshes discovered project workflows and surfaces validation errors.
- [ ] Invalid workflows remain visible as actionable configuration work.

## Notable edge cases

- Resetting to the built-in default is available.
- A broken workflow cannot silently become active.
- Sync with no changes leaves the current default stable.
