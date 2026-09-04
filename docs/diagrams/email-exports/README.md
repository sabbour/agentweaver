# Email-ready diagrams

## Overall system architecture

`email-architecture.png` shows the Agentweaver intent, control, and execution boundaries. It comes from `../00-system-overview-fig1.png`, which gives the clearest complete architecture without deployment-specific detail.

## System components

`email-components.png` shows the API host, middleware, endpoint modules, services, workers, stores, and external resources. It comes from `../canonical-api-host.png` and complements the architecture view with component responsibilities.

## Coordinator workflow

`email-coordinator-workflow.png` shows how the Coordinator confirms intent, stores a plan, dispatches child runs, assembles results, and completes review. It comes from `../canonical-coordinator-architecture.png`.
