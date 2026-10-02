# Agentweaver 1.0

This branch (`v1`) is where Agentweaver 1.0 is built. It starts from an empty history on
purpose: 1.0 is a rebuild, not a refactor of 0.x.

- **0.x keeps shipping** on [`dev`](https://github.com/sabbour/agentweaver/tree/dev). It is the reference for behavior, not
  a source of code to copy.
- **1.0 ports behavior, not code.** Each 0.x capability is tracked in a parity map and
  rebuilt behind the 1.0 contracts.
- **Status:** design proposal. There is no code on this branch yet.

## Start here

| Document | What it covers |
| --- | --- |
| [ADR 0001: Agentweaver 1.0 platform architecture](docs/architecture/decisions/0001-platform-architecture.md) | Overview: goals, decisions, architecture at a glance, delivery strategy, roadmap alignment, phases, risk register |
| [Provider seams](docs/architecture/design/provider-seams.md) | Provider model, the 15 seams, per-seam designs, conformance |
| [Sessions and coordination](docs/architecture/design/sessions-and-coordination.md) | Run journal, suspend/resume, session tree, messages, knowledge records |
| [Orchestration](docs/architecture/design/orchestration.md) | Thin coordinator, workflow step catalogs, typed decision tools, rules in code |
| [Applications and surfaces](docs/architecture/design/applications-and-surfaces.md) | One application model (`live` → `preview` → `published`), Application Hosting, the surface panel |
| [Services and release](docs/architecture/design/services-and-release.md) | Service decomposition, data plane, per-service versioning and release |

## What 1.0 changes

- Backends plug in through **provider seams** instead of being wired into one monolith.
- The platform splits into independently versioned **control-plane and data-plane
  services**.
- Agent memory, decisions, and inbox move from repository files into the **session tree
  and messages**.
- The coordinator prompt stays **thin**; workflow rules live in code.
- Previews and published applications become **one application model**, shown in
  Agentweaver's own UI next to a **surface panel**.
- 1.0 runs **in the cloud only** (AKS and Azure first). There is no local runtime mode.
