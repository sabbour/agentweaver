# canonical-sandbox-experience — pass-04

Mode: correction-only

## Actual PNG inspection
Opened `canonical-sandbox-experience-pass-04.png` (2× official draw.io export) and `canonical-sandbox-experience-pass-04-print.png` (100-units/inch A5 inspection page).
Both actual raster views were opened with the image-view tool; this is not an XML-only or editor-only review.
A5 landscape: one editable uncompressed 827 × 583 page; warm Fluent palette and Segoe UI.

## Findings / changes
Final correction-only export. Traced every numbered arrow against its evidence, exact source/target ports, direction, routing and crossing semantics. No new content or composition changes.

## Grounding and credits
See `evidence.md` for node/connector sources, the exclusive claim, supplied research provenance, symbol classifications and asset rights.

## Complete arrow trace
| ID | Source | Target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|---|
| e0 / 1 | n0: Authorized run | n1: SandboxClaim | start | apps/Agentweaver.Api/Endpoints/RunEndpoints.cs; docs/deep-dive/sandbox-pod-execution.md; apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs; apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs | authorized request right → claim left; target block arrowhead, no false junction dots | clean |
| e1 / 2 | n1: SandboxClaim | n2: AgentHost | configure | apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs; apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs; apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs | claim right → AgentHost left; target block arrowhead, no false junction dots | clean |
| e2 / 3 | n2: AgentHost | n4: Isolated workspace | tools | apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs; docs/deep-dive/sandbox.md:12-24; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs | AgentHost bottom → workspace top using lower central lane; target block arrowhead, no false junction dots | clean |
| e3 / 4 | n2: AgentHost | n3: Run experience | events / approvals | apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs; docs/deep-dive/sandbox.md:65-70; packages/Agentweaver.AgentRuntime/InMemoryToolApprovalGate.cs | AgentHost bottom → run experience right using separate upper/left lane; target block arrowhead, no false junction dots | clean |
| e4 / 5 | n4: Isolated workspace | n5: Preview or review | work artifacts | docs/deep-dive/sandbox.md:12-24; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-330; docs/deep-dive/sandbox-pod-execution.md | workspace right → preview/review left; resulting work, not unconditional preview readiness; target block arrowhead, no false junction dots | clean |
