# Documentation diagram audit

Agentweaver publishes one curated set of **15 flagship diagrams**. Legacy
public PNGs, flat sources, generated XML, and review/export trees were removed. Product
documentation embeds only the assets listed below.

This change is documentation-only. It does not modify coordinator topology, workflows,
cluster topology, APIs, or application rendering.

| # | Concept | Preview | Structured source | Editable source | Consumers |
|---|---|---|---|---|---|
| 1 | System architecture | [PNG](docs/diagrams/flagship/canonical-coordinator-architecture.png) | [JSON](docs/diagrams/src/flagship/canonical-coordinator-architecture.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-coordinator-architecture.drawio) | [README.md](README.md)<br>[docs/deep-dive/coordinator-internals.md](docs/deep-dive/coordinator-internals.md) |
| 2 | Coordinator-to-agent communication and A2A runtime | [PNG](docs/diagrams/flagship/canonical-coordinator-runtime-sequence.png) | [JSON](docs/diagrams/src/flagship/canonical-coordinator-runtime-sequence.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-coordinator-runtime-sequence.drawio) | [docs/deep-dive/coordinator-internals.md](docs/deep-dive/coordinator-internals.md)<br>[docs/deep-dive/a2a-bridge.md](docs/deep-dive/a2a-bridge.md) |
| 3 | Coordinator journey | [PNG](docs/diagrams/flagship/canonical-coordinator-journey.png) | [JSON](docs/diagrams/src/flagship/canonical-coordinator-journey.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-coordinator-journey.drawio) | [docs/guide/runs.md](docs/guide/runs.md) |
| 4 | Default workflow | [PNG](docs/diagrams/flagship/canonical-default-workflow.png) | [JSON](docs/diagrams/src/flagship/canonical-default-workflow.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-default-workflow.drawio) | [docs/guide/workflows.md](docs/guide/workflows.md)<br>[docs/workflow-library.md](docs/workflow-library.md) |
| 5 | Workflow authoring | [PNG](docs/diagrams/flagship/canonical-workflow-authoring.png) | [JSON](docs/diagrams/src/flagship/canonical-workflow-authoring.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-workflow-authoring.drawio) | [docs/workflow-generation.md](docs/workflow-generation.md) |
| 6 | Workflow selection | [PNG](docs/diagrams/flagship/canonical-workflow-selection.png) | [JSON](docs/diagrams/src/flagship/canonical-workflow-selection.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-workflow-selection.drawio) | [docs/workflow-selection.md](docs/workflow-selection.md) |
| 7 | Workflow invocation | [PNG](docs/diagrams/flagship/canonical-workflow-invocation.png) | [JSON](docs/diagrams/src/flagship/canonical-workflow-invocation.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-workflow-invocation.drawio) | [docs/deep-dive/workflow-engine.md](docs/deep-dive/workflow-engine.md) |
| 8 | Durable events | [PNG](docs/diagrams/flagship/canonical-durable-event-stream-sequence.png) | [JSON](docs/diagrams/src/flagship/canonical-durable-event-stream-sequence.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-durable-event-stream-sequence.drawio) | [docs/deep-dive/events-observability.md](docs/deep-dive/events-observability.md) |
| 9 | Board lifecycle | [PNG](docs/diagrams/flagship/canonical-board-lifecycle.png) | [JSON](docs/diagrams/src/flagship/canonical-board-lifecycle.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-board-lifecycle.drawio) | [docs/guide/board.md](docs/guide/board.md) |
| 10 | Context compilation and progressive tool disclosure | [PNG](docs/diagrams/flagship/canonical-memory-context.png) | [JSON](docs/diagrams/src/flagship/canonical-memory-context.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-memory-context.drawio) | [docs/deep-dive/memory-decisions.md](docs/deep-dive/memory-decisions.md)<br>[docs/deep-dive/agent-runtime.md](docs/deep-dive/agent-runtime.md) |
| 11 | Provider admission | [PNG](docs/diagrams/flagship/canonical-provider-admission.png) | [JSON](docs/diagrams/src/flagship/canonical-provider-admission.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-provider-admission.drawio) | [docs/deep-dive/project-generation-model-settings.md](docs/deep-dive/project-generation-model-settings.md) |
| 12 | Sandbox boundary | [PNG](docs/diagrams/flagship/canonical-sandbox-boundary.png) | [JSON](docs/diagrams/src/flagship/canonical-sandbox-boundary.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-sandbox-boundary.drawio) | [docs/deep-dive/sandbox.md](docs/deep-dive/sandbox.md) |
| 13 | Pod and process boundaries | [PNG](docs/diagrams/flagship/canonical-pod-process-boundaries.png) | [JSON](docs/diagrams/src/flagship/canonical-pod-process-boundaries.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-pod-process-boundaries.drawio) | [docs/deep-dive/sandbox-pod-execution.md](docs/deep-dive/sandbox-pod-execution.md) |
| 14 | AKS deployment | [PNG](docs/diagrams/flagship/canonical-aks-components.png) | [JSON](docs/diagrams/src/flagship/canonical-aks-components.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-aks-components.drawio) | [docs/guide/architecture-aks.md](docs/guide/architecture-aks.md) |
| 15 | Blueprint relationships | [PNG](docs/diagrams/flagship/canonical-blueprint-relationships.png) | [JSON](docs/diagrams/src/flagship/canonical-blueprint-relationships.json) | [draw.io](docs/diagrams/drawio/generated/flagship/canonical-blueprint-relationships.drawio) | [docs/guide/blueprints.md](docs/guide/blueprints.md)<br>[docs/catalog-groupings.md](docs/catalog-groupings.md) |

## Validation contract

- Fluent publication gate accepted every selected source before writing output.
- Raster readability is checked at a 960 px documentation embed.
- Actual Azure resources use official Azure SVGs.
- Actual Kubernetes resources use native Kubernetes symbols.
- Software concepts use the vendored Fluent IconCloud catalog.
- UML, sequence, activity, state, deployment, and flowchart notation are selected by concept.
