---
"Agentweaver.Abstractions": major
"Agentweaver.Providers": major
"Agentweaver.SourceControl": major
"Agentweaver.Orchestrator.Core": major
"Agentweaver.Persistence.Postgres": major
"Agentweaver.Secrets.AzureKeyVault": major
"Agentweaver.Identity": major
"Agentweaver.AgentRuntime": major
"Agentweaver.AgentHost": major
"Agentweaver.Telemetry": major
"Agentweaver.Telemetry.AzureMonitor": major
"Agentweaver.ObjectStore.AzureBlob": major
"Agentweaver.Identity.Broker": major
"Agentweaver.EventsAndSessions": major
"Agentweaver.Gateway": major
"Agentweaver.Mcp": major
"Agentweaver.Web": major
"Agentweaver.Orchestrator": major
"Agentweaver.Environment": major
"Agentweaver.Knowledge": major
"Agentweaver.Projects.Config": major
"Agentweaver.Providers.Storage.AzureFiles": major
"Agentweaver.Providers.Sandbox.AgentSandbox": major
---

Prepare the user-selected 1.0.0 version for all 23 P1 product components.
The canonical planner uses this major intent instead of the earlier minor
and patch intentions. This record does not change runtime code or APIs.
It retains the reviewed source and all existing dependency pins.
The manifest explicitly accepts 1.0.0 for its 53 direct dependency relationships.
The acceptance-only Foundation Probe keeps its separate pending version.
Product artifact preparation and publication use the same platform-only selection.
This preparation does not publish packages or images, apply database changes,
or deploy services.
