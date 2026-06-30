# Drive Agentweaver through MCP tools

**Issue:** [#33](https://github.com/sabbour/agentweaver/issues/33)  
**Area:** MCP & integrations

## User story

As a developer using an assistant, I want to operate Agentweaver projects, teams, backlog, runs, workflows, reviews, and memory through MCP tools, so that I can pair with an assistant that acts in the same product state the web UI shows.

## Context / problem

MCP is the programmatic product surface. It should mirror core Agentweaver capabilities so assistants can help without inventing parallel state.

## Scope

### In
- project and team tools
- backlog and workflow tools
- coordinator and run tools
- artifact review tools
- memory and diagnostics tools
- structured errors for invalid actions

### Out
- silent arbitrary file edits outside Agentweaver runs
- client-specific conversational policy
- capabilities that bypass web/API authorization

## Acceptance criteria

- [ ] An MCP client can discover and invoke grouped tools for the same domains exposed in the UI.
- [ ] Tool calls operate on the same projects, board state, runs, reviews, and memory as the web app.
- [ ] Long-running run watch reports progress and terminal state.
- [ ] Errors are returned as actionable tool failures.
- [ ] Sensitive or final decisions remain explicit tool calls.

## Notable edge cases

- MCP IDs and paths are treated as data, not route manipulation.
- Preview/generation tools do not persist changes unless explicitly confirmed.
- Unsupported or invalid state transitions fail without partial product state.
