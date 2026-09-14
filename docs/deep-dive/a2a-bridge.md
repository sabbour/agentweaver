# A2A bridge

## Purpose

Agentweaver keeps the workflow graph, graph-level approval gates, checkpoints, and durable persistence in the orchestration host. It sends each leaf agent turn to AgentHost over A2A HTTP+JSON. Structured run events cross that boundary and are persisted by the caller's event pipeline; pod-local tool approval waits have a separate return path.

`RemoteAgentProxy` implements the workflow agent surface on the worker. It forwards a turn, receives streamed output and run events, and re-emits them to the local runtime.

AgentHost hosts an `A2ATurnBridgeAgent` around the provider-backed runtime. It exposes `POST /a2a/agent/v1/message:stream` and `GET /a2a/agent/v1/card` on port `8088` by default.

## Workflow agents

`RemoteWorkflowAgentFactory` returns `RemoteAgentProxy` for five workflow agents:

- worker
- RAI
- Rubberduck
- Build/Test
- Scribe

The Operator Assistant also uses `RemoteAgentProxy`, outside `IWorkflowAgentFactory`.

## Turn and event transport

The worker sends turn setup as the first A2A `DataContent` part. This setup contains workspace and repository context, model and system-prompt data, project and agent identity, and revision state.

AgentHost sends assistant updates and structured `RunEvent` values through the same stream. `RunEventDataPartCodec` serializes the run events, which the worker appends to its local event pipeline.

See the [canonical A2A sequence](../diagrams/canonical-agent-communication-a2a.png). Transport EOF alone is not success: the proxy checks the definitive turn-end marker and structured failures.

## Warm-pool configuration

AgentHost starts in standby without a run identity. When the API claims a warm pod, it makes one `POST /configure` request. The request includes run, project, agent, and purpose identity; shared and local workspace descriptors; turn authentication; approval settings; and provider configuration.

The provider payload is either `copilotCredential` or `byokProviderConfiguration`. Repository, preview, and MCP broker credentials are optional and purpose-scoped.

The [claim/configure sequence](../diagrams/sandbox-pod-execution-fig6.png) distinguishes listener liveness from configured readiness. `/healthz` returns HTTP 200 with `standby` before configuration and `ready` afterward. `/configure` accepts one configuration per pod; a second attempt returns 409. Other nonexempt requests return 503 before setup completes.

Production launch supplies a fresh turn bearer. The A2A middleware compares it when a nonempty token is configured; the optional request field is not unconditional endpoint enforcement. `/configure` cannot authenticate with the token it delivers. NetworkPolicy and configured transport protections are separate controls; the additive preview ingress range also includes port 8088 (see [network-policy limitations](./infra-deployment.md#network-policy-model)).

## Security boundary

The sandbox pod has no database connection and does not hold an `ICheckpointStore`. It cannot retrieve ambient user credentials from Key Vault, CSI volumes, shared storage, or host configuration.

The A2A turn token is unique to the run. A token from one pod cannot authorize a turn against another pod.

## Source

- `apps/Agentweaver.Api/Sandbox/RemoteWorkflowAgentFactory.cs`
- `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs`
- `apps/Agentweaver.AgentHost/Program.cs`
- `apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs`

## Related reading

- [Sandbox pod execution](./sandbox-pod-execution.md)
- [AgentHost capability credential delivery](./agent-token-delivery.md)

<!-- diagram-context:canonical-agent-communication-a2a:start -->
<details id="diagram-context-canonical-agent-communication-a2a" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>A2A remotes a leaf turn, not the graph</td></tr>
<tr><td>takeaway</td><td>Setup and task cross to AgentHost; assistant output and structured events return.</td></tr>
<tr><td>Workflow graph</td><td>Workflow graph</td></tr>
<tr><td>Workflow graph</td><td>Host owns gates/checkpoints</td></tr>
<tr><td>Workflow graph</td><td>Five factory-created leaf types</td></tr>
<tr><td>RemoteAgentProxy</td><td>RemoteAgentProxy</td></tr>
<tr><td>RemoteAgentProxy</td><td>Build setup DataContent</td></tr>
<tr><td>RemoteAgentProxy</td><td>Task TextContent in same message</td></tr>
<tr><td>AgentHost bridge</td><td>AgentHost bridge</td></tr>
<tr><td>AgentHost bridge</td><td>message:stream over HTTP+JSON</td></tr>
<tr><td>AgentHost bridge</td><td>Apply per-turn context</td></tr>
<tr><td>Caller event pipeline</td><td>Caller event pipeline</td></tr>
<tr><td>Caller event pipeline</td><td>Decoded structured events</td></tr>
<tr><td>Caller event pipeline</td><td>Durable state outside pod</td></tr>
<tr><td>Proxy stream decoder</td><td>Proxy stream decoder</td></tr>
<tr><td>Proxy stream decoder</td><td>Output + RunEventDataPart</td></tr>
<tr><td>Proxy stream decoder</td><td>Check definitive turn end</td></tr>
<tr><td>Leaf runtime</td><td>Leaf runtime</td></tr>
<tr><td>Leaf runtime</td><td>Execute provider/tool loop</td></tr>
<tr><td>Leaf runtime</td><td>Stream updates and events</td></tr>
<tr><td>arrow-1</td><td>invoke</td></tr>
<tr><td>arrow-2</td><td>send</td></tr>
<tr><td>arrow-3</td><td>run</td></tr>
<tr><td>arrow-4</td><td>stream</td></tr>
<tr><td>arrow-5</td><td>append</td></tr>
<tr><td>note-0</td><td>Claim/configure is a separate lifecycle, completed before this exchange.</td></tr>
<tr><td>note-1</td><td>EOF alone is not successful completion; structured failures remain failures.</td></tr>
<tr><td>notes</td><td>Claim/configure is a separate lifecycle, completed before this exchange.; EOF alone is not successful completion; structured failures remain failures.</td></tr>
</tbody></table>
</details>
<!-- diagram-context:canonical-agent-communication-a2a:end -->

<!-- diagram-context:sandbox-pod-execution-fig6:start -->
<details id="diagram-context-sandbox-pod-execution-fig6" v-pre>
<summary>Diagram details and constraints</summary>
<table><thead><tr><th>Element</th><th>Contract</th></tr></thead><tbody>
<tr><td>title</td><td>Bind, reach standby, configure once</td></tr>
<tr><td>takeaway</td><td>Liveness precedes configuration; production delivers run credentials out of pod specs.</td></tr>
<tr><td>Prepare launch</td><td>Prepare launch</td></tr>
<tr><td>Prepare launch</td><td>Resolve provider and run context</td></tr>
<tr><td>Prepare launch</td><td>Mint fresh turn token</td></tr>
<tr><td>Claim warm pod</td><td>Claim warm pod</td></tr>
<tr><td>Claim warm pod</td><td>Create/adopt; omit spec.env</td></tr>
<tr><td>Claim warm pod</td><td>Wait Ready + bound pod name</td></tr>
<tr><td>Resolve and register</td><td>Resolve and register</td></tr>
<tr><td>Resolve and register</td><td>Pod mapping / token registry</td></tr>
<tr><td>Resolve and register</td><td>Resolve actual pod IP</td></tr>
<tr><td>GET /healthz</td><td>GET /healthz</td></tr>
<tr><td>GET /healthz</td><td>HTTP 200 standby</td></tr>
<tr><td>GET /healthz</td><td>Listener liveness, not turn ready</td></tr>
<tr><td>POST /configure</td><td>POST /configure</td></tr>
<tr><td>POST /configure</td><td>Identity / workspace / approvals</td></tr>
<tr><td>POST /configure</td><td>Copilot capability OR BYOK</td></tr>
<tr><td>AgentHost setup</td><td>AgentHost setup</td></tr>
<tr><td>AgentHost setup</td><td>One-time configuration</td></tr>
<tr><td>AgentHost setup</td><td>Effective workspace + HOME</td></tr>
<tr><td>Configuration guards</td><td>Configuration guards</td></tr>
<tr><td>Configuration guards</td><td>Second configure: 409</td></tr>
<tr><td>Configuration guards</td><td>Other routes: 503 before ready</td></tr>
<tr><td>Register effective endpoint</td><td>Register effective endpoint</td></tr>
<tr><td>Register effective endpoint</td><td>Return effective working directory</td></tr>
<tr><td>Register effective endpoint</td><td>Shared/local/private fallback</td></tr>
<tr><td>First A2A turn</td><td>First A2A turn</td></tr>
<tr><td>First A2A turn</td><td>Production sends turn bearer</td></tr>
<tr><td>First A2A turn</td><td>Equality guard when nonempty</td></tr>
<tr><td>arrow-1</td><td>launch</td></tr>
<tr><td>arrow-2</td><td>bound</td></tr>
<tr><td>arrow-3</td><td>poll</td></tr>
<tr><td>arrow-4</td><td>reachable</td></tr>
<tr><td>arrow-5</td><td>setup</td></tr>
<tr><td>arrow-6</td><td>ready</td></tr>
<tr><td>arrow-7</td><td>invoke</td></tr>
<tr><td>note-0</td><td>Top, middle and bottom rows are successive launch stages.</td></tr>
<tr><td>note-1</td><td>Repository / preview / broker credentials have separate purposes.</td></tr>
<tr><td>note-2</td><td>Optional schema fields do not imply unconditional endpoint enforcement.</td></tr>
<tr><td>notes</td><td>Top, middle and bottom rows are successive launch stages.; Repository / preview / broker credentials have separate purposes.; Optional schema fields do not imply unconditional endpoint enforcement.</td></tr>
</tbody></table>
</details>
<!-- diagram-context:sandbox-pod-execution-fig6:end -->
