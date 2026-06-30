# Operate Agentweaver from a browser chat control console

**Issue:** Draft (not opened)  
**Area:** MCP & integrations

## User story

As an Agentweaver operator, I want a browser-based chat console for managing Agentweaver in natural language, so that I can shape work, launch existing orchestration flows, and monitor progress without leaving the web app.

## Context / problem

Agentweaver already exposes project, backlog, workflow, orchestration, review, and memory operations through the web UI and MCP-compatible tools. Users need a lightweight in-browser command surface that feels like a chat REPL for the control plane: conversational, link-rich, and live-updating, while still sending work through the existing Agentweaver lifecycle.

## Scope

### In
- natural-language control-plane chat for project and backlog management
- creating, editing, ranking, decomposing, and promoting backlog work through existing product actions
- starting coordinator orchestrations and surfacing their confirmation or monitoring paths
- clickable links to created tasks, board locations, runs, orchestration details, and existing monitoring views
- live streaming updates about relevant Agentweaver work while the console is open
- clear separation between management actions and actual agent execution

### Out
- running agent work inside the console itself
- replacing the board, run timeline, workflow graph, or orchestration topology views
- bypassing existing confirmation, approval, review, or merge gates
- introducing a second execution system or separate source of truth

## Acceptance criteria

- [ ] Users can open an in-browser chat console and ask Agentweaver to manage projects, backlog items, work breakdown, and orchestration starts.
- [ ] Console actions use the same authorized product capabilities as existing MCP/API surfaces.
- [ ] The console never executes agent work directly; any work execution is started and monitored through existing runs and orchestrations.
- [ ] When the console creates or starts something, it returns clickable links to the relevant board, task, run, workflow, or orchestration view.
- [ ] The console streams live updates for relevant backlog, run, and orchestration activity while preserving the durable event history shown elsewhere.
- [ ] Sensitive transitions such as confirmation, approval, review, and merge remain explicit and consistent with existing Agentweaver gates.

## Notable edge cases

- If a requested management action is ambiguous, the console asks for clarification instead of guessing and launching work.
- If a created item is later deleted, archived, or becomes inaccessible, its link fails gracefully and points the user back to the project context.
- If live streaming disconnects, the console can reconnect or fall back to a current-state summary without losing the authoritative run history.
- If the user lacks access to a project or run, the console reports the authorization boundary instead of exposing hidden state.
