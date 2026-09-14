# Scaling: pass 05 final correction

Final enlarged inspection exposed the temporary-ref label's proximity to the
unrelated fetch elbow. Moved only that label down 25 logical pixels. No node,
edge, port, relationship, text, color or other geometry changed; the publisher
asserts XML equivalence after removing that one label-offset point.

Opened the official Desktop 31.4.5 PNG enlarged and the separate A5 print PNG.
The fetch elbow is unobstructed; the temporary-ref label now sits on its own
lower return segment, above the arrow into Azure Files. It clears both the
adjacent heading and body text. Orientation, overlap and arrow defects: zero.
Earlier source/PNG passes remain unchanged.

## Final arrow trace

Retraced every connector in the final image. The unheaded shared-access bus
joins both callers and fans out to both persistent services. A2A is bidirectional;
execution is one-way into pod-local scratch. Fetch runs from shared Files to
the local checkout; temporary-ref publication returns to Files, never GitHub.
There is no sandbox-to-database edge, unintended junction, crossing, or
semantic revision-loop rail.

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
| `temp-ref` | `local` to `files` | Publish a non-force temporary writeback ref to the shared repository; `apps/Agentweaver.AgentHost/PodLocalWorkspaceManager.cs:328-348` | corrected |
