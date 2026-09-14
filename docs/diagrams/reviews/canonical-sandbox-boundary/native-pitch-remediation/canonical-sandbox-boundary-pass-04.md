# canonical-sandbox-boundary — pass 04

Separate no-layout-change export and complete arrow trace. PNG inspection caught Windows default-decoding corruption in Unicode annotation glyphs during source copying. This pass is retained as historical evidence, NOT the final promoted source. Diagrams containing only ASCII had no glyph defect.

Input PNG was inspected before this pass. This pass has a distinct editable source and a fresh official PNG export. Its actual output PNG was opened enlarged, and its print-normalized image was opened in the per-pass contact sheet (preview pass 6 individually). No editor/XML-only inspection substitutes for the PNG.

Remaining orientation defects: 0; overlap/label-legibility defects: 0; arrow defects: 0.

## Complete every-arrow trace

| ID | Source → target | Relationship | Endpoint / route inspection | Evidence | Result |
|---|---|---|---|---|---|
| e0 | n0 → n1 | governed calls | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:1850-1920,2079-2139; packages/Agentweaver.AgentRuntime/SandboxGovernance.cs:114-162 | clean |
| e1 | n1 → n2 | both allow | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | packages/Agentweaver.AgentRuntime/SandboxGovernance.cs:114-162; packages/Agentweaver.AgentRuntime/SandboxGovernance.cs; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs | clean |
| e2 | n2 → n3 | file operation | Exit (0.65,1); entry (1,0.3); waypoints [('709.6', '288'), ('282', '288'), ('282', '351.0')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | packages/Agentweaver.AgentRuntime/SandboxGovernance.cs; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs; packages/Agentweaver.SandboxFs; docs/deep-dive/sandbox.md:72-110 | clean |
| e3 | n2 → n4 | run_command | Exit (0.35,1); entry (0.35,0); waypoints [('642.4', '270'), ('380.4', '270')]. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | packages/Agentweaver.AgentRuntime/SandboxGovernance.cs; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs; apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs; docs/deep-dive/sandboxed-execution.md:20-40 | clean |
| e4 | n4 → n5 | eligible git / gh | Exit (1,0.5); entry (0,0.5); waypoints direct orthogonal. Target arrowhead verified. Gutters clear; crossings bridge, not junction. Number matches the visible connector key. | apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs; docs/deep-dive/sandboxed-execution.md:20-40; apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs:92-95,172-174; packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:446-453; docs/deep-dive/sandboxed-execution.md:113-130 | clean |

All arrows terminate on the intended component cards, not group surfaces. No logical junction dots or dashed revision/return rails are needed by these relationships. Pass-4 glyph defects are recorded above independently of arrow topology and are corrected before promotion.
