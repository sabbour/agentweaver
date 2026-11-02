<a id="agentweaver-1x"></a>

# Agentweaver

[![.NET coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fsabbour%2Fagentweaver%2Fbadges%2Fv1%2Fcoverage-dotnet.json)](https://github.com/sabbour/agentweaver/blob/badges/v1/coverage.md)
[![Web coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fsabbour%2Fagentweaver%2Fbadges%2Fv1%2Fcoverage-web.json)](https://github.com/sabbour/agentweaver/blob/badges/v1/coverage.md)
[![Node coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2Fsabbour%2Fagentweaver%2Fbadges%2Fv1%2Fcoverage-node.json)](https://github.com/sabbour/agentweaver/blob/badges/v1/coverage.md)

This repository contains the Agentweaver source, contracts, and service
implementations.
The source does not establish that a component is published or running in a
specific deployment.

<a id="learn-agentweaver"></a>

<a id="build-and-validate-the-source"></a>

## Start with the source

Follow the [quickstart](docs/guide/quickstart.md) to restore the dependencies,
build the source, and run local checks. You need the .NET 10 SDK and Node.js
24. A successful build and local test run do not install or start the
Agentweaver platform, publish packages, or deploy Kubernetes workloads.

The [documentation home](docs/index.md) helps you choose a next task. If you
already have access to a configured Agentweaver web app, use the
[browser task guides](docs/experience/README.md). Those guides describe the
checked-in web implementation; they do not confirm that its services are
available in your environment.

<a id="standalone-cli"></a>

## Choose a task

- [Build and use the foundation libraries](docs/guide/build-and-use.md) from
  the source tree.
- [Review provider choices and limits](docs/guide/configure-providers.md), or
  [Knowledge memory](docs/guide/configure-knowledge.md) for a reviewed
  deployment.
- [Find local CLI commands](docs/guide/find-cli-commands.md); see the
  [CLI guide](docs/guide/cli.md) for its source build and use.
- [Deploy to Azure](docs/guide/deploy-to-azure.md) only when you have an
  approved target and the required access.
- Read the [architecture overview](docs/architecture/overview.md) and
  [system overview](docs/deep-dive/00-system-overview.md) to understand the
  source boundaries and runtime.
- Use the [release runbook](releases/README.md) for package publication,
  container image publication, and workload deployment. These are separate
  operations.

## Version boundary

Agentweaver 0.x remains a separate product line on `dev`. Read the
[0.x documentation](https://sabbour.github.io/agentweaver/) for that product.
This source is a separate rebuild; it does not silently upgrade or replace the
0.x deployment.

For contributor planning, see [Plan issues and releases](docs/guide/release-planning.md).
An issue milestone describes a target. It does not show that a change shipped
or is deployed.

## Contribute documentation

Use the [documentation authoring guide](docs/authoring/README.md) to choose
the right article type and check claims against source. Its templates are
prompts; remove sections that do not help the reader.
