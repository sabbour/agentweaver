# Generate and save custom workflows

**Issue:** [#12](https://github.com/sabbour/agentweaver/issues/12)  
**Area:** Workflows & automation

## User story

As a project owner, I want to describe a workflow, review the generated draft, and save it only when valid, so that I can create project-specific processes without hand-authoring everything from scratch.

## Context / problem

Workflow generation should accelerate process design without committing unreviewed or invalid definitions. The product boundary is draft first, strict save second.

## Scope

### In
- plain-language workflow generation
- editable generated draft
- validation before save
- visual or text editing for project-authored workflows
- alignment with project roles when available

### Out
- auto-saving generated workflows without review
- generating workflows for unavailable roles without feedback
- guaranteeing model creativity beyond valid process output

## Acceptance criteria

- [ ] Generation returns a draft for review rather than immediately changing the project.
- [ ] Users can edit the draft before saving.
- [ ] Saving validates the workflow and rejects invalid or non-runnable definitions.
- [ ] Saved workflows appear in the workflow library after refresh.
- [ ] Generation failures or correction limits produce clear feedback.

## Notable edge cases

- Generated drafts can require user edits before save.
- A workflow id mismatch or invalid structure blocks persistence.
- Projects with a cast team constrain generated role usage to available roles where possible.
