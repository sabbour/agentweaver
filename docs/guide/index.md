# Guides

Agentweaver v1 separates the browser and Gateway from services that own project,
run, identity, and journal state. These guides cover building the source and
validating those boundaries.

## Reading order

1. [Build and use](./build-and-use.md): requirements, locked restore, build, and
   foundation libraries.
2. [Experience overview](../experience/00-overview.md): the implemented user
   journeys and current browser navigation.
3. [Web client](./web-client.md): Broker authentication, Gateway integration, and
   frontend validation.
4. [Gateway](./gateway.md): finite API routing, current authorization, browser
   connection flows, MCP, and event streaming.
5. [Testing](./testing.md): existing test commands, owner-contract coverage, and
   documentation checks.
6. [Environment Sandbox testing](./environment-sandbox-testing.md): controlled
   provider, Workspace, AgentHost, and recovery checks.
7. [Azure acceptance](./azure-acceptance.md): deployment prerequisites and
   separately authorized environment checks.

Building the source does not deploy a platform. Follow the acceptance guide
only when you have authorization for the target environment.

## Find details

- [Experience guide](../experience/README.md): what users see and do.
- [HTTP API](../reference/api.md): route inventory by owning service.
- [Events](../reference/events.md): journal and outbox contracts.
- [Web reference](../reference/web.md): active source files and configuration.
- [MCP tools](../reference/mcp-tools.md): Gateway-derived tool surface.
- [Contracts and configuration](../reference/contracts.md): shared types,
  configuration, and endpoint semantics.
- [Architecture overview](../architecture/overview.md): component ownership.
- [Component releases](../reference/releases.md): validation and publication.
- [Diagram authoring](../diagrams/README.md): diagram source and validation.
