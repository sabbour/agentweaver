# Agentweaver 1.x

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
npm run release:validate
```

These commands validate and build source. They do not provision Azure,
publish a release, or deploy Kubernetes workloads. See the
[quickstart](docs/guide/quickstart.md) for local checks and the
[deployment guide](docs/guide/deploy-to-azure.md) for the separately approved
Azure workflow.

## Version boundary

Agentweaver 0.x remains a separate product line on `dev`. Read the
[0.x documentation](https://sabbour.github.io/agentweaver/) for that product.
The v1 branch is a separate rebuild; it does not silently upgrade or replace
the 0.x deployment.
