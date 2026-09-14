# canonical-pod-process-boundaries: pitch inspection

Takeaway: Kata isolates the pod; the executor container separates model processes from AgentHost.

Audience: operators and contributors reading the execution deep dives.
One editable A5 landscape page. Exported with draw.io Desktop 31.4.5.
Opened enlarged and in the saved A5-scale contact sheets.
Sparse generic cards are a sketch, not an approved native-notation composition.
Missing hierarchy, relationship detail and process boundaries require redesign.

Evidence: `k8s/base/sandbox-template-agenthost.yaml:58-61,151-162,269-292,341-399; packages/Agentweaver.SandboxExec/PodExec/PodExecServer.cs:212-242; packages/Agentweaver.SandboxExec/KataBwrapExecutor.cs:609-655,892-931; tests/Agentweaver.Tests/KataBwrapExecutorTests.cs:29-47,110-164`.

Fluent palette/template/library: repository-owned docs/diagrams/drawio assets.
Native symbol proposals and source-backed content: content-models.json and the
three research reports in the canonical-pod-process-boundaries review directory.
