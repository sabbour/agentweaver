# Edit workflows with the generation prompt

**Issue:** Draft (not opened)  
**Area:** Workflows & automation

## User story

As a project owner, I want to describe a change to an existing workflow in natural language, so that I can evolve a process without rebuilding it from scratch or hand-editing its structure.

## Context / problem

Agentweaver can generate a new workflow draft from a description, but workflow improvement is iterative. Users often want to add, remove, reorder, or refine steps in a workflow they already use. The edit experience should preserve the workflow's intent, apply only the requested change, and keep review in the user's control before anything is saved.

## Scope

### In
- natural-language edits to existing project workflows
- natural-language edits to built-in or library workflows as project-owned customized copies
- structural edits to steps, dependencies, triggers, gates, and review paths
- preview of the proposed workflow changes before save
- discard without changing the saved workflow
- iterative re-editing from the latest reviewed draft or saved workflow
- validation before a changed workflow can be saved or used
- preserving the original workflow's identity and intent unless the edit explicitly changes them

### Out
- mutating shared built-in or library workflow definitions in place
- auto-saving model edits without review
- accepting invalid or unrunnable workflow drafts
- replacing the visual or text workflow editor
- inferring unrelated improvements beyond the user's requested change

## Acceptance criteria

- [ ] Users can select an existing project workflow and describe an edit in natural language.
- [ ] Users can select a built-in or library workflow and create a customized project-owned copy from a natural-language edit.
- [ ] The proposed edit preserves the original workflow's purpose and unchanged structure unless the prompt asks otherwise.
- [ ] Edits can add, remove, reorder, or modify workflow steps and dependencies.
- [ ] Edits can add or modify supported trigger structure.
- [ ] Users can preview the proposed changes before saving.
- [ ] Users can discard a proposed edit without changing the saved workflow.
- [ ] Users can apply another natural-language edit after reviewing a draft.
- [ ] Saving validates the resulting workflow and rejects invalid or non-runnable definitions.
- [ ] The saved edited workflow appears alongside other project workflows and can be selected like any other valid workflow.

## Notable edge cases

- If the prompt conflicts with the workflow's existing purpose, the draft highlights the conflict instead of silently rewriting the workflow into something unrelated.
- If a built-in workflow is edited, the shared library remains unchanged and the project receives its own derived workflow.
- If an edit would remove a required gate or terminal path, validation blocks the save with actionable feedback.
- If the generated draft changes more than requested, the preview makes that visible so the user can discard it.
- If an iterative edit starts from an unsaved draft, the user can still choose whether to save or discard the accumulated changes.
