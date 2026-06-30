# Jordan Lee — Greenfield AKS Automatic Developer

## Identity & background

- Full-stack developer starting a new product idea and trying to get it running on AKS Automatic quickly.
- Comfortable with code, GitHub, containers, and cloud basics, but not an AKS specialist.
- Uses Agentweaver to orchestrate app scaffolding, infrastructure decisions, deployment, and verification with as little hand-holding as possible.

## Domain

Greenfield application development, containerization, Kubernetes deployment, AKS Automatic adoption, developer experience.

## Goals & motivations

- Go from blank idea to a running app on AKS Automatic in one guided flow.
- Avoid learning every Kubernetes, ingress, identity, and deployment detail before seeing value.
- Trust Agentweaver to coordinate specialists while keeping generated code and cloud changes reviewable.

## What Jordan wants from a multi-agent system

- A product engineer, frontend engineer, backend engineer, DevOps engineer, AKS Automatic specialist, security reviewer, and QA verifier working together.
- Clear prompts for only the decisions Jordan truly must make, such as app purpose, cloud subscription, region, and public/private exposure.
- A visible path from idea to scaffolded repo, container image, AKS Automatic deployment, and live smoke test.

## Behavioral profile & decision patterns

- Starts by typing a plain-language product idea into the console or project board and expects Agentweaver to infer the needed team.
- Clicks quick-start paths, generated plans, and “deploy” affordances before reading long documentation.
- Moderate tolerance for generated defaults; low tolerance for dead ends that require Kubernetes expertise.
- Reacts to errors by asking Agentweaver to diagnose and fix rather than manually editing YAML first.
- Expects every cloud action to be previewed, every credential problem to be explained, and every successful deployment to include a URL or concrete verification command.

## Agentweaver scenarios

### Blank idea to AKS Automatic

- **Trigger/goal:** Jordan wants to create a new task-tracking web app and deploy it to AKS Automatic with minimal guidance.
- **Team/agents:** Product planner, app scaffolder, frontend engineer, backend engineer, containerization engineer, AKS Automatic deployer, QA smoke tester.
- **UI steps attempted:** Create a new Agentweaver project; type “Build a simple multi-user task tracker with a web UI and API, then deploy it to AKS Automatic”; accept sensible defaults; review the generated plan; start the run; monitor the board and live console; approve generated code, Dockerfile, manifests or Helm chart, and deployment steps.
- **Success looks like:** Agentweaver creates a working app, prepares container/deployment assets, deploys to an AKS Automatic cluster, shows the deployed endpoint, and records a passing smoke test against the running app.

### Minimal-guidance deployment setup

- **Trigger/goal:** Jordan has an Azure subscription but does not know which AKS Automatic prerequisites matter.
- **Team/agents:** Cloud readiness checker, AKS Automatic specialist, security reviewer, deployment planner.
- **UI steps attempted:** Ask in the console, “Check whether my environment is ready for AKS Automatic and fix or tell me only the blockers”; review required inputs; let Agentweaver validate subscription, region, cluster access, registry, and identity assumptions.
- **Success looks like:** Agentweaver distinguishes ready checks from blockers, asks only for missing human-provided values, and produces a deployment plan with no unexplained Kubernetes jargon.

### Post-deploy iteration

- **Trigger/goal:** Jordan wants to add a small feature after the first successful AKS Automatic deployment.
- **Team/agents:** Feature engineer, regression tester, deployment updater, release scribe.
- **UI steps attempted:** Type a feature request into the run console; ask Agentweaver to update the app and redeploy; inspect changed files and rollout status; verify the public endpoint still works.
- **Success looks like:** The app update is implemented, reviewed, redeployed to AKS Automatic, and verified without losing the original endpoint or deployment history.

## Failure signals to watch for

- Agentweaver assumes Jordan already knows Kubernetes resource types, ingress setup, registry configuration, or AKS Automatic constraints.
- The UI cannot move from product idea to coordinated app, container, and deployment work in one traceable flow.
- Deployment progress is hidden, stale, or missing enough detail to know whether the app is building, pushing, applying, rolling out, or smoke testing.
- Cloud/auth failures are generic and do not identify the exact missing permission, subscription, cluster, registry, or login step.
- “Success” is declared before a reachable endpoint or concrete smoke-test evidence is shown.
