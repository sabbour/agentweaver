# Email-ready diagrams

## Overall system architecture

`email-architecture.png` shows the deployed system from clients and identity through the authoritative API and isolated AKS execution plane. It also shows PostgreSQL, Key Vault, GitHub, and model providers. It comes from `../email-architecture.png`.

## System components

`email-components.png` maps the actual applications, API modules, AgentHost processes, shared packages, and external dependencies. The labels follow current project folders and package references. It comes from `../email-components.png`.

## Coordinator workflow

`email-coordinator-workflow.png` is a sequence diagram of the real Coordinator lifecycle. It shows outcome confirmation, durable planning, dependency-aware child dispatch, result observation, collective assembly, review, merge, Scribe, and decision promotion. It comes from `../email-coordinator-workflow.png`. The other two exports use the grouped graph-spec style.
