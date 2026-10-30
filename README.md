# Agentweaver 1.x

[![v1 .NET coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fsabbour%2Fagentweaver%2Fbadges%2Fv1%2Fcoverage-dotnet.json)](https://github.com/sabbour/agentweaver/blob/badges/v1/coverage.md)
[![v1 web coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fsabbour%2Fagentweaver%2Fbadges%2Fv1%2Fcoverage-web.json)](https://github.com/sabbour/agentweaver/blob/badges/v1/coverage.md)
[![v1 Node coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fsabbour%2Fagentweaver%2Fbadges%2Fv1%2Fcoverage-node.json)](https://github.com/sabbour/agentweaver/blob/badges/v1/coverage.md)

Agentweaver 1.x is a rebuild of the Agentweaver platform on the `v1` branch.
It defines provider contracts and service boundaries for agent projects,
identity, Knowledge, run events, orchestration, environments, and user-facing
surfaces.

The repository contains implemented foundations and service candidates. A
component in source is not necessarily selectable, published, or deployed.
Follow the documentation's status notes before treating a route, provider, or
Azure resource as available.

## Learn Agentweaver

Start with the [1.x documentation](docs/index.md). The site is organized as:

- **Get started:** [build the source](docs/guide/quickstart.md) and understand
  the reusable foundation libraries.
- **Concepts:** review [what Agentweaver can do](docs/experience/00-overview.md),
  [the architecture](docs/architecture/overview.md), and
  [providers and models](docs/architecture/providers-models.md).
- **How-to:** [configure providers](docs/guide/configure-providers.md),
  [configure Knowledge memory](docs/guide/configure-knowledge.md), and
  [use the standalone CLI](docs/guide/cli.md) or
  [deploy to Azure](docs/guide/deploy-to-azure.md).
- **Reference:** find every
  [runtime configuration setting](docs/reference/runtime-configuration.md),
  API route, event, and release contract.
- **Troubleshooting:** diagnose source-grounded configuration and rollout
  failures in the [troubleshooting guide](docs/guide/troubleshooting.md).

## Build and validate the source

Use .NET 10 and Node.js 24 from the repository root:

```powershell
npm ci
dotnet restore Agentweaver.slnx --locked-mode
dotnet build Agentweaver.slnx --no-restore --configuration Release
npm run test:release
```

These commands build source and test the release/deployment workflow
contracts. They do not provision Azure, publish packages, or deploy
Kubernetes workloads. See the
[quickstart](docs/guide/quickstart.md) for local checks and the
[deployment guide](docs/guide/deploy-to-azure.md) for the separately approved
Azure workflow.

## Standalone CLI

The source includes a self-contained .NET CLI project, but downloadable CLI
archives are not yet available. The intended distributed executable includes
its .NET runtime and does not require Node.js, a .NET runtime, or a source
checkout. See the [CLI guide](docs/guide/cli.md) for the local publish command,
target prerequisites, sign-in, API use, and acceptance workflow. Local
publishing requires the .NET 10 SDK.

## Version boundary

For contributor planning, use [Plan issues and releases](docs/guide/release-planning.md).
Scheduled issues and pull requests use version milestones; labels describe
scope, triage and current work. Release evidence determines what shipped.

Agentweaver 0.x remains a separate product line on `dev`. Read the
[0.x documentation](https://sabbour.github.io/agentweaver/) for that product.
The v1 branch is a separate rebuild; it does not silently upgrade or replace
the 0.x deployment.
