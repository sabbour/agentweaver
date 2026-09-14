# reference-a2a-fig1: pass 04

Correction-only review; no defects requiring XML changes. A separately saved source was exported and inspected again. No expansion or decorative growth.

The official PNG was opened at enlarged/native resolution and its `-print.png` A5-scale companion was opened separately. Checked orientation, hierarchy, text wrapping, native symbols, boundary containment, connector ports, arrowheads, labels and crossings.

Remaining defects: orientation 0; overlap 0; arrows 0.

## Final arrow trace

Every connector was followed visually from its source port to target port. Arrowheads match the request/response or one-way relationship, labels stay clear of cards, and there are no ambiguous crossings. Checkpoint persistence never originates inside AgentHost; no sandbox-to-PostgreSQL edge exists. Scaling's three unheaded junction segments are a shared-access bus, not service hops. No semantic revision-return rail is needed in either topology.

| ID | Source to target | Relationship and evidence | Result |
| --- | --- | --- | --- |
| `configure-call` | `api` to `configure` | health / configure; `apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs:635-707` | clean |
| `turn-stream` | `worker` to `turn` | turn / ordered stream; `packages/Agentweaver.AgentRuntime/Workflow/RemoteAgentProxy.cs:174-204,307-329` | clean |
| `checkpoint-write` | `worker` to `checkpoint` | Worker owns checkpoint manager; `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:181-188` | clean |
| `checkpoint-store` | `checkpoint` to `database` | save / resume; `apps/Agentweaver.Api/Runs/RunWorkflowFactory.cs:181-188` | clean |
| `bridge-call` | `turn` to `bridge` | Authorized turn reaches bridge and purpose router; `apps/Agentweaver.AgentHost/Program.cs:145-178,386-401` | clean |
