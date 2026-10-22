# How-to guides and tutorials

Use these guides and tutorials to build, configure, deploy, and validate Agentweaver 1.x.
Each guide names the source boundary and required target access. Building or
rendering the source does not deploy a platform.

## Get started

1. Follow [Quickstart: build the source](./quickstart.md) to restore the
   solution and run local checks.
2. Read [What Agentweaver 1.x can do](../experience/00-overview.md) before
   choosing a workflow or service.
3. Review [Foundation architecture](../architecture/overview.md) to find the
   service that owns each operation.

## Tutorial

- [Deploy Agentweaver to Azure](./deploy-to-azure.md): provision an approved
  target, bootstrap its database and identities, apply Kustomize, and verify
  the workloads.

## Configure and deploy

- [Configure providers](./configure-providers.md): review the providers that
  are registered in the current catalog and their limits.
- [Configure Knowledge memory](./configure-knowledge.md): configure native
  PostgreSQL Memory and preserve its store identity.
- [Gateway and BFF](./gateway.md): discover fixed routes and understand
  authentication and forwarding.
- [Web client](./web-client.md): configure the browser client and sign-in.

## Test and accept

- [Testing](./testing.md): run tests and interpret exactly what they prove.
- [Plan issues and releases](./release-planning.md): label work, choose
  version milestones and reconcile publication and acceptance evidence.
- [Environment Sandbox testing](./environment-sandbox-testing.md): run
  controlled lifecycle and provider tests.
- [Azure acceptance](./azure-acceptance.md): verify the deployed foundation
  with approved, bounded checks.
- [Troubleshoot Agentweaver 1.x](./troubleshooting.md): diagnose safe,
  source-grounded configuration and rollout failures.

## Find reference information

- [Runtime configuration](../reference/runtime-configuration.md)
- [HTTP API](../reference/api.md)
- [Events](../reference/events.md)
- [MCP tools](../reference/mcp-tools.md)
- [Contracts and endpoints](../reference/contracts.md)
- [Component releases](../reference/releases.md)
- [Diagram authoring](../diagrams/README.md)
