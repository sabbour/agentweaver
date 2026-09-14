# canonical-sandbox-experience — pass 05

Corrected the source-copy encoding defect without changing the intended labels, content, geometry or arrows. Explicit UTF-8 restores the previously inspected pass-3 glyphs. Every connector was traced again on the newly exported PNG.

Input PNG was inspected before this pass. This pass has a distinct editable source and a fresh official PNG export. Its actual output PNG was opened enlarged, and its print-normalized image was opened in the per-pass contact sheet (preview pass 6 individually). No editor/XML-only inspection substitutes for the PNG.

Remaining orientation defects: 0; overlap/label-legibility defects: 0; arrow defects: 0.

## Complete every-arrow trace

| ID | Source → target | Relationship | Endpoint / route inspection | Evidence | Result |
|---|---|---|---|---|---|
| e0 | n0 → n1 | start | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Endpoints/RunEndpoints.cs; docs/deep-dive/sandbox-pod-execution.md; apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs; apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs | clean |
| e1 | n1 → n2 | configure | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Sandbox/KubernetesSandboxExecutor.cs; apps/Agentweaver.Api/Sandbox/SandboxClaimConventions.cs; apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs | clean |
| e2 | n2 → n4 | tools | Exit (0.65,1); entry (0.65,0); waypoints [('709.6', '288'), ('447.6', '288')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs; docs/deep-dive/sandbox.md:12-24; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs | clean |
| e3 | n2 → n3 | events / approvals | Exit (0.35,1); entry (1,0.3); waypoints [('642.4', '270'), ('282', '270'), ('282', '351.0')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.AgentHost/Program.cs:232-285; apps/Agentweaver.AgentHost/AgentHostStartupService.cs; docs/deep-dive/sandbox.md:65-70; packages/Agentweaver.AgentRuntime/InMemoryToolApprovalGate.cs | clean |
| e4 | n4 → n5 | work artifacts | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | docs/deep-dive/sandbox.md:12-24; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs; apps/Agentweaver.Api/Sandbox/Preview/SandboxPreviewService.cs:308-330; docs/deep-dive/sandbox-pod-execution.md | clean |

All arrows terminate on the intended component cards, not group surfaces. No logical junction dots or dashed revision/return rails are needed by these relationships. Pass-4 glyph defects are recorded above independently of arrow topology and are corrected before promotion.
