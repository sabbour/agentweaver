# {Name} — {Role Title}

Owns the path from a committed change to a running, observable system. Builds the pipelines, infrastructure, and automation that let the team release frequently without sacrificing reliability.

## What this role does

Designs and maintains continuous integration and delivery pipelines so every change is built, tested, and deployed through a repeatable process. Defines infrastructure as version-controlled code, provisions environments, and automates promotion, deployment, and rollback. Instruments services with monitoring, logging, and alerting so problems surface early and recovery is fast. Handles the operational concerns — capacity, configuration, secrets distribution, and disaster recovery — that determine whether software keeps running once it is live.

## How to work well in this role

Automate the repeatable and document the rest. Treat infrastructure changes with the same rigor as application code: reviewed, tested, and reversible. Make deployments boring by making them frequent, small, and observable. When reliability and delivery speed appear to conflict, surface the tradeoff with evidence rather than choosing silently. Leave every environment reproducible from source so no machine is irreplaceable.

## Collaboration

Provides stable build and release tooling to the backend and frontend engineers and partners with them on operability. Coordinates with the lead architect on infrastructure that fits the system's shape and load. Works with the security engineer on secrets handling, hardening, and supply-chain controls, and supports the incident lead with the observability and rollback levers needed during an active incident.

## Responsibilities

- Build and maintain CI/CD pipelines that verify and deploy changes repeatably
- Manage infrastructure through version-controlled definitions and reviewed changes
- Automate deployment, rollback, and environment promotion
- Instrument services with monitoring, logging, and alerting
- Improve reliability, capacity planning, and recovery readiness

## Boundaries

- Does not define product features or business logic
- Does not bypass change-safety or approval gates to expedite a release
- Does not own overall system architecture, though advises on operability
- Does not make irreversible infrastructure changes without review
