<a id="agentweaver-1-x-documentation"></a>

# Agentweaver documentation

Choose a guide by what you want to do. These pages describe the checked-in
source. A source build or route definition does not show that a package
was published or that a service is running in your environment.

<a id="get-started"></a>

## Build the source

Use [Quickstart: build and check the source](./guide/quickstart.md) to restore
the locked dependencies, build the solution, and run local checks. The result
is a source build, not an installed or deployed platform.

## Use the browser app

If you have access to a configured Agentweaver web app, start with
[the browser experience](./experience/README.md). It describes the checked-in
web implementation and links to tasks for sign-in, projects, settings, runs,
connections, Skills, and Knowledge. It does not confirm that a web app or its
owner services are deployed.

<a id="concepts"></a>

## Learn how Agentweaver works

| Start here | Learn about |
| --- | --- |
| [What the web app can do](./experience/00-overview.md) | Browser tasks, routes, access, and source boundaries. |
| [Foundation architecture](./architecture/overview.md) | Component ownership and dependencies. |
| [System overview](./deep-dive/00-system-overview.md) | System context and runtime views. |
| [Providers and models](./architecture/providers-models.md) | Provider registration, model bindings, and limits. |
| [Knowledge and Memory](./architecture/knowledge-memory.md) | Memory records, revisions, and provider bindings. |
| [Identity and secrets](./architecture/identity-secrets.md) | Broker identity, workload identity, grants, and Key Vault. |

<a id="how-to-guides"></a>

## Complete a task

| Task | Guide |
| --- | --- |
| Configure native PostgreSQL Memory | [Configure Knowledge memory](./guide/configure-knowledge.md) |
| Call a Gateway API route | [Use the Gateway](./guide/gateway.md) |
| Configure or build the web client | [Build and configure the web client](./guide/web-client.md) |
| Read and dismiss notifications | [Read notifications](./guide/notifications.md) |
| Read platform component status | [Read the platform components report](./guide/platform-components.md) |
| Find local CLI commands | [Find CLI commands](./guide/find-cli-commands.md) |
| Plan a release | [Plan issues and releases](./guide/release-planning.md) |
| Deploy to an approved Azure target | [Deploy Agentweaver to Azure](./guide/deploy-to-azure.md) |

See the [guide index](./guide/index.md) for tutorials, operations, and
troubleshooting.

<a id="reference"></a>

## Find reference information

| Reference | Use it to |
| --- | --- |
| [Provider choices](./guide/configure-providers.md) | Find registered provider choices, source candidates, and configuration boundaries. |
| [Identity Broker permissions](./guide/identity-scopes.md) | Understand what each permission allows and what it does not authorize. |
| [Runtime configuration](./reference/runtime-configuration.md) | Find setting names, defaults, validation, and value sources. |
| [Contracts and endpoints](./reference/contracts.md) | Read shared contracts and endpoint behavior. |
| [HTTP API](./reference/api.md) | Find versioned routes, owners, and request contracts. |
| [Events](./reference/events.md) | Read journal event, ordering, and outbox contracts. |
| [Web integration](./reference/web.md) | Find browser routes, configuration, and auth integration. |
| [Web UI parity](./reference/web-ui-parity.md) | Check the implemented browser surface and its limits. |
| [AgentSuite import and export](./reference/agentsuite-import.md) | Read the team archive contract. |
| [Component releases](./reference/releases.md) | Understand component versioning, publication, and deployment. |

<a id="tutorials"></a>

## Deploy and operate

Deployment changes resources. Use the
[Azure deployment guide](./guide/deploy-to-azure.md) only for an approved
target. The [release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md)
keeps package publication, image publication, and workload deployment separate.

<a id="troubleshooting"></a>

For a source-grounded diagnosis, use
[Troubleshoot Agentweaver](./guide/troubleshooting.md). For approved
source-bound Azure checks, see
[Azure acceptance](./guide/azure-acceptance.md).

## Understand the version boundary

Agentweaver 0.x remains a separate product line on `dev`. Read the
[0.x documentation](https://sabbour.github.io/agentweaver/) for that product.
This source is a separate rebuild; it does not silently upgrade or replace the
0.x deployment.
