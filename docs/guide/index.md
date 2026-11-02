<a id="how-to-guides-and-tutorials"></a>

# Guides and tutorials

Choose a guide by the task you need to complete. Some tasks change cloud or
Kubernetes resources. Each such guide states its access and approval needs
before its commands.

<a id="get-started"></a>

## Build and understand

- [Build and check the source](./quickstart.md): restore dependencies, build
  the solution, and run local source checks. This does not install or deploy
  Agentweaver.
- [Build and use the foundation libraries](./build-and-use.md): build the
  source and compose the reusable libraries.
- [Find CLI commands without signing in](./find-cli-commands.md): read local
  help from the Windows executable built from source.
- [Build and configure the web client](./web-client.md): configure and build
  the checked-in browser app.
- [Foundation architecture](../architecture/overview.md) and
  [system overview](../deep-dive/00-system-overview.md): understand component
  ownership and runtime behavior.

## Use the browser app

If your team has configured and deployed the web app, start with
[browser tasks](../experience/README.md). Those pages describe source
behavior; they do not confirm availability in a particular environment.

- [Chief of Staff](./chief-of-staff.md): manage personal conversations and
  Priorities.
- [Read and dismiss notifications](./notifications.md): review pending requests and
  dismiss a notification.
- [Read platform component status](./platform-components.md): interpret the
  owner-reported report.

<a id="tutorial"></a>

## Configure and deploy

- [Configure Knowledge memory](./configure-knowledge.md): configure the
  native PostgreSQL Memory provider.
- [Use the Gateway](./gateway.md): read the authorization boundary and call a
  supported API route.
- [Deploy Agentweaver to Azure](./deploy-to-azure.md): deploy to an approved
  target.
- [Run database maintenance](./database-maintenance.md): perform an approved
  database operation.
- [Verify an Azure deployment](./azure-acceptance.md): run approved,
  source-bound checks.
- [Test Environment Sandbox](./environment-sandbox-testing.md): verify the
  supported lifecycle contract.

<a id="test-and-accept"></a>

## Plan, diagnose, and reconcile

- [Plan issues and releases](./release-planning.md): choose a target
  milestone and update repository planning metadata.

<a id="troubleshooting"></a>

- [Troubleshoot Agentweaver](./troubleshooting.md):
  diagnose a symptom before you choose a remedy.

## Use the CLI and run tests

Use [Agentweaver CLI](./cli.md) for source publishing, target configuration,
sign-in, and API requests. Use [Testing](./testing.md) to choose a test
command and understand its limits.

<a id="find-reference-information"></a>

## Find reference information

- [Provider choices](./configure-providers.md)
- [Identity Broker permissions](./identity-scopes.md)
- [Runtime configuration](../reference/runtime-configuration.md)
- [HTTP API](../reference/api.md)
- [Events](../reference/events.md)
- [Contracts and endpoints](../reference/contracts.md)
- [Web integration](../reference/web.md)
- [Web UI parity](../reference/web-ui-parity.md)
- [AgentSuite import and export](../reference/agentsuite-import.md)
- [Component releases](../reference/releases.md)
- [Diagram authoring](../diagrams/README.md)

<a id="understand-the-version-boundary"></a>

For the difference between this rebuild and Agentweaver 0.x, see the
[documentation home](../index.md#understand-the-version-boundary).
