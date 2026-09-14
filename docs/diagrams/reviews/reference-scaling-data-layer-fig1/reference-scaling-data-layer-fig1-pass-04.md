# reference-scaling-data-layer-fig1: pass 04

Correction-only review; no defects requiring XML changes. A separately saved source was exported and inspected again. No expansion or decorative growth.

The official PNG was opened at enlarged/native resolution and its `-print.png` A5-scale companion was opened separately. Checked orientation, hierarchy, text wrapping, native symbols, boundary containment, connector ports, arrowheads, labels and crossings.

Remaining defects: orientation 0; overlap 0; arrows 0.

## Final arrow trace

Every connector was followed visually from its source port to target port. Arrowheads match the request/response or one-way relationship, labels stay clear of cards, and there are no ambiguous crossings. Checkpoint persistence never originates inside AgentHost; no sandbox-to-PostgreSQL edge exists. Scaling's three unheaded junction segments are a shared-access bus, not service hops. No semantic revision-return rail is needed in either topology.

| ID | Source to target | Relationship and evidence | Result |
| --- | --- | --- | --- |
| `api-access` | `api` to `caller-junction` | API shares persistent services; `k8s/base/api-deployment.yaml:338-354,404-409` | clean |
| `worker-access` | `worker` to `caller-junction` | Worker shares persistent services; `k8s/base/worker-deployment.yaml:146-160,256-260` | clean |
| `shared-access` | `caller-junction` to `access-junction` | Both platform roles access both persistent services; `k8s/base/api-deployment.yaml:338-354; k8s/base/worker-deployment.yaml:146-160` | clean |
| `database-access` | `access-junction` to `postgres` | state / leases; `apps/Agentweaver.Api/Program.cs:1027-1074` | clean |
| `workspace-access` | `access-junction` to `files` | workspace; `k8s/base/pvc-workspace.yaml:34-51` | clean |
| `a2a-control` | `worker` to `agenthost` | A2A; `k8s/base/networkpolicy-agenthost.yaml:35-48; apps/Agentweaver.AgentHost/A2ATurnBridgeAgent.cs:326-353` | clean |
| `local-execution` | `agenthost` to `local` | execute; `k8s/base/sandbox-template-agenthost.yaml:151-160,332-371` | clean |
| `source-fetch` | `files` to `local` | fetch; `apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:98-135` | clean |
| `temp-ref` | `local` to `files` | Publish a non-force temporary writeback ref to the shared repository; `apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:328-348` | clean |

## Final-inspection addendum

The later enlarged publication inspection found one label-placement overlap risk: `temp ref` sat alongside the unrelated fetch elbow. This supersedes the earlier zero-overlap assessment. Pass five corrects the offset; this pass's source and PNG remain unchanged.
