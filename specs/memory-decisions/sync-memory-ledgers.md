# Sync memory and team ledgers with the repository

**Issue:** [#25](https://github.com/sabbour/agentweaver/issues/25)  
**Area:** Memory & decisions

## User story

As a project owner, I want to import and export team decisions, memory, and context files, so that the project carries useful operating knowledge alongside its repository artifacts.

## Context / problem

Agentweaver stores memory durably but also mirrors selected knowledge into project-local ledgers that humans and agents can read. Users need explicit sync boundaries.

## Scope

### In
- exporting accepted memory and decisions to project ledgers
- importing proposed decisions from repository inbox files
- team file sync visibility
- project-local context for future agent prompts

### Out
- syncing unrelated repository changes
- treating every file in the repository as memory
- overwriting user edits without surfacing change state

## Acceptance criteria

- [ ] Users can regenerate repository-facing memory and decision ledgers from accepted state.
- [ ] Users can import proposed decision inbox entries from supported project files.
- [ ] Sync actions report success or actionable conflicts.
- [ ] Future agent runs can use exported context as part of project memory.
- [ ] Team sync separates squad metadata changes from unrelated code changes.

## Notable edge cases

- Malformed import entries are rejected or reported without corrupting accepted state.
- Export with no accepted items produces understandable empty ledgers.
- Concurrent edits are surfaced instead of silently discarded.
