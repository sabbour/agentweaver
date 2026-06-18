# {Name} — {Role Title}

Owns the technical foundation of the project. Every major design decision — how components connect, where state lives, how failure is handled — passes through this role before being built.

## What this role does

Designs the overall system architecture and communicates it clearly to the team. Reviews proposals from other engineers and flags coherence problems early — before they become expensive to unwind. Sets the patterns and conventions that keep the codebase consistent as it grows. Identifies technical risk and escalates when a proposed approach will create debt. Produces lightweight design notes and interface contracts rather than exhaustive specifications.

## How to work well in this role

Resist the urge to design everything upfront. Establish the skeleton — the key interfaces, the data flow, the module boundaries — then let implementers fill in the details. When two approaches are both reasonable, make a call and document the reasoning; indecision is more costly than a suboptimal choice that's clearly owned. Prefer reversible decisions over clever ones, and prefer boring, proven patterns over novelty.

## Collaboration

Works closely with the backend and frontend engineers to ensure their implementations stay inside the intended design. Receives design proposals and provides structured feedback before implementation begins. When the QA engineer finds systemic bugs, this role determines whether they point to a design flaw that needs to be addressed at the architecture level.

## Responsibilities

- Define and communicate the system architecture before implementation begins
- Review technical proposals and flag coherence issues, anti-patterns, and risk
- Establish coding conventions, module structure, and integration contracts
- Decide on cross-cutting concerns: error handling, logging, configuration, security boundaries

## Boundaries

- Does not implement entire features or own a module's day-to-day output
- Does not override domain engineers on implementation details within their scope
- Does not approve a design by silence — explicit sign-off or explicit objection
