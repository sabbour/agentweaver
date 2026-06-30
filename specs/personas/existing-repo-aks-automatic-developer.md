# Casey Morgan — Existing-Repo AKS Automatic Developer

## Identity & background

- Application developer who owns an existing repository and wants to deploy it to AKS Automatic without becoming the deployment expert.
- Understands the app code and local run commands, but the repo may lack Dockerfiles, manifests, Helm charts, ingress, or production config.
- Uses Agentweaver to assess readiness, fill deployment gaps, and ship the app safely.

## Domain

Existing application modernization, deployment readiness, containerization, Kubernetes manifests, AKS Automatic rollout.

## Goals & motivations

- Turn a working repository into a running AKS Automatic deployment with minimal manual orchestration.
- Have Agentweaver discover missing deployment assets instead of requiring Casey to know them upfront.
- Keep code changes, infrastructure changes, and cloud actions reviewable before applying them.

## What Casey wants from a multi-agent system

- A repo assessor, build engineer, containerization specialist, AKS Automatic deployer, configuration reviewer, and QA verifier.
- Clear distinction between what the app already has and what Agentweaver must add or change.
- Automated diagnosis when build, container, or rollout steps fail.

## Behavioral profile & decision patterns

- Starts from an existing repo URL or selected local project and asks Agentweaver to “figure out what is needed.”
- Opens generated diffs and deployment plans before approving cloud changes.
- High tolerance for technical details when they are tied to repo evidence; low tolerance for generic deployment advice.
- Reacts to failures by expecting Agentweaver to inspect logs, update assets, and retry a scoped step.
- Expects deployment verification to reflect the app’s own health endpoint, routes, or smoke-test commands when available.

## Agentweaver scenarios

### Repo readiness assessment

- **Trigger/goal:** Casey wants to know whether an existing web API repo can be deployed to AKS Automatic.
- **Team/agents:** Repo analyst, build detector, dependency reviewer, deployment-gap mapper, AKS Automatic specialist.
- **UI steps attempted:** Create or select a project connected to the repo; type “Assess this repo and tell me what is missing to deploy it to AKS Automatic”; review detected language, build command, ports, config needs, Dockerfile/manifests status, and risks.
- **Success looks like:** Agentweaver produces a repo-specific readiness report with discovered facts, missing assets, recommended deployment path, and questions that truly require Casey’s input.

### Fill deployment gaps and deploy

- **Trigger/goal:** The repo lacks complete deployment assets, and Casey wants Agentweaver to add what is necessary and deploy.
- **Team/agents:** Dockerfile author, Kubernetes/Helm author, config and secret reviewer, AKS Automatic deployer, smoke-test engineer.
- **UI steps attempted:** Approve the readiness plan; ask Agentweaver to generate or update Dockerfile, manifests/Helm, ingress, config, and deployment workflow; inspect the diff; approve deployment to AKS Automatic; monitor rollout.
- **Success looks like:** The repo gains reviewable deployment assets, the app image builds and deploys to AKS Automatic, rollout status is visible, and Agentweaver reports a working endpoint or explicit verification command.

### Failed rollout recovery

- **Trigger/goal:** The first AKS Automatic rollout fails because of an image, port, health probe, config, or permission issue.
- **Team/agents:** Rollout diagnostician, log analyst, app engineer, deployment fixer, QA verifier.
- **UI steps attempted:** Click into the failed run; ask “diagnose the failed rollout and retry only the needed fix”; inspect logs and proposed change; approve a targeted retry.
- **Success looks like:** Agentweaver identifies the failing layer, proposes a minimal fix, reruns the failed step, and updates the deployment evidence after recovery or clearly reports the remaining blocker.

## Failure signals to watch for

- Agentweaver gives generic AKS advice instead of inspecting the actual repository structure, build scripts, ports, and runtime configuration.
- The UI cannot show generated deployment diffs separately from app-code changes.
- Casey cannot tell which cloud action will run next or what will be changed in the cluster.
- Build, image push, manifest apply, and rollout failures collapse into a single opaque error.
- The system claims deployment success without checking the app-specific endpoint, health probe, or smoke test.
