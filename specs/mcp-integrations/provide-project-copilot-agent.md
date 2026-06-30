# Provide a Copilot agent for every project

**Issue:** [#35](https://github.com/sabbour/agentweaver/issues/35)  
**Area:** MCP & integrations

## User story

As a project owner, I want each project to include a ready-to-use Copilot agent definition for Agentweaver, so that I can start using Agentweaver from Copilot without hand-writing the tool map.

## Context / problem

Agentweaver can be driven by compatible assistants. A project-local Copilot agent gives users a discoverable, versioned entry point tailored to the live MCP surface.

## Scope

### In
- project-local assistant definition
- tool map generated from current MCP capabilities
- preservation of user edits
- refresh when tool surface changes
- clear role for driving Agentweaver rather than replacing it

### Out
- forcing a single IDE or assistant client
- overwriting customized agent instructions silently
- running work outside Agentweaver governance

## Acceptance criteria

- [ ] New or synced projects can surface a Copilot agent definition for Agentweaver use.
- [ ] The agent definition describes how to use Agentweaver tools and product concepts.
- [ ] Generated tool information matches the available MCP tool surface.
- [ ] User edits are preserved or conflicts are surfaced rather than overwritten.
- [ ] The agent directs work through Agentweaver projects, runs, review, and memory.

## Notable edge cases

- Missing MCP capabilities produce a reduced but valid definition.
- Regeneration with local edits avoids destructive overwrite.
- Projects without assistant support can still use the web UI and MCP directly.
