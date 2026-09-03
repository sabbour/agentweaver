# A2A bridge

## Purpose

Agentweaver keeps workflow orchestration on worker pods. It sends each leaf agent turn to AgentHost over A2A HTTP+JSON. The orchestration graph, approval gates, checkpoints, and durable events do not cross this boundary.

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

## Warm-pool configuration

AgentHost starts in standby without a run identity. When the API claims a warm pod, it makes one `POST /configure` request. The request includes run, project, agent, and purpose identity; shared and local workspace descriptors; turn authentication; approval settings; and provider configuration.

The provider payload is either `copilotCredential` or `byokProviderConfiguration`. Repository, preview, and MCP broker credentials are optional and purpose-scoped.

`/configure` is protected by the in-cluster NetworkPolicy because it delivers the turn credential. It accepts one configuration per pod. After configuration, `/healthz` reports ready and AgentHost accepts only A2A turns with the configured per-run bearer token.

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
