# Email-ready diagrams

## Overall system architecture

`email-architecture.png` shows the Agentweaver intent, control, and execution boundaries. Four bands separate the Client, Control plane, Execution plane, and External systems. It comes from `../00-system-overview-fig1.png`, which gives the clearest complete architecture without deployment-specific detail.

## System components

`email-components.png` shows the API host, middleware, endpoint modules, services, workers, stores, and external resources. Four bands separate the Client, Control plane, Execution plane, and External systems. It comes from `../canonical-api-host.png` and complements the architecture view with component responsibilities.

## Coordinator workflow

`email-coordinator-workflow.png` is a sequence diagram of the real Coordinator lifecycle. It shows outcome confirmation, durable planning, dependency-aware child dispatch, result observation, collective assembly, review, merge, Scribe, and decision promotion. It comes from `../email-coordinator-workflow.png`. The other two exports use the grouped graph-spec style.
