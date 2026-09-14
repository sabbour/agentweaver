# Email-ready diagrams

## Overall system architecture

[email-architecture.png](./email-architecture.png) separates Entra user identity, purpose-bound GitHub capabilities,
API/worker control, MCP forwarding, AgentHost execution and PostgreSQL persistence.
Storage and constrained execution credentials are qualified in the diagram rather than
presented as a credential-free sandbox. This is a repository-grounded logical deployment
view, not a live cluster capture. It comes from the [canonical architecture PNG](../email-architecture.png).

## System components

[email-components.png](./email-components.png) maps the actual applications, API modules, AgentHost processes, shared packages, and external dependencies. The labels follow current project folders and package references. It comes from the [canonical component PNG](../email-components.png).

## Coordinator workflow

[email-coordinator-workflow.png](./email-coordinator-workflow.png) is a sequence diagram of the Coordinator lifecycle.
It distinguishes retryable assembly blockage from terminal failure or decline:
decline skips model-backed Scribe, while a merge failure may still be recorded by Scribe.
The inspected collective path does not establish automatic PR publication. It comes from
the [canonical coordinator sequence PNG](../email-coordinator-workflow.png).

All three exports now have editable uncompressed draw.io sources in `../src/` and
pitch/pass records in `../reviews/`. The system and component views use grouped
native-symbol compositions, not separate JSON graph-spec sources. Export with pinned
draw.io Desktop 31.4.5; the copies here must match their canonical PNGs.

<!-- diagram-context:email-architecture:start -->
<details id="diagram-context-email-architecture" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Identity, control, and execution</td></tr>
<tr><td>subtitle</td><td>Entra authenticates people; GitHub capabilities authorize purpose-bound repository access.</td></tr>
<tr><td>group-title0</td><td>AKS · control and execution workloads</td></tr>
<tr><td>group-title1</td><td>External services</td></tr>
<tr><td>API + worker</td><td>API + worker</td></tr>
<tr><td>API + worker</td><td>Control plane services</td></tr>
<tr><td>API + worker</td><td>Postgres + CSI mounted data</td></tr>
<tr><td>MCP service</td><td>MCP service</td></tr>
<tr><td>MCP service</td><td>Authenticated tool surface</td></tr>
<tr><td>MCP service</td><td>HTTP API client</td></tr>
<tr><td>AgentHost pod</td><td>AgentHost pod</td></tr>
<tr><td>AgentHost pod</td><td>Constrained turn execution</td></tr>
<tr><td>AgentHost pod</td><td>per-run bearer / sandbox</td></tr>
<tr><td>Microsoft Entra</td><td>Microsoft Entra</td></tr>
<tr><td>Microsoft Entra</td><td>User identity</td></tr>
<tr><td>Microsoft Entra</td><td>sign-in / sessions / roles</td></tr>
<tr><td>GitHub</td><td>GitHub</td></tr>
<tr><td>GitHub</td><td>Purpose-bound capabilities</td></tr>
<tr><td>GitHub</td><td>repository authorization</td></tr>
<tr><td>PostgreSQL</td><td>PostgreSQL</td></tr>
<tr><td>PostgreSQL</td><td>Shared durable state</td></tr>
<tr><td>PostgreSQL</td><td>API + worker persistence</td></tr>
<tr><td>e1</td><td>HTTP</td></tr>
<tr><td>e2</td><td>configure / A2A</td></tr>
<tr><td>e3</td><td>identity</td></tr>
<tr><td>e4</td><td>capability</td></tr>
<tr><td>e5</td><td>read / write</td></tr>
<tr><td>assurance-title</td><td>TRUST FLOWS ARE DELIBERATELY DISTINCT</td></tr>
<tr><td>assurance-line1</td><td>GitHub authorization constrains repository credentials used during execution; Entra establishes user identity.</td></tr>
<tr><td>assurance-line2</td><td>API and worker use PostgreSQL and CSI-mounted storage. MCP is an API client, not another database owner.</td></tr>
<tr><td>API + worker</td><td>Services</td></tr>
<tr><td>API + worker</td><td>Data</td></tr>
<tr><td>API + worker</td><td>Postgres persistence</td></tr>
<tr><td>API + worker</td><td>Files</td></tr>
<tr><td>API + worker</td><td>CSI-mounted storage</td></tr>
<tr><td>API + worker</td><td>Control</td></tr>
<tr><td>API + worker</td><td>Configure + A2A</td></tr>
<tr><td>MCP service</td><td>Surface</td></tr>
<tr><td>MCP service</td><td>Authenticated tools</td></tr>
<tr><td>MCP service</td><td>Credential</td></tr>
<tr><td>MCP service</td><td>Validated broker token</td></tr>
<tr><td>MCP service</td><td>Downstream</td></tr>
<tr><td>MCP service</td><td>HTTP API</td></tr>
<tr><td>MCP service</td><td>Storage</td></tr>
<tr><td>MCP service</td><td>No database connector</td></tr>
<tr><td>AgentHost pod</td><td>Host</td></tr>
<tr><td>AgentHost pod</td><td>AgentHost workload</td></tr>
<tr><td>AgentHost pod</td><td>Transport</td></tr>
<tr><td>AgentHost pod</td><td>Authenticated A2A</td></tr>
<tr><td>AgentHost pod</td><td>Constrained repo access</td></tr>
<tr><td>AgentHost pod</td><td>Scope</td></tr>
<tr><td>AgentHost pod</td><td>Bound to execution</td></tr>
<tr><td>Microsoft Entra</td><td>Role</td></tr>
<tr><td>Microsoft Entra</td><td>User authentication</td></tr>
<tr><td>Microsoft Entra</td><td>Methods</td></tr>
<tr><td>Microsoft Entra</td><td>Sign-in / sessions</td></tr>
<tr><td>Microsoft Entra</td><td>Access</td></tr>
<tr><td>Microsoft Entra</td><td>Role authorization</td></tr>
<tr><td>Microsoft Entra</td><td>Not</td></tr>
<tr><td>Microsoft Entra</td><td>Repository authority</td></tr>
<tr><td>GitHub</td><td>Repository capability</td></tr>
<tr><td>GitHub</td><td>Grant</td></tr>
<tr><td>GitHub</td><td>Purpose-bound access</td></tr>
<tr><td>GitHub</td><td>Delivery</td></tr>
<tr><td>GitHub</td><td>Constrained credentials</td></tr>
<tr><td>GitHub</td><td>Product sign-in</td></tr>
<tr><td>PostgreSQL</td><td>Durable persistence</td></tr>
<tr><td>PostgreSQL</td><td>Writers</td></tr>
<tr><td>PostgreSQL</td><td>Stores</td></tr>
<tr><td>PostgreSQL</td><td>Runs / events / projects</td></tr>
<tr><td>PostgreSQL</td><td>Separate</td></tr>
<tr><td>PostgreSQL</td><td>CSI-mounted files</td></tr>
<tr><td>api</td><td>API hosts execution orchestration; MCP calls API; no MCP database</td></tr>
<tr><td>mcp</td><td>Broker-token validation; No database connector from MCP</td></tr>
<tr><td>sandbox</td><td>Worker configures and streams A2A; Repository credential flow exists</td></tr>
<tr><td>entra</td><td>Authentication is not repo authority; Distinct from GitHub capability grants</td></tr>
<tr><td>github</td><td>Constrained credentials can reach execution; Not a credential-free sandbox</td></tr>
<tr><td>postgres</td><td>Run state / events / project data; CSI storage serves mounted files</td></tr>
<tr><td>notes</td><td>TRUST FLOWS ARE DELIBERATELY DISTINCT; GitHub authorization constrains repository credentials used during execution; Entra establishes user identity.; API and worker use PostgreSQL and CSI-mounted storage. MCP is an API client, not another database owner.</td></tr>
<tr><td>groups</td><td>AKS · control and execution workloads; External identity, capabilities, and durable data</td></tr>
</tbody></table>
</details>
<!-- diagram-context:email-architecture:end -->

<!-- diagram-context:email-components:start -->
<details id="diagram-context-email-components" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Components and project references</td></tr>
<tr><td>subtitle</td><td>Arrows mean compile-time ProjectReference—not HTTP calls or deployment routing.</td></tr>
<tr><td>group-title0</td><td>Executable projects</td></tr>
<tr><td>group-title1</td><td>Shared packages</td></tr>
<tr><td>Agentweaver.Api</td><td>Api</td></tr>
<tr><td>Agentweaver.Api</td><td>Control plane + workers</td></tr>
<tr><td>Agentweaver.Api</td><td>apps/Agentweaver.Api</td></tr>
<tr><td>Agentweaver.AgentHost</td><td>AgentHost</td></tr>
<tr><td>Agentweaver.AgentHost</td><td>Sandbox turn host</td></tr>
<tr><td>Agentweaver.AgentHost</td><td>apps/Agentweaver.AgentHost</td></tr>
<tr><td>Agentweaver.Mcp</td><td>Mcp</td></tr>
<tr><td>Agentweaver.Mcp</td><td>Remote tool surface</td></tr>
<tr><td>Agentweaver.Mcp</td><td>apps/Agentweaver.Mcp</td></tr>
<tr><td>AgentRuntime</td><td>AgentRuntime</td></tr>
<tr><td>AgentRuntime</td><td>Agent workflow execution</td></tr>
<tr><td>AgentRuntime</td><td>packages/Agentweaver.AgentRuntime</td></tr>
<tr><td>AgentTools</td><td>AgentTools</td></tr>
<tr><td>AgentTools</td><td>Agent-facing tool adapters</td></tr>
<tr><td>AgentTools</td><td>packages/Agentweaver.AgentTools</td></tr>
<tr><td>Domain</td><td>Domain</td></tr>
<tr><td>Domain</td><td>Shared domain contracts</td></tr>
<tr><td>Domain</td><td>packages/Agentweaver.Domain</td></tr>
<tr><td>e1</td><td>ref.</td></tr>
<tr><td>assurance-title</td><td>SELECTED DEPENDENCIES, NOT AN EXHAUSTIVE BUILD GRAPH</td></tr>
<tr><td>assurance-line1</td><td>UML component symbols retain the component identity; omitted direct references are preserved in these details.</td></tr>
<tr><td>assurance-line2</td><td>The web SPA / Web host, Api.Data, migrations, and Squad remain separate projects; no network inference.</td></tr>
<tr><td>Agentweaver.Api</td><td>Kind</td></tr>
<tr><td>Agentweaver.Api</td><td>Executable project</td></tr>
<tr><td>Agentweaver.Api</td><td>Uses</td></tr>
<tr><td>Agentweaver.Api</td><td>Data</td></tr>
<tr><td>Agentweaver.Api</td><td>Api.Data</td></tr>
<tr><td>Agentweaver.Api</td><td>Catalog</td></tr>
<tr><td>Agentweaver.Api</td><td>Squad</td></tr>
<tr><td>Agentweaver.AgentHost</td><td>Also</td></tr>
<tr><td>Agentweaver.AgentHost</td><td>Role</td></tr>
<tr><td>Agentweaver.Mcp</td><td>Calls</td></tr>
<tr><td>Agentweaver.Mcp</td><td>HTTP API client</td></tr>
<tr><td>Agentweaver.Mcp</td><td>Graph</td></tr>
<tr><td>Agentweaver.Mcp</td><td>No selected refs</td></tr>
<tr><td>Agentweaver.Mcp</td><td>Not a database layer</td></tr>
<tr><td>AgentRuntime</td><td>Contracts</td></tr>
<tr><td>AgentRuntime</td><td>Exec</td></tr>
<tr><td>AgentRuntime</td><td>SandboxExec</td></tr>
<tr><td>AgentRuntime</td><td>Files</td></tr>
<tr><td>AgentRuntime</td><td>SandboxFs</td></tr>
<tr><td>AgentTools</td><td>Tool adapters</td></tr>
<tr><td>Domain</td><td>Shared contracts</td></tr>
<tr><td>Domain</td><td>Used by</td></tr>
<tr><td>Domain</td><td>AgentTools / AgentHost</td></tr>
<tr><td>Domain</td><td>Arrow</td></tr>
<tr><td>Domain</td><td>Toward dependency</td></tr>
<tr><td>api</td><td>References AgentRuntime; References Api.Data and Squad</td></tr>
<tr><td>host</td><td>References AgentRuntime; Also directly references Domain</td></tr>
<tr><td>mcp</td><td>No project-reference arrows here; API client—not a database layer</td></tr>
<tr><td>runtime</td><td>References AgentTools; Also Domain / SandboxExec / SandboxFs</td></tr>
<tr><td>tools</td><td>References Domain; References SandboxExec / SandboxFs</td></tr>
<tr><td>domain</td><td>Referenced by runtime and tools; Dependency direction is toward Domain</td></tr>
<tr><td>notes</td><td>SELECTED DEPENDENCIES, NOT AN EXHAUSTIVE BUILD GRAPH; UML component symbols retain the component identity; omitted direct references are listed inside cards.; The web SPA / Web host, Api.Data, migrations, and Squad remain separate projects; no network inference.</td></tr>
<tr><td>groups</td><td>Executable projects; Shared package dependencies · selected edges</td></tr>
</tbody></table>
</details>
<!-- diagram-context:email-components:end -->

<!-- diagram-context:email-coordinator-workflow:start -->
<details id="diagram-context-email-coordinator-workflow" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>notes</td><td>APPROVED PATH · alternatives remain explicit below; Changes may redispatch work. Decline skips Scribe. Blocked assembly is recoverable—not terminal.; Merge failure may still run Scribe. No automatic PR publication is claimed by this collective workflow.</td></tr>
</tbody></table>
</details>
<!-- diagram-context:email-coordinator-workflow:end -->
