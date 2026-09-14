# canonical-sandbox-boundary — pass-04

Mode: correction-only

## Actual PNG inspection
Opened `canonical-sandbox-boundary-pass-04.png` (2× official draw.io export) and `canonical-sandbox-boundary-pass-04-print.png` (100-units/inch A5 inspection page).
Both actual raster views were opened with the image-view tool; this is not an XML-only or editor-only review.
A5 landscape: one editable uncompressed 827 × 583 page; warm Fluent palette and Segoe UI.

## Findings / changes
Final correction-only export. Traced every numbered arrow against its evidence, exact source/target ports, direction, routing and crossing semantics. No new content or composition changes.

## Grounding and credits
See `evidence.md` for node/connector sources, the exclusive claim, supplied research provenance, symbol classifications and asset rights.

## Complete arrow trace
| ID | Source | Target | Relationship | Evidence | Direction / endpoints / route | Result |
|---|---|---|---|---|---|---|
| e0 / 1 | n0: Model tool request | n1: Governance | governed calls | packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:1850-1920,2079-2139; packages/Agentweaver.AgentRuntime/SandboxGovernance.cs:114-162 | model request right → governance left; only remaining governed calls after the stated exceptions; target block arrowhead, no false junction dots | clean |
| e1 / 2 | n1: Governance | n2: Registered tools | both allow | packages/Agentweaver.AgentRuntime/SandboxGovernance.cs:114-162; packages/Agentweaver.AgentRuntime/SandboxGovernance.cs; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs | governance right → registered tools left; both checks must allow; target block arrowhead, no false junction dots | clean |
| e2 / 3 | n2: Registered tools | n3: Workspace boundary | file operation | packages/Agentweaver.AgentRuntime/SandboxGovernance.cs; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs; packages/Agentweaver.SandboxFs; docs/deep-dive/sandbox.md:72-110 | registered tools bottom → workspace right through left gutter; target block arrowhead, no false junction dots | clean |
| e3 / 4 | n2: Registered tools | n4: Execution boundary | run_command | packages/Agentweaver.AgentRuntime/SandboxGovernance.cs; packages/Agentweaver.AgentTools/Tools/RunCommandTool.cs; apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs; docs/deep-dive/sandboxed-execution.md:20-40 | registered tools bottom → execution top through independent lane; bridge at file-path crossing; target block arrowhead, no false junction dots | clean |
| e4 / 5 | n4: Execution boundary | n5: Credential handling | eligible git / gh | apps/Agentweaver.Api/Sandbox/SandboxExecutorRouter.cs; docs/deep-dive/sandboxed-execution.md:20-40; apps/Agentweaver.AgentHost/AgentHostRuntimeState.cs:92-95,172-174; packages/Agentweaver.AgentRuntime/CopilotAIAgent.cs:446-453; docs/deep-dive/sandboxed-execution.md:113-130 | execution right → credential handling left; eligible direct git/gh only; target block arrowhead, no false junction dots | clean |
