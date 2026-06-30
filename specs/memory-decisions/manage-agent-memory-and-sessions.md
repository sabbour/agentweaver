# Manage agent memory and session context

**Issue:** [#23](https://github.com/sabbour/agentweaver/issues/23)  
**Area:** Memory & decisions

## User story

As a project owner, I want to record, view, search, and update team memory and current session focus, so that future runs start with useful project knowledge rather than a blank slate.

## Context / problem

Agentweaver memory captures durable learnings, patterns, context, and the current session. Users need to inspect and shape that memory without confusing it with accepted decisions.

## Scope

### In
- agent memory creation and listing
- cross-agent memory search
- importance and type filtering
- current session start, update, and end
- Scribe-captured learnings after runs

### Out
- using memory as unreviewed policy authority
- unbounded prompt history dumping
- cross-project memory leakage

## Acceptance criteria

- [ ] Users can view and create memory entries with agent, type, importance, and content.
- [ ] Users can search memory across agents within a project.
- [ ] Current session context can be started, updated, summarized, and ended.
- [ ] Run completion can add useful learnings without changing the run outcome.
- [ ] Memory remains scoped to the project and relevant agents.

## Notable edge cases

- Empty memory states explain that no entries exist yet.
- Session updates handle an already-open session predictably.
- Low-quality or obsolete memory can be updated rather than silently overriding decisions.
