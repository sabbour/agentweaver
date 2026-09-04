# Render Fluent-styled documentation diagrams

**Issue:** [#1188](https://github.com/sabbour/agentweaver/issues/1188)  
**Area:** Deployment & platform

## User story

As a documentation maintainer, I want flowcharts and sequence diagrams to use
the same static Fluent-styled rendering pipeline so architecture documentation
has one consistent visual language.

## Context / problem

The graph-spec renderer produces polished Fluent cards, but Mermaid
`sequenceDiagram` blocks still use generic Mermaid styling.

## Scope

### In

- A discriminated sequence-diagram JSON spec and schema.
- Participant cards, lifelines, messages, activations, notes, and combined fragments.
- Migration of supported Mermaid sequence diagrams to static PNG embeds.
- Backward compatibility for graph specs without a `kind` field.

### Out

- Replacing state, class, entity-relationship, or other Mermaid diagram types.
- Interactive sequence diagrams in the published site.

## Acceptance criteria

- Existing graph specs render without modification.
- Sequence diagrams use the resolved Agentweaver Fluent palette and card style.
- Repository sequence diagrams migrate without losing their participant or message order.
- Diagram hash checks and the docs build pass.

## Notable edge cases

- Self-messages and long labels remain readable.
- Nested `alt`, `opt`, and `loop` fragments retain section boundaries.
- Unsupported Mermaid syntax stays in its original fence rather than being corrupted.
