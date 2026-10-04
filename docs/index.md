# Agentweaver 1.x documentation

Agentweaver 1.x is a rebuild on the `v1` branch. The current source contains provider contracts, foundation libraries, an Identity broker candidate, and Azure infrastructure definitions.

The foundation source does not provide a deployed product platform. It does not publish packages or images. The dedicated Azure environment has not been provisioned.

This site documents code and procedures that exist in the `v1` source. Proposed architecture records stay in the repository and do not describe implemented capabilities.

## Start

| Page | Use |
| --- | --- |
| [Build and use](./guide/build-and-use) | Restore, build, test, and compose foundation libraries. |
| [Testing](./guide/testing) | Run current tests and read their limits. |
| [Azure acceptance](./guide/azure-acceptance) | Compile infrastructure and prepare an approved acceptance run. |

## Architecture

| Page | Covers |
| --- | --- |
| [Foundation overview](./architecture/overview) | Compiled components and their dependencies. |
| [Providers and models](./architecture/providers-models) | Provider resolution, pinning, and model support. |
| [Identity and secrets](./architecture/identity-secrets) | OAuth, run grants, Key Vault, and workload identity. |
| [PostgreSQL and Blob](./architecture/persistence-objects) | Outbox, inbox, relay, and object storage. |
| [Telemetry](./architecture/telemetry) | OpenTelemetry and Azure Monitor adapters. |
| [Dedicated Azure environment](./architecture/azure) | Bicep, AKS, private dependencies, and the Foundation Probe. |

## Reference

| Page | Covers |
| --- | --- |
| [Contracts and endpoints](./reference/contracts) | Provider and secret contracts, Identity routes, and host configuration. |
| [Component releases](./reference/releases) | Draft manifest, changesets, and release commands. |
| [Diagram authoring](./diagrams/README) | Editable sources, rendered figures, and validation commands. |

## Version boundary

Agentweaver 0.x remains an active product line on `dev`. Read the [0.x documentation](https://sabbour.github.io/agentweaver/) for that product.

The v1 source describes a separate foundation. The [proposed architecture record](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0001-platform-architecture.md) is not an implementation status report.
