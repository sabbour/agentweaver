# Agentweaver 1.x documentation

Learn how to build, configure, and validate the Agentweaver 1.x source. These
articles describe the current `v1` branch. They distinguish implemented
contracts from source candidates and do not claim that a service, provider, or
Azure environment is deployed.

## Get started

| Article | Use it to |
| --- | --- |
| [Quickstart: build the source](./guide/quickstart.md) | Restore and build the v1 solution and run local checks. |
| [Build and use the foundation libraries](./guide/build-and-use.md) | Understand and compose the reusable foundation libraries. |

## Tutorials

| Article | Use it to |
| --- | --- |
| [Tutorial: deploy Agentweaver to Azure](./guide/deploy-to-azure.md) | Provision an approved target, bootstrap its database and identities, install the Kubernetes bundle, and verify the workloads. |

## Concepts

| Article | Learn about |
| --- | --- |
| [What Agentweaver 1.x can do](./experience/00-overview.md) | Current product capabilities and source boundaries. |
| [Foundation architecture](./architecture/overview.md) | Component ownership and dependencies. |
| [Providers and models](./architecture/providers-models.md) | Provider registration, resolution, model bindings, and limits. |
| [Knowledge and Memory](./architecture/knowledge-memory.md) | Project memory, decisions, revisions, and provider binding. |
| [Identity and secrets](./architecture/identity-secrets.md) | Broker identity, workload identity, grants, and Key Vault. |

## How-to guides

| Article | Use it to |
| --- | --- |
| [Configure providers](./guide/configure-providers.md) | Choose from the providers registered in the current catalog. |
| [Configure Knowledge memory](./guide/configure-knowledge.md) | Configure native PostgreSQL Memory and understand store identity. |
| [Gateway and BFF](./guide/gateway.md) | Discover and call the finite Gateway API. |
| [Web client](./guide/web-client.md) | Configure the browser client and its authentication flow. |
| [Identity Broker scopes](./guide/identity-scopes.md) | Understand the permissions shown on the Broker consent screen. |
| [Testing](./guide/testing.md) | Run the relevant test suites and interpret their limits. |
| [Azure acceptance](./guide/azure-acceptance.md) | Prepare and verify approved Azure acceptance checks. |

## Reference

| Article | Use it to |
| --- | --- |
| [Runtime configuration](./reference/runtime-configuration.md) | Find runtime keys, defaults, validation rules, and value sources. |
| [Contracts and endpoints](./reference/contracts.md) | Read shared contracts and service endpoint behavior. |
| [HTTP API](./reference/api.md) | Find the versioned API routes and owners. |
| [Events](./reference/events.md) | Read journal event and outbox contracts. |
| [MCP tools](./reference/mcp-tools.md) | Discover the Gateway-derived MCP tool surface. |
| [Component releases](./reference/releases.md) | Understand component versioning, publication, and deployment. |

## Troubleshooting

Use [Troubleshoot Agentweaver 1.x](./guide/troubleshooting.md) to diagnose
provider resolution, authentication, configuration, and rollout failures.

## Understand the version boundary

Agentweaver 0.x remains a separate product line on `dev`. Read the
[0.x documentation](https://sabbour.github.io/agentweaver/) for that product.
The v1 source is a separate rebuild. Proposed architecture and source-only
service candidates do not imply a released or deployed capability.
